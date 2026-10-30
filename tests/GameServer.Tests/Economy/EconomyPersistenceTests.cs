using Dapper;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;

namespace GameServer.Tests.Economy;

/// <summary>
/// The outpost sale and the vendor purchase against a real PostgreSQL (class 1, R-7.1-01/02, rule 5; CLAUDE.md: double
/// submit, retry, crash in the middle, concurrent access; WP-M2-4). Each builds the batch the handlers build (items and
/// gold through <see cref="InventoryService"/>, pending class 2 changes included) and the character save writes it with the
/// version-checked row, the operation keys, the audit rows, the gold balance and the gold ledger in one transaction.
/// Also: the gold reloads with the character, the demand state round trip, and migrations applied with a version gap.
/// </summary>
public sealed class EconomyPersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly ItemRules _items = ItemRules.From(TestCombat.Tables())!;
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private InventoryRepository _inventory = null!;
    private GoldRepository _gold = null!;
    private long _nextAccount = 1;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _inventory = new InventoryRepository(_realmDb);
        _gold = new GoldRepository(_realmDb);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private ItemDef Item(string id)
    {
        Assert.True(_items.TryGetItem(id, out var item));
        return item;
    }

    /// <summary>A character holding <paramref name="horns"/> demon horns and <paramref name="gold"/> gold, committed.</summary>
    private async Task<CharacterRecord> CreateAsync(int horns = 0, long gold = 0)
    {
        long account = _nextAccount++;
        var result = await _characters.CreateAsync(new NewCharacter(account, 0, $"Seller{account}", "warrior", Spawn, Guid.NewGuid()));
        var character = result.Character!;
        if (horns == 0 && gold == 0)
            return character;
        var service = await AttachAsync(character.CharacterId);
        if (horns > 0)
            service.TryAdd(character.CharacterId, Item("m1.demonHorn"), 0, horns, $"seed:{account}:items", InventoryReason.Admin, "test");
        if (gold > 0)
            service.TryAddGold(character.CharacterId, gold, $"seed:{account}:gold", InventoryReason.Admin, "test");
        var saveId = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved,
            (await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, Take(service, character.CharacterId, saveId))).Status);
        return (await _characters.LoadAsync(character.CharacterId))!;
    }

    /// <summary>A service holding the character as loaded from the database (a world that owns it).</summary>
    private async Task<InventoryService> AttachAsync(long characterId)
    {
        var service = new InventoryService(_items, events: null);
        Assert.True(service.Attach(characterId, await _inventory.LoadAsync(characterId)));
        service.AttachGold(characterId, (await _characters.LoadAsync(characterId))!.Gold);
        return service;
    }

    private static InventoryBatch Take(InventoryService service, long characterId, Guid saveId) =>
        service.TakeBatch(characterId, saveId) ?? throw new InvalidOperationException("no batch");

    /// <summary>What <c>SaleHandler</c> applies for a sale: the units out (<c>{op}:items</c>), the gold in (<c>{op}:gold</c>).</summary>
    private void Sell(InventoryService service, long characterId, string op, int count, long gold)
    {
        Assert.True(service.TryRemove(characterId, 0, count, op + ":items", InventoryReason.OutpostSale, "npc:m1.outpost"));
        Assert.True(service.TryAddGold(characterId, gold, op + ":gold", InventoryReason.OutpostSale, "npc:m1.outpost",
            new SaleInfo((ushort)Item("m1.demonHorn").NetId, 0, count, 1)).Applied);
    }

    /// <summary>What <c>VendorHandler</c> applies for a purchase: the gold out, the potions in.</summary>
    private void Buy(InventoryService service, long characterId, string op, int count, long gold)
    {
        Assert.True(service.TryRemoveGold(characterId, gold, op + ":gold", InventoryReason.VendorPurchase, "npc:m1.vendor").Applied);
        Assert.Equal(count, service.TryAdd(characterId, Item("m2.potionHpSmall"), 0, count, op + ":items", InventoryReason.VendorPurchase,
            "npc:m1.vendor").Added);
    }

    private async Task<int> CountAsync(string table, long characterId)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table} WHERE character_id = @characterId", new { characterId });
    }

    private async Task<int> Horns(long characterId) =>
        (await _inventory.LoadAsync(characterId)).Where(r => r.DefId == "m1.demonHorn").Sum(r => r.Count);

    [Fact]
    public async Task A_sale_commits_the_items_the_gold_the_keys_the_audit_and_the_ledger_together()
    {
        var character = await CreateAsync(horns: 20);
        long id = character.CharacterId;
        var service = await AttachAsync(id);
        Sell(service, id, "sell:r:1:1:1", 5, 50);
        var saveId = Guid.NewGuid();

        var result = await _characters.SaveAsync(id, character.State, character.Version, saveId, Take(service, id, saveId));

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(15, await Horns(id));
        Assert.Equal(50, await _gold.BalanceAsync(id));
        Assert.Equal(50, (await _characters.LoadAsync(id))!.Gold); // a login reads it with the row
        var entry = (await _gold.LedgerAsync(id)).Single(e => e.OpKey == "sell:r:1:1:1:gold");
        Assert.Equal(new GoldEntry("sell:r:1:1:1:gold", 50, 50, GoldFlow.Faucet, nameof(InventoryReason.OutpostSale), "npc:m1.outpost"), entry);
        Assert.Contains("sell:r:1:1:1:items", await _inventory.OpKeysAsync(id));
        Assert.Equal(-5, (await _inventory.AuditAsync(id)).Where(a => a.OpKey == "sell:r:1:1:1:items").Sum(a => a.CountDelta));

        service.Committed(id, saveId);
        Assert.Equal(50, service.CommittedGold(id));
        Assert.True(service.IsCommitted(id, "sell:r:1:1:1:gold"));
    }

    [Fact]
    public async Task A_sale_while_a_class_2_batch_is_pending_writes_both_in_one_transaction()
    {
        var character = await CreateAsync(horns: 20);
        long id = character.CharacterId;
        var service = await AttachAsync(id);
        // A pickup not saved yet (class 2), then the sale of everything: the ordering barrier by inclusion (R-7.1-02).
        service.TryAdd(id, Item("m1.demonHorn"), 0, 4, "loot:k:p0", InventoryReason.LootPickup, "loot:k");
        Sell(service, id, "sell:r:1:1:2", 24, 200);
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);
        Assert.Equal(["loot:k:p0", "sell:r:1:1:2:items", "sell:r:1:1:2:gold"], batch.Ops.Select(o => o.OpKey));

        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(id, character.State, character.Version, saveId, batch)).Status);

        Assert.Equal(0, await Horns(id)); // sold units that were picked up in the same batch never exist alone in the database
        Assert.Equal(200, await _gold.BalanceAsync(id));
    }

    [Fact]
    public async Task A_double_submit_of_a_sale_save_applies_it_once()
    {
        var character = await CreateAsync(horns: 20);
        long id = character.CharacterId;
        var service = await AttachAsync(id);
        Sell(service, id, "sell:r:1:1:3", 5, 50);
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _characters.SaveAsync(id, character.State, character.Version, saveId, batch)));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(15, await Horns(id));
        Assert.Equal(50, await _gold.BalanceAsync(id));
        Assert.Single(await _gold.LedgerAsync(id), e => e.OpKey.StartsWith("sell:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_retry_of_a_sale_after_an_unknown_outcome_is_recognised_and_pays_once()
    {
        var character = await CreateAsync(horns: 20);
        long id = character.CharacterId;
        var service = await AttachAsync(id);
        Sell(service, id, "sell:r:1:1:4", 5, 50);
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);
        await _characters.SaveAsync(id, character.State, character.Version, saveId, batch);

        var retry = await _characters.SaveAsync(id, character.State, character.Version, saveId, batch);

        Assert.Equal(SaveStatus.Saved, retry.Status);
        Assert.Equal(50, await _gold.BalanceAsync(id));
        Assert.Equal(15, await Horns(id));
    }

    [Fact]
    public async Task A_crash_in_the_middle_of_a_sale_writes_nothing_and_the_retry_applies_once()
    {
        var character = await CreateAsync(horns: 20);
        long id = character.CharacterId;
        var service = await AttachAsync(id);
        Sell(service, id, "crash:sale", 5, 50);
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);
        // The ledger insert fails after the row, the keys, the items, the audit and the balance were written.
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_gold_crash() RETURNS trigger AS $$
                BEGIN IF NEW.op_key LIKE 'crash:%' THEN RAISE EXCEPTION 'simulated crash'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_gold_crash BEFORE INSERT ON gold_ledger FOR EACH ROW EXECUTE FUNCTION test_gold_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveAsync(id, character.State, character.Version, saveId, batch));

        Assert.Equal(character.Version, (await _characters.LoadAsync(id))!.Version);
        Assert.Equal(20, await Horns(id));
        Assert.Equal(0, await _gold.BalanceAsync(id));
        Assert.DoesNotContain(await _inventory.OpKeysAsync(id), k => k.StartsWith("crash:", StringComparison.Ordinal));

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_gold_crash ON gold_ledger");
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(id, character.State, character.Version, saveId, batch)).Status);

        Assert.Equal(15, await Horns(id));
        Assert.Equal(50, await _gold.BalanceAsync(id));
    }

    [Fact]
    public async Task Concurrent_writers_of_a_seller_have_one_winner_and_the_gold_is_never_paid_twice()
    {
        var character = await CreateAsync(horns: 20);
        long id = character.CharacterId;
        // Two worlds that both think they own the character sell the same horns from the same loaded version.
        var a = await AttachAsync(id);
        var b = await AttachAsync(id);
        Sell(a, id, "sell:a:1:1:1", 20, 150);
        Sell(b, id, "sell:b:1:1:1", 20, 150);
        var saveA = Guid.NewGuid();
        var saveB = Guid.NewGuid();
        var batchA = Take(a, id, saveA);
        var batchB = Take(b, id, saveB);

        var results = await Task.WhenAll(
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, saveA, batchA)),
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, saveB, batchB)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        Assert.Equal(150, await _gold.BalanceAsync(id));
        Assert.Equal(0, await Horns(id));
        Assert.Single(await _gold.LedgerAsync(id), e => e.OpKey.StartsWith("sell:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_purchase_commits_the_gold_destroyed_and_the_items_created_together()
    {
        var character = await CreateAsync(gold: 100);
        long id = character.CharacterId;
        var service = await AttachAsync(id);
        Buy(service, id, "buy:r:1:1:1", 2, 50);
        var saveId = Guid.NewGuid();

        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(id, character.State, character.Version, saveId, Take(service, id, saveId))).Status);

        Assert.Equal(50, await _gold.BalanceAsync(id));
        Assert.Contains(await _inventory.LoadAsync(id), r => r.DefId == "m2.potionHpSmall" && r.Count == 2);
        var entry = (await _gold.LedgerAsync(id)).Single(e => e.OpKey == "buy:r:1:1:1:gold");
        Assert.Equal((-50L, 50L, GoldFlow.Sink), (entry.Delta, entry.BalanceAfter, entry.Flow));
    }

    [Fact]
    public async Task A_double_submit_or_retry_of_a_purchase_charges_once()
    {
        var character = await CreateAsync(gold: 100);
        long id = character.CharacterId;
        var service = await AttachAsync(id);
        Buy(service, id, "buy:r:1:1:2", 1, 25);
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _characters.SaveAsync(id, character.State, character.Version, saveId, batch)));
        var retry = await _characters.SaveAsync(id, character.State, character.Version, saveId, batch);

        Assert.All(results.Append(retry), r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(75, await _gold.BalanceAsync(id));
        Assert.Equal(1, (await _inventory.LoadAsync(id)).Single(r => r.DefId == "m2.potionHpSmall").Count);
    }

    [Fact]
    public async Task A_crash_in_the_middle_of_a_purchase_writes_nothing()
    {
        var character = await CreateAsync(gold: 100);
        long id = character.CharacterId;
        var service = await AttachAsync(id);
        Buy(service, id, "crash:buy", 1, 25);
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_buy_crash() RETURNS trigger AS $$
                BEGIN IF NEW.op_key LIKE 'crash:%' THEN RAISE EXCEPTION 'simulated crash'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_buy_crash BEFORE INSERT ON gold_ledger FOR EACH ROW EXECUTE FUNCTION test_buy_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveAsync(id, character.State, character.Version, saveId, batch));

        Assert.Equal(100, await _gold.BalanceAsync(id));
        Assert.DoesNotContain(await _inventory.LoadAsync(id), r => r.DefId == "m2.potionHpSmall");
    }

    [Fact]
    public async Task Concurrent_purchases_from_one_balance_have_one_winner()
    {
        var character = await CreateAsync(gold: 30);
        long id = character.CharacterId;
        var a = await AttachAsync(id);
        var b = await AttachAsync(id);
        Buy(a, id, "buy:a:1:1:1", 1, 25);
        Buy(b, id, "buy:b:1:1:1", 1, 25);
        var saveA = Guid.NewGuid();
        var saveB = Guid.NewGuid();
        var batchA = Take(a, id, saveA);
        var batchB = Take(b, id, saveB);

        var results = await Task.WhenAll(
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, saveA, batchA)),
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, saveB, batchB)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Equal(5, await _gold.BalanceAsync(id)); // 30 - 25, once
        Assert.Equal(1, (await _inventory.LoadAsync(id)).Single(r => r.DefId == "m2.potionHpSmall").Count);
    }

    // Vendor instances (WP-M3-2, R-4.2-01: basic tools and starter molds): the purchase creates an instance (a new id and the
    // character's instance counter) in the Safe bag equipment area, in the same class 1 transaction as the gold destroyed.

    /// <summary>What <c>VendorHandler</c> applies for a pickaxe: the gold out, one instance in.</summary>
    private void BuyPickaxe(InventoryService service, long characterId, string op, long gold = 50)
    {
        Assert.True(service.TryRemoveGold(characterId, gold, op + ":gold", InventoryReason.VendorPurchase, "npc:m1.vendor").Applied);
        Assert.Equal(1, service.TryAdd(characterId, Item("m3.pickaxeT1"), 0, 1, op + ":items", InventoryReason.VendorPurchase,
            "npc:m1.vendor").Added);
    }

    private async Task<InventoryService> AttachInstancesAsync(long characterId)
    {
        var service = new InventoryService(_items, events: null);
        Assert.True(service.Attach(characterId, await _inventory.LoadInventoryAsync(characterId), "warrior", 1));
        service.AttachGold(characterId, (await _characters.LoadAsync(characterId))!.Gold);
        return service;
    }

    private async Task<List<ItemInstanceRow>> Pickaxes(long characterId) =>
        (await _inventory.LoadInventoryAsync(characterId)).Instances.Where(i => i.DefId == "m3.pickaxeT1").ToList();

    [Fact]
    public async Task A_tool_purchase_commits_the_gold_destroyed_and_the_instance_created_together()
    {
        var character = await CreateAsync(gold: 100);
        long id = character.CharacterId;
        var service = await AttachInstancesAsync(id);
        BuyPickaxe(service, id, "buy:r:1:1:10");
        var saveId = Guid.NewGuid();

        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(id, character.State, character.Version, saveId, Take(service, id, saveId))).Status);

        Assert.Equal(50, await _gold.BalanceAsync(id));
        var load = await _inventory.LoadInventoryAsync(id);
        var pickaxe = Assert.Single(load.Instances);
        Assert.Equal((ItemInstanceIds.Make(id, 1), 2, "m3.pickaxeT1"), (pickaxe.InstanceId, pickaxe.Container, pickaxe.DefId));
        Assert.Equal(1, load.InstanceSeq);
        Assert.True(Shared.Simulation.Items.ItemProperties.TryParse(pickaxe.PropertiesJson, out var properties, out _));
        Assert.Equal((2, id), (properties.ReadVersion, properties.OriginalOwner)); // written as v2
        Assert.True(new InventoryService(_items, events: null).Attach(id, load, "warrior", 1)); // and it loads back
        var entry = (await _gold.LedgerAsync(id)).Single(e => e.OpKey == "buy:r:1:1:10:gold");
        Assert.Equal((-50L, 50L, GoldFlow.Sink), (entry.Delta, entry.BalanceAfter, entry.Flow));
    }

    [Fact]
    public async Task A_double_submit_or_retry_of_a_tool_purchase_creates_one_instance_and_charges_once()
    {
        var character = await CreateAsync(gold: 100);
        long id = character.CharacterId;
        var service = await AttachInstancesAsync(id);
        BuyPickaxe(service, id, "buy:r:1:1:11");
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _characters.SaveAsync(id, character.State, character.Version, saveId, batch)));
        var retry = await _characters.SaveAsync(id, character.State, character.Version, saveId, batch);

        Assert.All(results.Append(retry), r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(50, await _gold.BalanceAsync(id));
        Assert.Single(await Pickaxes(id));
    }

    [Fact]
    public async Task A_crash_in_the_middle_of_a_tool_purchase_writes_nothing_and_the_id_is_given_again()
    {
        var character = await CreateAsync(gold: 100);
        long id = character.CharacterId;
        var service = await AttachInstancesAsync(id);
        BuyPickaxe(service, id, "crash:tool");
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_tool_crash() RETURNS trigger AS $$
                BEGIN IF NEW.op_key LIKE 'crash:%' THEN RAISE EXCEPTION 'simulated crash'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_tool_crash BEFORE INSERT ON gold_ledger FOR EACH ROW EXECUTE FUNCTION test_tool_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveAsync(id, character.State, character.Version, saveId, batch));

        Assert.Equal(100, await _gold.BalanceAsync(id));
        var load = await _inventory.LoadInventoryAsync(id);
        Assert.Empty(load.Instances);
        Assert.Equal(0, load.InstanceSeq); // the counter rolled back with the instance: no id was given (R-7.1-02)
    }

    [Fact]
    public async Task Concurrent_tool_purchases_from_one_balance_have_one_winner()
    {
        var character = await CreateAsync(gold: 60);
        long id = character.CharacterId;
        var a = await AttachInstancesAsync(id);
        var b = await AttachInstancesAsync(id);
        BuyPickaxe(a, id, "buy:a:1:1:12");
        BuyPickaxe(b, id, "buy:b:1:1:12");
        var saveA = Guid.NewGuid();
        var saveB = Guid.NewGuid();
        var batchA = Take(a, id, saveA);
        var batchB = Take(b, id, saveB);

        var results = await Task.WhenAll(
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, saveA, batchA)),
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, saveB, batchB)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Equal(10, await _gold.BalanceAsync(id)); // 60 - 50, once
        Assert.Single(await Pickaxes(id)); // both took id 1; one instance exists
    }

    [Fact]
    public async Task The_demand_state_round_trips_and_an_upsert_replaces_a_row()
    {
        var repository = new DemandStateRepository(_realmDb, _clock);
        await repository.SaveAsync(1,
        [
            new DemandStateRow(DemandKeyKind.Kills, "m1.demon", "m1.combat", 12_000, 130, 80),
            new DemandStateRow(DemandKeyKind.Sales, "m1.demonHorn", "", 40_000, 100, 100),
        ]);
        await repository.SaveAsync(1, [new DemandStateRow(DemandKeyKind.Sales, "m1.demonHorn", "", 36_000, 100, 100)]);
        await repository.SaveAsync(2, [new DemandStateRow(DemandKeyKind.Sales, "m1.demonHorn", "", 1, 100, 100)]);

        var rows = await repository.LoadAsync(1);

        Assert.Equal(
        [
            new DemandStateRow(DemandKeyKind.Kills, "m1.demon", "m1.combat", 12_000, 130, 80),
            new DemandStateRow(DemandKeyKind.Sales, "m1.demonHorn", "", 36_000, 100, 100),
        ], rows);
    }

    [Fact]
    public async Task Migrations_apply_across_a_version_gap_and_a_later_lower_version_still_applies()
    {
        // V0006 lands before V0004/V0005 of parallel work: the runner applies every known unapplied version in order.
        var database = await _databases.CreateEmptyAsync("gap"); // disposed with the others
        var runner = new MigrationRunner(_clock);
        var one = Migration.Create(1, "one", "CREATE TABLE gap_one (id int);");
        var six = Migration.Create(6, "six", "CREATE TABLE gap_six (id int);");
        var four = Migration.Create(4, "four", "CREATE TABLE gap_four (id int);");

        var first = await runner.ApplyAsync(database, [one, six]);
        var later = await runner.ApplyAsync(database, [one, four, six]);

        Assert.Equal([1, 6], first.Applied);
        Assert.Equal([4], later.Applied);
        Assert.Equal(6, later.CurrentVersion);
    }
}
