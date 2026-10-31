using Dapper;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;

namespace GameServer.Tests.World;

/// <summary>
/// Harvesting against a real PostgreSQL (spec 5.2, WP-M3-5; rule 5, R-7.1-01/02; CLAUDE.md: double submit, retry, crash in
/// the middle, concurrent access). A harvest is a class 2 add (reason <c>Harvest</c>) that rides the character save: the
/// stack, the operation key and the audit row commit in one transaction or not at all. The world-level counterparts (the
/// answer after the commit, the node, two harvesters in one tick) are <see cref="HarvestWorldTests"/>.
/// </summary>
public sealed class HarvestPersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(12, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly ItemRules _items = ItemRules.From(TestCombat.Tables())!;
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private InventoryRepository _inventory = null!;
    private long _nextAccount = 1;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _inventory = new InventoryRepository(_realmDb);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private async Task<CharacterRecord> CreateAsync()
    {
        long account = _nextAccount++;
        var created = await _characters.CreateAsync(new NewCharacter(account, 0, $"Miner{account}", "warrior", Spawn, Guid.NewGuid()));
        return created.Character!;
    }

    private async Task<InventoryService> AttachAsync(long characterId)
    {
        var service = new InventoryService(_items, events: null);
        Assert.True(service.Attach(characterId, await _inventory.LoadAsync(characterId)));
        return service;
    }

    /// <summary>One harvest applied in memory as the world does it: <paramref name="count"/> T1 ore added (reason Harvest).</summary>
    private (InventoryBatch Batch, Guid SaveId) Harvest(InventoryService service, CharacterRecord character, string opKey, int count = 2)
    {
        Assert.True(_items.TryGetItem("m3.oreT1", out var ore));
        Assert.Equal(count, service.TryAdd(character.CharacterId, ore, 0, count, opKey, InventoryReason.Harvest, "harvest").Added);
        var saveId = Guid.NewGuid();
        return (service.TakeBatch(character.CharacterId, saveId)!, saveId);
    }

    private async Task<int> CountAsync(string table, long characterId)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table} WHERE character_id = @characterId", new { characterId });
    }

    private async Task<int> OreAsync(long characterId) => (await _inventory.LoadAsync(characterId)).Sum(r => r.Count);

    [Fact]
    public async Task A_harvest_commits_the_ore_its_key_and_its_audit_row_in_one_transaction()
    {
        var character = await CreateAsync();
        var service = await AttachAsync(character.CharacterId);
        var (batch, saveId) = Harvest(service, character, "harvest:run:7:1:1");

        var result = await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(2, await OreAsync(character.CharacterId));
        Assert.Contains("harvest:run:7:1:1", await _inventory.OpKeysAsync(character.CharacterId));
        var audit = (await _inventory.AuditAsync(character.CharacterId)).Single(a => a.OpKey == "harvest:run:7:1:1");
        Assert.Equal((AuditKind.Add, 2, "m3.oreT1"), (audit.Kind, audit.CountDelta, audit.DefId));
        await using var connection = await _realmDb.OpenConnectionAsync();
        Assert.Equal("Harvest", await connection.ExecuteScalarAsync<string>(
            "SELECT reason FROM inventory_ops WHERE op_key = 'harvest:run:7:1:1'"));
    }

    [Fact]
    public async Task A_double_submit_of_one_save_harvests_once()
    {
        var character = await CreateAsync();
        var service = await AttachAsync(character.CharacterId);
        var (batch, saveId) = Harvest(service, character, "harvest:run:7:1:2");

        var results = await Task.WhenAll(
            _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch),
            _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(2, await OreAsync(character.CharacterId));
        Assert.Equal(1, await CountAsync("inventory_ops", character.CharacterId));
    }

    [Fact]
    public async Task A_retry_after_an_unknown_outcome_that_did_commit_harvests_nothing_more()
    {
        var character = await CreateAsync();
        var service = await AttachAsync(character.CharacterId);
        var (batch, saveId) = Harvest(service, character, "harvest:run:7:1:3");
        await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch);

        var retry = await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch);

        Assert.Equal(SaveStatus.Saved, retry.Status);
        Assert.Equal(2, await OreAsync(character.CharacterId));
        Assert.Equal(1, await CountAsync("audit_log", character.CharacterId));
    }

    [Fact]
    public async Task A_crash_in_the_middle_writes_no_ore_and_the_retry_applies_once()
    {
        var character = await CreateAsync();
        var service = await AttachAsync(character.CharacterId);
        var (batch, saveId) = Harvest(service, character, "crash:harvest:4");
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_harvest_crash() RETURNS trigger AS $$
                BEGIN IF NEW.op_key LIKE 'crash:%' THEN RAISE EXCEPTION 'simulated crash'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_harvest_crash BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION test_harvest_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() =>
            _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch));

        Assert.Equal(character.Version, (await _characters.LoadAsync(character.CharacterId))!.Version);
        Assert.Equal(0, await OreAsync(character.CharacterId));
        Assert.Equal(0, await CountAsync("inventory_ops", character.CharacterId));

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_harvest_crash ON audit_log");
        var retry = await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch);

        Assert.Equal(SaveStatus.Saved, retry.Status);
        Assert.Equal(2, await OreAsync(character.CharacterId));
    }

    [Fact]
    public async Task Two_worlds_saving_a_harvest_of_one_character_at_once_have_exactly_one_winner()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var a = await AttachAsync(id);
        var b = await AttachAsync(id);
        var harvestA = Harvest(a, character, "world-a:harvest");
        var harvestB = Harvest(b, character, "world-b:harvest", count: 3);

        var results = await Task.WhenAll(
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, harvestA.SaveId, harvestA.Batch)),
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, harvestB.SaveId, harvestB.Batch)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        Assert.Contains(await OreAsync(id), new[] { 2, 3 }); // one harvest landed, never both
    }
}
