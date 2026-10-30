using Dapper;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;

namespace GameServer.Tests.Items;

/// <summary>
/// Consumable use against a real PostgreSQL (spec 3.4 consumable use, WP-M3-3; rule 5, R-7.1-01/02; CLAUDE.md: double submit,
/// retry, crash in the middle, concurrent access). A use is a class 2 removal (reason <c>ItemUsed</c>) that rides the
/// character save together with the raised toxicity: the row update, the operation key, the slot and the audit row commit
/// in one transaction or not at all.
/// </summary>
public sealed class ItemUsePersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

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

    /// <summary>A character with three small HP potions in Normal bag slot 0, committed.</summary>
    private async Task<CharacterRecord> CreateWithPotionsAsync()
    {
        long account = _nextAccount++;
        var created = await _characters.CreateAsync(new NewCharacter(account, 0, $"Drinker{account}", "warrior", Spawn, Guid.NewGuid()));
        var character = created.Character!;
        var service = await AttachAsync(character.CharacterId);
        Assert.True(_items.TryGetItem("m2.potionHpSmall", out var potion));
        service.TryAdd(character.CharacterId, potion, 0, 3, "buy:seed", InventoryReason.VendorPurchase, "test");
        var saveId = Guid.NewGuid();
        var saved = await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId,
            service.TakeBatch(character.CharacterId, saveId));
        Assert.Equal(SaveStatus.Saved, saved.Status);
        return (await _characters.LoadAsync(character.CharacterId))!;
    }

    private async Task<InventoryService> AttachAsync(long characterId)
    {
        var service = new InventoryService(_items, events: null);
        Assert.True(service.Attach(characterId, await _inventory.LoadAsync(characterId)));
        return service;
    }

    /// <summary>One use applied in memory as the world does it: one unit removed (ItemUsed) and the toxicity raised.</summary>
    private static (InventoryBatch Batch, CharacterState State, Guid SaveId) Use(InventoryService service, CharacterRecord character,
        string opKey)
    {
        Assert.True(service.TryRemove(character.CharacterId, 1, 0, 1, opKey, InventoryReason.ItemUsed, "use"));
        var saveId = Guid.NewGuid();
        var state = character.State with { ToxicityMilli = 25_000, ConsumptionAtUtcMs = 1 };
        return (service.TakeBatch(character.CharacterId, saveId)!, state, saveId);
    }

    private async Task<int> CountAsync(string table, long characterId)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table} WHERE character_id = @characterId", new { characterId });
    }

    private async Task<int> PotionsAsync(long characterId) => (await _inventory.LoadAsync(characterId)).Sum(r => r.Count);

    [Fact]
    public async Task A_use_commits_the_removal_its_key_its_audit_row_and_the_toxicity_in_one_transaction()
    {
        var character = await CreateWithPotionsAsync();
        var service = await AttachAsync(character.CharacterId);
        var (batch, state, saveId) = Use(service, character, "use:run:7:1:1");

        var result = await _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, batch);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(2, await PotionsAsync(character.CharacterId));
        Assert.Contains("use:run:7:1:1", await _inventory.OpKeysAsync(character.CharacterId));
        var audit = (await _inventory.AuditAsync(character.CharacterId)).Single(a => a.OpKey == "use:run:7:1:1");
        Assert.Equal((AuditKind.Remove, -1), (audit.Kind, audit.CountDelta));
        Assert.Equal(25_000, (await _characters.LoadAsync(character.CharacterId))!.State.ToxicityMilli);
    }

    [Fact]
    public async Task A_double_submit_of_one_save_uses_one_potion()
    {
        var character = await CreateWithPotionsAsync();
        var service = await AttachAsync(character.CharacterId);
        var (batch, state, saveId) = Use(service, character, "use:run:7:1:2");

        var first = _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, batch);
        var second = _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, batch);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(2, await PotionsAsync(character.CharacterId));
        Assert.Equal(2, await CountAsync("inventory_ops", character.CharacterId)); // the seed and one use
    }

    [Fact]
    public async Task A_retry_after_an_unknown_outcome_that_did_commit_uses_nothing_more()
    {
        var character = await CreateWithPotionsAsync();
        var service = await AttachAsync(character.CharacterId);
        var (batch, state, saveId) = Use(service, character, "use:run:7:1:3");
        await _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, batch);

        var retry = await _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, batch);

        Assert.Equal(SaveStatus.Saved, retry.Status);
        Assert.Equal(2, await PotionsAsync(character.CharacterId));
        Assert.Equal(2, await CountAsync("audit_log", character.CharacterId));
    }

    [Fact]
    public async Task A_crash_in_the_middle_writes_neither_the_removal_nor_the_toxicity_and_the_retry_applies_once()
    {
        var character = await CreateWithPotionsAsync();
        var service = await AttachAsync(character.CharacterId);
        var (batch, state, saveId) = Use(service, character, "crash:use:4");
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_use_crash() RETURNS trigger AS $$
                BEGIN IF NEW.op_key LIKE 'crash:%' THEN RAISE EXCEPTION 'simulated crash'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_use_crash BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION test_use_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() =>
            _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, batch));

        var loaded = (await _characters.LoadAsync(character.CharacterId))!;
        Assert.Equal((character.Version, 0), (loaded.Version, loaded.State.ToxicityMilli));
        Assert.Equal(3, await PotionsAsync(character.CharacterId));

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_use_crash ON audit_log");
        var retry = await _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, batch);

        Assert.Equal(SaveStatus.Saved, retry.Status);
        Assert.Equal(2, await PotionsAsync(character.CharacterId));
        Assert.Equal(25_000, (await _characters.LoadAsync(character.CharacterId))!.State.ToxicityMilli);
    }

    [Fact]
    public async Task Two_worlds_using_the_same_potion_at_once_have_exactly_one_winner()
    {
        var character = await CreateWithPotionsAsync();
        long id = character.CharacterId;
        var a = await AttachAsync(id);
        var b = await AttachAsync(id);
        var useA = Use(a, character, "world-a:use");
        var useB = Use(b, character, "world-b:use");

        var results = await Task.WhenAll(
            Task.Run(() => _characters.SaveAsync(id, useA.State, character.Version, useA.SaveId, useA.Batch)),
            Task.Run(() => _characters.SaveAsync(id, useB.State, character.Version, useB.SaveId, useB.Batch)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        Assert.Equal(2, await PotionsAsync(id)); // one potion used, never two
    }
}
