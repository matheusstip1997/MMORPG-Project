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
/// Inventory transactions against a real PostgreSQL (backlog G, rule 5, R-7.1-01/02; CLAUDE.md: double submit, retry, crash
/// in the middle, concurrent access). <see cref="InventoryService"/> builds the batches; the character save writes them
/// with the version-checked row, the operation keys and the audit rows in one transaction.
/// </summary>
public sealed class InventoryPersistenceTests : IAsyncLifetime
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

    /// <summary>A service holding the character as loaded from the database (a world that owns it).</summary>
    private async Task<InventoryService> AttachAsync(long characterId)
    {
        var service = new InventoryService(_items, events: null);
        Assert.True(service.Attach(characterId, await _inventory.LoadAsync(characterId)));
        return service;
    }

    private static InventoryBatch Take(InventoryService service, long characterId, Guid saveId) =>
        service.TakeBatch(characterId, saveId) ?? throw new InvalidOperationException("no batch");

    private async Task<int> CountAsync(string table, long characterId)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table} WHERE character_id = @characterId", new { characterId });
    }

    [Fact]
    public async Task A_save_commits_items_operation_keys_and_audit_rows_together()
    {
        var character = await CreateAsync();
        var service = await AttachAsync(character.CharacterId);
        service.TryAdd(character.CharacterId, Item("m1.demonHorn"), 0, 2, "loot:a:p0", InventoryReason.LootPickup, "loot:a");
        service.TryAdd(character.CharacterId, Item("m1.demonAsh"), 0, 3, "loot:b:p0", InventoryReason.LootPickup, "loot:b");
        var saveId = Guid.NewGuid();
        var batch = Take(service, character.CharacterId, saveId);

        var result = await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch);

        Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), result);
        Assert.Equal(new[]
        {
            new ItemStackRow(1, 0, "m1.demonHorn", 0, false, 2),
            new ItemStackRow(1, 1, "m1.demonAsh", 0, false, 3),
        }, await _inventory.LoadAsync(character.CharacterId));
        Assert.Equal(new[] { "loot:a:p0", "loot:b:p0" }, await _inventory.OpKeysAsync(character.CharacterId));
        var audit = await _inventory.AuditAsync(character.CharacterId);
        Assert.Equal(2, audit.Count);
        Assert.Equal(new AuditEntry("loot:a:p0", 0, AuditKind.Add, 1, 0, "m1.demonHorn", 0, false, 2, 2), audit[0]);

        // The service shows it to the player only now (rule 5: commit, then confirm).
        Assert.True(service.Committed(character.CharacterId).All(s => s.IsEmpty));
        service.Committed(character.CharacterId, saveId);
        Assert.Equal(2, service.Committed(character.CharacterId)[0].Count);
    }

    [Fact]
    public async Task Moves_swaps_and_removes_round_trip_and_reload_into_the_same_bag()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var service = await AttachAsync(id);
        service.TryAdd(id, Item("m1.demonHorn"), 0, 5, "add:1", InventoryReason.Admin, "test");
        service.TryAdd(id, Item("m1.demonAsh"), 0, 4, "add:2", InventoryReason.Admin, "test");
        Assert.Equal(BagMoveResult.Swapped, service.TryMove(id, 0, 1, "move:1", InventoryReason.PlayerMove));
        Assert.Equal(BagMoveResult.Moved, service.TryMove(id, 1, 7, "move:2", InventoryReason.PlayerMove));
        Assert.True(service.TryRemove(id, 0, 1, "remove:1", InventoryReason.Admin, "test"));
        var saveId = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(id, character.State, 1, saveId, Take(service, id, saveId))).Status);

        var reloaded = await AttachAsync(id);
        for (int slot = 0; slot < _items.NormalBag.Slots; slot++)
            Assert.Equal(service.Current(id)[slot], reloaded.Current(id)[slot]);
        Assert.Equal(new[]
        {
            new ItemStackRow(1, 0, "m1.demonAsh", 0, false, 3),
            new ItemStackRow(1, 7, "m1.demonHorn", 0, false, 5),
        }, await _inventory.LoadAsync(id));
        // Every unit is accounted for by the ledger (R-7.1-06): the deltas per item sum to what is held.
        var audit = await _inventory.AuditAsync(id);
        Assert.Equal(5, audit.Where(a => a.DefId == "m1.demonHorn").Sum(a => a.CountDelta));
        Assert.Equal(3, audit.Where(a => a.DefId == "m1.demonAsh").Sum(a => a.CountDelta));
    }

    [Fact]
    public async Task A_double_submit_of_one_save_applies_its_batch_once()
    {
        var character = await CreateAsync();
        var service = await AttachAsync(character.CharacterId);
        service.TryAdd(character.CharacterId, Item("m1.demonHorn"), 0, 2, "loot:a:p0", InventoryReason.LootPickup, "loot:a");
        var saveId = Guid.NewGuid();
        var batch = Take(service, character.CharacterId, saveId);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch)));

        Assert.All(results, r => Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), r));
        Assert.Equal(2, Assert.Single(await _inventory.LoadAsync(character.CharacterId)).Count);
        Assert.Equal(1, await CountAsync("audit_log", character.CharacterId));
        Assert.Equal(1, await CountAsync("inventory_ops", character.CharacterId));
    }

    [Fact]
    public async Task A_retry_after_an_unknown_outcome_that_did_commit_is_recognised_and_writes_nothing_more()
    {
        var character = await CreateAsync();
        var service = await AttachAsync(character.CharacterId);
        service.TryAdd(character.CharacterId, Item("m1.demonHorn"), 0, 2, "loot:a:p0", InventoryReason.LootPickup, "loot:a");
        var saveId = Guid.NewGuid();
        var batch = Take(service, character.CharacterId, saveId);
        await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch);

        // The worker never saw the answer (a timeout): the scheduler resends the identical job (same save id, same batch).
        var retry = await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch);

        Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), retry);
        Assert.Equal(1, await CountAsync("audit_log", character.CharacterId));
        Assert.Equal(2, Assert.Single(await _inventory.LoadAsync(character.CharacterId)).Count);
    }

    [Fact]
    public async Task A_crash_in_the_middle_of_the_transaction_writes_nothing_and_the_retry_applies_once()
    {
        var character = await CreateAsync();
        var service = await AttachAsync(character.CharacterId);
        service.TryAdd(character.CharacterId, Item("m1.demonHorn"), 0, 2, "loot:a:p0", InventoryReason.LootPickup, "loot:a");
        service.TryAdd(character.CharacterId, Item("m1.demonAsh"), 0, 1, "crash:b", InventoryReason.LootPickup, "loot:b");
        var saveId = Guid.NewGuid();
        var batch = Take(service, character.CharacterId, saveId);
        // The audit insert fails after the row update, the operation keys and the item rows were written: a crash mid-way.
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_crash() RETURNS trigger AS $$
                BEGIN IF NEW.op_key LIKE 'crash:%' THEN RAISE EXCEPTION 'simulated crash'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_crash BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION test_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() =>
            _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch));

        Assert.Equal(character.Version, (await _characters.LoadAsync(character.CharacterId))!.Version);
        Assert.Empty(await _inventory.LoadAsync(character.CharacterId));
        Assert.Equal(0, await CountAsync("inventory_ops", character.CharacterId));
        Assert.Equal(0, await CountAsync("audit_log", character.CharacterId));

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_crash ON audit_log");
        var retry = await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch);

        Assert.Equal(SaveStatus.Saved, retry.Status);
        Assert.Equal(2, (await _inventory.LoadAsync(character.CharacterId)).Count);
        Assert.Equal(2, await CountAsync("audit_log", character.CharacterId));
    }

    [Fact]
    public async Task Concurrent_writers_of_one_character_have_exactly_one_winner_and_nothing_is_duplicated()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        // Two worlds that both think they own the character (a split brain the ownership lock should prevent): one adds,
        // one adds and removes, both from the same loaded version.
        var adder = await AttachAsync(id);
        var other = await AttachAsync(id);
        adder.TryAdd(id, Item("m1.demonHorn"), 0, 5, "world-a:add", InventoryReason.Admin, "test");
        other.TryAdd(id, Item("m1.demonAsh"), 0, 9, "world-b:add", InventoryReason.Admin, "test");
        Assert.True(other.TryRemove(id, 0, 4, "world-b:remove", InventoryReason.Admin, "test"));
        var saveA = Guid.NewGuid();
        var saveB = Guid.NewGuid();
        var batchA = Take(adder, id, saveA);
        var batchB = Take(other, id, saveB);

        var results = await Task.WhenAll(
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, saveA, batchA)),
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, saveB, batchB)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        var rows = await _inventory.LoadAsync(id);
        var row = Assert.Single(rows);
        Assert.True(row == new ItemStackRow(1, 0, "m1.demonHorn", 0, false, 5) || row == new ItemStackRow(1, 0, "m1.demonAsh", 0, false, 5));
        Assert.Equal(results[0].Status == SaveStatus.Saved ? 1 : 2, await CountAsync("inventory_ops", id));
    }

    [Fact]
    public async Task Many_parallel_saves_at_one_version_apply_exactly_one_batch()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var batches = new List<(Guid SaveId, InventoryBatch Batch)>();
        for (int i = 0; i < 8; i++)
        {
            var service = await AttachAsync(id);
            service.TryAdd(id, Item("m1.demonHorn"), 0, 1 + i, $"world-{i}:add", InventoryReason.Admin, "test");
            var saveId = Guid.NewGuid();
            batches.Add((saveId, Take(service, id, saveId)));
        }

        var results = await Task.WhenAll(batches.Select(b =>
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, b.SaveId, b.Batch))));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Equal(1, await CountAsync("item_stacks", id));
        Assert.Equal(1, await CountAsync("inventory_ops", id));
    }

    [Fact]
    public async Task An_operation_key_applied_for_one_character_is_refused_for_another()
    {
        var first = await CreateAsync();
        var second = await CreateAsync();
        var a = await AttachAsync(first.CharacterId);
        var b = await AttachAsync(second.CharacterId);
        // The same drop pickup key on two characters: impossible in one world (the world thread claims a drop once), so
        // the database refuses it as the last line of defence against duplication.
        a.TryAdd(first.CharacterId, Item("m1.demonHorn"), 0, 2, "run:7:0:p0", InventoryReason.LootPickup, "run:7:0");
        b.TryAdd(second.CharacterId, Item("m1.demonHorn"), 0, 2, "run:7:0:p0", InventoryReason.LootPickup, "run:7:0");
        var saveA = Guid.NewGuid();
        var saveB = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved,
            (await _characters.SaveAsync(first.CharacterId, first.State, first.Version, saveA, Take(a, first.CharacterId, saveA))).Status);

        var duplicate = await _characters.SaveAsync(second.CharacterId, second.State, second.Version, saveB, Take(b, second.CharacterId, saveB));

        Assert.Equal(SaveStatus.DuplicateOperation, duplicate.Status);
        Assert.Equal(second.Version, (await _characters.LoadAsync(second.CharacterId))!.Version); // nothing written, row included
        Assert.Empty(await _inventory.LoadAsync(second.CharacterId));
        Assert.Equal(SaveOutcome.DuplicateOperation, SaveCompletion.From(new SaveJob(second.CharacterId, second.State, 1, saveB, SaveReason.Class2Batch), duplicate).Outcome);
    }

    [Fact]
    public async Task A_repeated_operation_key_in_one_session_returns_the_first_result_and_records_nothing()
    {
        var character = await CreateAsync();
        var service = await AttachAsync(character.CharacterId);

        var first = service.TryAdd(character.CharacterId, Item("m1.demonHorn"), 0, 2, "loot:a:p0", InventoryReason.LootPickup, "loot:a");
        var again = service.TryAdd(character.CharacterId, Item("m1.demonHorn"), 0, 2, "loot:a:p0", InventoryReason.LootPickup, "loot:a");

        Assert.Equal(new AddResult(2, false), first);
        Assert.Equal(new AddResult(2, true), again);
        var batch = Take(service, character.CharacterId, Guid.NewGuid());
        Assert.Single(batch.Ops);
        Assert.Equal(2, service.Current(character.CharacterId)[0].Count);
    }

    [Fact]
    public async Task The_worker_saves_a_batch_and_reports_it_through_the_completion_queue()
    {
        var character = await CreateAsync();
        var service = await AttachAsync(character.CharacterId);
        service.TryAdd(character.CharacterId, Item("m1.cyclopsHide"), 0, 4, "loot:c:p0", InventoryReason.LootPickup, "loot:c");
        var saveId = Guid.NewGuid();
        await using var worker = new PersistenceWorker(_characters);

        worker.Enqueue(new SaveJob(character.CharacterId, character.State, character.Version, saveId, SaveReason.Class2Batch,
            Take(service, character.CharacterId, saveId)));
        SaveCompletion completion = default;
        for (int i = 0; i < 500 && !worker.TryDequeue(out completion); i++)
            await Task.Delay(10);

        Assert.Equal(SaveOutcome.Saved, completion.Outcome);
        Assert.Equal(4, Assert.Single(await _inventory.LoadAsync(character.CharacterId)).Count);
    }
}
