using Dapper;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;
using Shared.Simulation.Economy;

namespace GameServer.Tests.Economy;

/// <summary>
/// The fee inside its caller's operation against a real PostgreSQL (spec 4.2 R-4.2-05, 8.2; rule 5; CLAUDE.md: double
/// submit, retry, crash in the middle, concurrent access; WP-M4-1). The real callers (listings WP-M4-7, stalls WP-M4-8,
/// contracts WP-M4-10) come later, so the caller here is a <b>test settlement</b>: the seller's sold units leave the bag, the
/// sale's gold comes in, and the sale tax goes out through <see cref="FeeService.Apply"/>, all in one pending batch that
/// one <see cref="CharacterRepository.SaveAsync"/> writes with the version-checked row in one transaction. Every settlement
/// writes exactly one <c>gold_ledger</c> sink row with reason <c>Fee</c>.
/// </summary>
public sealed class FeePersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly ItemRules _items = ItemRules.From(TestCombat.Tables())!;
    private readonly FeeRateTable _rates = EconomyRules.From(TestCombat.Tables())!.Fees!;
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

    /// <summary>A character holding <paramref name="horns"/> demon horns, committed.</summary>
    private async Task<CharacterRecord> CreateAsync(int horns)
    {
        long account = _nextAccount++;
        var result = await _characters.CreateAsync(new NewCharacter(account, 0, $"Seller{account}", "warrior", Spawn, Guid.NewGuid()));
        var character = result.Character!;
        var (service, _) = await AttachAsync(character.CharacterId);
        service.TryAdd(character.CharacterId, Item("m1.demonHorn"), 0, horns, $"seed:{account}:items", InventoryReason.Admin, "test");
        var saveId = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved,
            (await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, Take(service, character.CharacterId, saveId))).Status);
        return (await _characters.LoadAsync(character.CharacterId))!;
    }

    /// <summary>A world's view of the character as loaded from the database, with its fee computer.</summary>
    private async Task<(InventoryService Inventory, FeeService Fees)> AttachAsync(long characterId)
    {
        var service = new InventoryService(_items, events: null);
        Assert.True(service.Attach(characterId, await _inventory.LoadAsync(characterId)));
        service.AttachGold(characterId, (await _characters.LoadAsync(characterId))!.Gold);
        return (service, new FeeService(_rates, service, NoTerritoryOwners.Instance, events: null));
    }

    private static InventoryBatch Take(InventoryService service, long characterId, Guid saveId) =>
        service.TakeBatch(characterId, saveId) ?? throw new InvalidOperationException("no batch");

    /// <summary>
    /// The test caller (WP-M4-1): a settlement that sells <paramref name="count"/> horns for <paramref name="price"/> gold and
    /// pays the sale tax on it, every half applied after the checks as the real settlements will (keys <c>{op}:items</c>,
    /// <c>{op}:gold</c>, <c>{op}:fee</c>). Returns the tax destroyed.
    /// </summary>
    private static long Settle(InventoryService inventory, FeeService fees, long characterId, string op, int count, long price)
    {
        var tax = fees.Quote(characterId, 0, FeeType.SaleTax, price);
        Assert.True(inventory.TryRemove(characterId, 0, count, op + ":items", InventoryReason.Admin, "test:settlement"));
        Assert.True(inventory.TryAddGold(characterId, price, op + ":gold", InventoryReason.Admin, "test:settlement").Applied);
        var applied = fees.Apply(characterId, tax, op, "test:settlement");
        Assert.True(applied.Ok);
        return applied.Destroyed;
    }

    private async Task<int> Horns(long characterId) =>
        (await _inventory.LoadAsync(characterId)).Where(r => r.DefId == "m1.demonHorn").Sum(r => r.Count);

    private async Task<List<GoldEntry>> FeeRows(long characterId) =>
        (await _gold.LedgerAsync(characterId)).Where(e => e.Reason == nameof(InventoryReason.Fee)).ToList();

    [Fact]
    public async Task A_settlement_commits_the_items_the_gold_and_one_Fee_sink_row_together()
    {
        var character = await CreateAsync(horns: 20);
        long id = character.CharacterId;
        var (inventory, fees) = await AttachAsync(id);
        long tax = Settle(inventory, fees, id, "test:r:1", 5, 1_000);
        var saveId = Guid.NewGuid();

        var result = await _characters.SaveAsync(id, character.State, character.Version, saveId, Take(inventory, id, saveId));

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(50, tax); // 5 % of 1 000 (FeeRates saleTax, placeholder)
        Assert.Equal(15, await Horns(id));
        Assert.Equal(950, await _gold.BalanceAsync(id));
        var fee = Assert.Single(await FeeRows(id));
        Assert.Equal(new GoldEntry("test:r:1:fee", -50, 950, GoldFlow.Sink, nameof(InventoryReason.Fee), "test:settlement"), fee);
        Assert.Contains("test:r:1:fee", await _inventory.OpKeysAsync(id));

        inventory.Committed(id, saveId);
        Assert.True(inventory.IsCommitted(id, FeeService.OpKeyOf("test:r:1")));
        Assert.Equal(950, inventory.CommittedGold(id));
    }

    [Fact]
    public async Task Every_settlement_writes_exactly_one_Fee_sink_row()
    {
        var character = await CreateAsync(horns: 20);
        long id = character.CharacterId;
        var (inventory, fees) = await AttachAsync(id);
        Settle(inventory, fees, id, "test:r:2a", 2, 400);
        Settle(inventory, fees, id, "test:r:2b", 2, 600);
        Settle(inventory, fees, id, "test:r:2c", 2, 19); // a tax that rounds to 0: no row
        var saveId = Guid.NewGuid();

        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(id, character.State, character.Version, saveId, Take(inventory, id, saveId))).Status);

        var rows = await FeeRows(id);
        Assert.Equal(["test:r:2a:fee", "test:r:2b:fee"], rows.Select(r => r.OpKey));
        Assert.All(rows, r => Assert.Equal(GoldFlow.Sink, r.Flow));
        Assert.Equal(-50, rows.Sum(r => r.Delta));
        Assert.Equal(400 + 600 + 19 - 50, await _gold.BalanceAsync(id));
    }

    [Fact]
    public async Task A_double_submit_of_a_settlement_save_charges_the_fee_once()
    {
        var character = await CreateAsync(horns: 20);
        long id = character.CharacterId;
        var (inventory, fees) = await AttachAsync(id);
        Settle(inventory, fees, id, "test:r:3", 5, 1_000);
        Assert.Equal(FeeApplyOutcome.Repeated, fees.Apply(id, fees.Quote(id, 0, FeeType.SaleTax, 1_000), "test:r:3", "test").Outcome);
        var saveId = Guid.NewGuid();
        var batch = Take(inventory, id, saveId);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _characters.SaveAsync(id, character.State, character.Version, saveId, batch)));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Single(await FeeRows(id));
        Assert.Equal(950, await _gold.BalanceAsync(id));
        Assert.Equal(15, await Horns(id));
    }

    [Fact]
    public async Task A_retry_after_an_unknown_outcome_is_recognised_and_charges_once()
    {
        var character = await CreateAsync(horns: 20);
        long id = character.CharacterId;
        var (inventory, fees) = await AttachAsync(id);
        Settle(inventory, fees, id, "test:r:4", 5, 1_000);
        var saveId = Guid.NewGuid();
        var batch = Take(inventory, id, saveId);
        await _characters.SaveAsync(id, character.State, character.Version, saveId, batch); // committed, but the world never heard

        var retry = await _characters.SaveAsync(id, character.State, character.Version, saveId, batch);

        Assert.Equal(SaveStatus.Saved, retry.Status);
        Assert.Single(await FeeRows(id));
        Assert.Equal(950, await _gold.BalanceAsync(id));
    }

    [Fact]
    public async Task A_crash_in_the_middle_writes_neither_the_settlement_nor_the_fee_and_the_retry_applies_once()
    {
        var character = await CreateAsync(horns: 20);
        long id = character.CharacterId;
        var (inventory, fees) = await AttachAsync(id);
        Settle(inventory, fees, id, "crash:r:5", 5, 1_000);
        var saveId = Guid.NewGuid();
        var batch = Take(inventory, id, saveId);
        // The fee's ledger insert fails after the row, the keys, the items, the audit, the balance and the sale's gold row.
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_fee_crash() RETURNS trigger AS $$
                BEGIN IF NEW.op_key LIKE 'crash:%:fee' THEN RAISE EXCEPTION 'simulated crash'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_fee_crash BEFORE INSERT ON gold_ledger FOR EACH ROW EXECUTE FUNCTION test_fee_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveAsync(id, character.State, character.Version, saveId, batch));

        Assert.Equal(character.Version, (await _characters.LoadAsync(id))!.Version);
        Assert.Equal(20, await Horns(id));
        Assert.Equal(0, await _gold.BalanceAsync(id));
        Assert.Empty(await _gold.LedgerAsync(id));
        Assert.DoesNotContain(await _inventory.OpKeysAsync(id), k => k.StartsWith("crash:", StringComparison.Ordinal));

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_fee_crash ON gold_ledger");
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(id, character.State, character.Version, saveId, batch)).Status);

        Assert.Single(await FeeRows(id));
        Assert.Equal(950, await _gold.BalanceAsync(id));
        Assert.Equal(15, await Horns(id));
    }

    [Fact]
    public async Task Concurrent_writers_of_one_character_have_one_winner_and_the_fee_is_charged_once()
    {
        var character = await CreateAsync(horns: 20);
        long id = character.CharacterId;
        // Two worlds that both think they own the character settle the same horns from the same loaded version.
        var (a, feesA) = await AttachAsync(id);
        var (b, feesB) = await AttachAsync(id);
        Settle(a, feesA, id, "test:a:6", 20, 1_000);
        Settle(b, feesB, id, "test:b:6", 20, 2_000);
        var saveA = Guid.NewGuid();
        var saveB = Guid.NewGuid();
        var batchA = Take(a, id, saveA);
        var batchB = Take(b, id, saveB);

        var results = await Task.WhenAll(
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, saveA, batchA)),
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, saveB, batchB)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        var fee = Assert.Single(await FeeRows(id));
        long balance = await _gold.BalanceAsync(id);
        Assert.True((fee.OpKey, balance) is ("test:a:6:fee", 950) or ("test:b:6:fee", 1_900), $"{fee.OpKey} {balance}");
        Assert.Equal(0, await Horns(id));
    }
}
