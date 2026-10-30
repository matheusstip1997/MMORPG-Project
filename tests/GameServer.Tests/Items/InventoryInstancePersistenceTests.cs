using Dapper;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;
using Shared.Simulation.Items;

namespace GameServer.Tests.Items;

/// <summary>
/// Item instances, containers and equipment against a real PostgreSQL (WP-M2-1, migration V0004; spec 4.1 M2 item model,
/// rule 5, R-7.1-01/02; CLAUDE.md: double submit, retry, crash in the middle, concurrent access for every transaction).
/// <c>Move(container)</c>, equip and unequip each ride on the class 2 character save like the M1 bag moves; a login reads
/// the instances back with their ids, flags and properties, and the instance counter never gives an id twice.
/// </summary>
public sealed class InventoryInstancePersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);
    private const byte Normal = 1, Safe = 2, Equipped = 5;
    private const string Sword = "m2.trainingSword", Cap = "m2.leatherCap";

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

    public enum Operation
    {
        MoveBetweenContainers,
        Equip,
        Unequip,
    }

    private ItemDef Item(string id)
    {
        Assert.True(_items.TryGetItem(id, out var item));
        return item;
    }

    private async Task<CharacterRecord> CreateAsync()
    {
        long account = _nextAccount++;
        var result = await _characters.CreateAsync(new NewCharacter(account, 0, $"Hero{account}", "warrior", Spawn, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, result.Status);
        return result.Character!;
    }

    private async Task<InventoryService> AttachAsync(long characterId, int level = 1)
    {
        var service = new InventoryService(_items, events: null);
        Assert.True(service.Attach(characterId, await _inventory.LoadInventoryAsync(characterId), "warrior", level));
        return service;
    }

    private static InventoryBatch Take(InventoryService service, long characterId, Guid saveId) =>
        service.TakeBatch(characterId, saveId) ?? throw new InvalidOperationException("no batch");

    private async Task<long> SaveAsync(InventoryService service, long characterId, long version)
    {
        var saveId = Guid.NewGuid();
        var result = await _characters.SaveAsync(characterId, Spawn, version, saveId, Take(service, characterId, saveId));
        Assert.Equal(SaveStatus.Saved, result.Status);
        service.Committed(characterId, saveId);
        return result.Version;
    }

    private async Task<int> CountAsync(string table, long characterId)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table} WHERE character_id = @characterId", new { characterId });
    }

    private async Task<ItemInstanceRow> SavedSwordAsync(long characterId) =>
        Assert.Single((await _inventory.LoadInventoryAsync(characterId)).Instances, i => i.DefId == Sword);

    /// <summary>
    /// A character with a saved sword in the Safe bag (equipped for an unequip), and the batch of the operation under test,
    /// not yet saved. Returns the version the batch must be saved at and where the sword ends up.
    /// </summary>
    private async Task<(CharacterRecord Character, InventoryService Service, long Version, InventoryLocation Expected,
        InventoryLocation Before)> PrepareAsync(Operation operation)
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var service = await AttachAsync(id);
        Assert.Equal(1, service.TryAdd(id, Item(Sword), 1, 1, "setup:add", InventoryReason.Admin, "test").Added);
        var safe = new InventoryLocation(Safe, 0);
        var main = new InventoryLocation(Equipped, 0);
        if (operation == Operation.Unequip)
            Assert.True(service.TryEquip(id, safe, 0, "setup:equip").Applied);
        long version = await SaveAsync(service, id, character.Version);

        var (from, to) = operation switch
        {
            Operation.MoveBetweenContainers => (safe, new InventoryLocation(Safe, 7)),
            Operation.Equip => (safe, main),
            _ => (main, new InventoryLocation(Safe, 4)),
        };
        var result = service.TryMove(id, from, to, $"op:{operation}", InventoryReason.PlayerMove);
        Assert.True(result.Applied, result.Reason.ToString());
        return (character, service, version, to, from);
    }

    private async Task AssertSwordAtAsync(long characterId, InventoryLocation at)
    {
        var sword = await SavedSwordAsync(characterId);
        Assert.Equal((at.Container, at.Slot), ((byte)sword.Container, sword.Slot));
        Assert.Equal(ItemInstanceIds.Make(characterId, 1), sword.InstanceId);
    }

    [Theory]
    [InlineData(Operation.MoveBetweenContainers)]
    [InlineData(Operation.Equip)]
    [InlineData(Operation.Unequip)]
    public async Task A_double_submit_of_one_save_applies_the_operation_once(Operation operation)
    {
        var (character, service, version, expected, _) = await PrepareAsync(operation);
        long id = character.CharacterId;
        int auditBefore = await CountAsync("audit_log", id);
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _characters.SaveAsync(id, Spawn, version, saveId, batch)));

        Assert.All(results, r => Assert.Equal(new SaveResult(SaveStatus.Saved, version + 1), r));
        await AssertSwordAtAsync(id, expected);
        Assert.Equal(1, await CountAsync("item_instances", id));
        Assert.Equal(auditBefore + batch.Audit.Count, await CountAsync("audit_log", id));
        Assert.Contains($"op:{operation}", await _inventory.OpKeysAsync(id));
    }

    [Theory]
    [InlineData(Operation.MoveBetweenContainers)]
    [InlineData(Operation.Equip)]
    [InlineData(Operation.Unequip)]
    public async Task A_retry_after_an_unknown_outcome_that_did_commit_writes_nothing_more(Operation operation)
    {
        var (character, service, version, expected, _) = await PrepareAsync(operation);
        long id = character.CharacterId;
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);
        await _characters.SaveAsync(id, Spawn, version, saveId, batch);
        int audit = await CountAsync("audit_log", id);

        var retry = await _characters.SaveAsync(id, Spawn, version, saveId, batch);

        Assert.Equal(new SaveResult(SaveStatus.Saved, version + 1), retry);
        Assert.Equal(audit, await CountAsync("audit_log", id));
        await AssertSwordAtAsync(id, expected);
    }

    [Theory]
    [InlineData(Operation.MoveBetweenContainers)]
    [InlineData(Operation.Equip)]
    [InlineData(Operation.Unequip)]
    public async Task A_crash_in_the_middle_writes_nothing_and_the_retry_applies_once(Operation operation)
    {
        var (character, service, version, expected, before) = await PrepareAsync(operation);
        long id = character.CharacterId;
        int ops = await CountAsync("inventory_ops", id);
        int audit = await CountAsync("audit_log", id);
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);
        // The instance write fails after the row update, the operation key and the stack slots were written: a crash mid-way.
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_crash() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_crash BEFORE INSERT ON item_instances FOR EACH ROW EXECUTE FUNCTION test_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveAsync(id, Spawn, version, saveId, batch));

        Assert.Equal(version, (await _characters.LoadAsync(id))!.Version);
        await AssertSwordAtAsync(id, before);
        Assert.Equal(ops, await CountAsync("inventory_ops", id));
        Assert.Equal(audit, await CountAsync("audit_log", id));

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_crash ON item_instances");
        var retry = await _characters.SaveAsync(id, Spawn, version, saveId, batch);

        Assert.Equal(SaveStatus.Saved, retry.Status);
        await AssertSwordAtAsync(id, expected);
        Assert.Equal(ops + 1, await CountAsync("inventory_ops", id));
    }

    [Theory]
    [InlineData(Operation.MoveBetweenContainers)]
    [InlineData(Operation.Equip)]
    [InlineData(Operation.Unequip)]
    public async Task Concurrent_writers_of_one_character_have_exactly_one_winner(Operation operation)
    {
        var (character, service, version, expected, before) = await PrepareAsync(operation);
        long id = character.CharacterId;
        // A second world that also thinks it owns the character moves the same sword somewhere else from the same version.
        var other = await AttachAsync(id);
        var elsewhere = new InventoryLocation(Safe, 12);
        Assert.True(other.TryMove(id, before, elsewhere, "other:move", InventoryReason.PlayerMove).Applied);
        var saveA = Guid.NewGuid();
        var saveB = Guid.NewGuid();
        var batchA = Take(service, id, saveA);
        var batchB = Take(other, id, saveB);

        var results = await Task.WhenAll(
            Task.Run(() => _characters.SaveAsync(id, Spawn, version, saveA, batchA)),
            Task.Run(() => _characters.SaveAsync(id, Spawn, version, saveB, batchB)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        await AssertSwordAtAsync(id, results[0].Status == SaveStatus.Saved ? expected : elsewhere);
        Assert.Equal(1, await CountAsync("item_instances", id));
    }

    [Fact]
    public async Task A_login_reads_back_every_container_with_instance_ids_flags_and_properties_and_ids_never_repeat()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var service = await AttachAsync(id);
        service.TryAdd(id, Item(Sword), 2, 1, "add:sword", InventoryReason.Admin, "test");
        service.TryAdd(id, Item(Cap), 0, 1, "add:cap", InventoryReason.Admin, "test");
        service.TryAdd(id, Item("m1.demonHorn"), 0, 4, "add:horn", InventoryReason.Admin, "test");
        Assert.True(service.TryEquip(id, new InventoryLocation(Safe, 0), 0, "equip:sword").Applied);
        Assert.True(service.TryMove(id, new InventoryLocation(Normal, 0), new InventoryLocation(3, 2), "move:horn", InventoryReason.PlayerMove).Applied);
        long version = await SaveAsync(service, id, character.Version);

        var load = await _inventory.LoadInventoryAsync(id);
        var reloaded = new InventoryService(_items, events: null);
        Assert.True(reloaded.Attach(id, load, "warrior", 1));

        Assert.Equal(2, load.InstanceSeq);
        var sword = Assert.Single(load.Instances, i => i.DefId == Sword);
        Assert.Equal((ItemInstanceIds.Make(id, 1), (int)Equipped, 0, 2, false, false),
            (sword.InstanceId, sword.Container, sword.Slot, sword.Rarity, sword.Stolen, sword.NonTradable));
        using (var properties = System.Text.Json.JsonDocument.Parse(sword.PropertiesJson))
        {
            var root = properties.RootElement;
            Assert.Equal((2, 0, 0, id), (root.GetProperty("v").GetInt32(), root.GetProperty("enchant").GetInt32(),
                root.GetProperty("gems").GetArrayLength(), root.GetProperty("originalOwner").GetInt64()));
        }
        Assert.Equal(new ItemStackRow(3, 2, "m1.demonHorn", 0, false, 4), Assert.Single(load.Stacks));
        var before = service.CurrentInventory(id);
        var after = reloaded.CurrentInventory(id);
        for (int index = 0; index < before.ContainerCount; index++)
        {
            for (int slot = 0; slot < before.BagAt(index).SlotCount; slot++)
                Assert.Equal(before.BagAt(index)[slot], after.BagAt(index)[slot]);
        }
        Assert.Single(reloaded.GetEquippedBonuses(id)); // the stats see the saved equipped set at login

        // The next instance continues the counter; the old ids are never handed out again.
        reloaded.TryAdd(id, Item(Cap), 0, 1, "add:cap2", InventoryReason.Admin, "test");
        await SaveAsync(reloaded, id, version);
        var ids = (await _inventory.LoadInventoryAsync(id)).Instances.Select(i => ItemInstanceIds.Seq(i.InstanceId)).Order();
        Assert.Equal(new long[] { 1, 2, 3 }, ids);
    }

    [Fact]
    public async Task A_destroyed_instance_leaves_its_row_and_the_ledger_keeps_its_history()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var service = await AttachAsync(id);
        service.TryAdd(id, Item(Cap), 0, 1, "add:cap", InventoryReason.Admin, "test");
        long version = await SaveAsync(service, id, character.Version);

        Assert.True(service.TryRemove(id, Safe, 0, 1, "remove:cap", InventoryReason.Admin, "test"));
        await SaveAsync(service, id, version);

        Assert.Equal(0, await CountAsync("item_instances", id));
        var history = (await _inventory.AuditAsync(id)).Where(a => a.InstanceId == ItemInstanceIds.Make(id, 1)).ToList();
        Assert.Equal(new[] { 1, -1 }, history.Select(a => a.CountDelta));
        Assert.Equal(new[] { AuditKind.Add, AuditKind.Remove }, history.Select(a => a.Kind));
    }

    [Fact]
    public async Task The_starter_weapon_is_granted_on_the_first_login_only()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        Assert.False((await _inventory.LoadInventoryAsync(id)).StarterGranted);
        var first = await AttachAsync(id);
        Assert.True(first.GrantStarterGear(id));
        await SaveAsync(first, id, character.Version);

        var load = await _inventory.LoadInventoryAsync(id);

        Assert.True(load.StarterGranted);
        var sword = Assert.Single(load.Instances);
        Assert.Equal((Sword, (int)Equipped, 0, 0), (sword.DefId, sword.Container, sword.Slot, sword.Rarity));
        var audit = Assert.Single(await _inventory.AuditAsync(id));
        Assert.Equal((AuditKind.Add, InventoryOpKeys.Starter(id), sword.InstanceId), (audit.Kind, audit.OpKey, audit.InstanceId));
    }

    [Fact]
    public async Task Equip_and_unequip_audit_rows_carry_their_kind_and_the_instance()
    {
        var (character, service, version, _, _) = await PrepareAsync(Operation.Equip);
        long id = character.CharacterId;
        await SaveAsync(service, id, version);

        var rows = (await _inventory.AuditAsync(id)).Where(a => a.OpKey == $"op:{Operation.Equip}").ToList();

        Assert.Equal(2, rows.Count); // out of the Safe bag, into Equipped
        Assert.All(rows, r => Assert.Equal((AuditKind.Equip, ItemInstanceIds.Make(id, 1)), (r.Kind, r.InstanceId)));
        Assert.Equal(new[] { (Safe, -1), (Equipped, 1) }, rows.Select(r => ((byte)r.Container, r.CountDelta)).OrderBy(r => r.Item1));
    }
}
