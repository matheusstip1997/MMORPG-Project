using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Simulation.Items;
using static GameServer.Tests.Items.TransferTestKit;

namespace GameServer.Tests.Items;

/// <summary>
/// The two-character transaction of a trade against a real PostgreSQL (WP-M4-4; spec 4.5, 7.1 R-7.1-02 over two characters,
/// R-7.1-06; CLAUDE.md rule 5: double submit, retry after an unknown outcome, a crash in the middle, concurrent access) and the
/// conservation check of the M4 plan section 1 after every case: the sum of every item and gold over both characters is
/// unchanged, the trade's ledger rows are moves that sum to zero, and each character rebuilds from its audit ledger.
/// </summary>
[Trait("Category", "NoDuplication")]
public sealed class TransferPersistenceTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private InventoryRepository _inventory = null!;
    private TransferTestKit _kit = null!;
    private CharacterRecord _a = null!, _b = null!;
    private string _before = "";

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _inventory = new InventoryRepository(_realmDb);
        _kit = new TransferTestKit(_realmDb, _characters, _clock);
        await _kit.CreateMailDoubleAsync();
        _a = await _kit.CreateAsync(ash: 10, swords: 1, gold: 100);
        _b = await _kit.CreateAsync(ash: 95, gold: 500);
        _before = await _kit.HoldingsAsync(_a.CharacterId, _b.CharacterId);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    /// <summary>
    /// The trade of the WP's "done" line, applied as <c>TradeService</c> applies it: A gives 4 ash and its sword, B gives 50 gold,
    /// in a world holding both (a pending class 2 pickup of B rides along); the pair job as the scheduler builds it.
    /// </summary>
    private TransferSaveJob Trade(InventoryService world, string opKey = "trade:r:1", bool withPickup = true)
    {
        long a = _a.CharacterId, b = _b.CharacterId;
        if (withPickup)
            Assert.Equal(2, world.TryAdd(b, Item(Ash), 0, 2, $"{opKey}:loot", InventoryReason.LootPickup, "loot").Added);
        Assert.Equal(ReserveResult.Ok, world.Reserve(a, "offer:a", [new ReserveItem(new InventoryLocation(Normal, 0), 4),
            new ReserveItem(SwordAt(world, a), 1)]));
        Assert.Equal(TransferRefusal.None, world.TryTransfer(new TransferSide(a, "offer:a", 0), new TransferSide(b, null, 50), opKey,
            InventoryReason.Trade, 16, out _));
        return new TransferSaveJob(Job(world, _a), Job(world, _b), TransferRecord.Create(opKey, TransferKind.Trade, a, b, "{}"));
    }

    private async Task AssertConservedAsync(string opPrefix = "trade:")
    {
        // Nothing of value appeared or vanished over both characters, the escrows and the mail double.
        Assert.Equal(_before, await _kit.HoldingsAsync(_a.CharacterId, _b.CharacterId));
        await _kit.AssertGoldMovesAsync(opPrefix);
        await _kit.AssertRebuildsFromAuditAsync(_a.CharacterId);
        await _kit.AssertRebuildsFromAuditAsync(_b.CharacterId);
    }

    private async Task<int> AshOf(long id) => (await _inventory.LoadAsync(id)).Where(r => r.DefId == Ash).Sum(r => r.Count);

    [Fact]
    public async Task A_trade_commits_both_characters_and_the_transfer_row_in_one_transaction()
    {
        var world = await _kit.AttachAsync(_a.CharacterId, _b.CharacterId);
        var job = Trade(world, withPickup: false);

        var result = await _characters.SaveTransferAsync(job);

        Assert.True(result.Saved);
        Assert.Equal((_a.Version + 1, _b.Version + 1), (result.A.Version, result.B.Version));
        Assert.Equal((6, 99), (await AshOf(_a.CharacterId), await AshOf(_b.CharacterId)));
        Assert.Equal((150L, 450L), ((await _characters.LoadAsync(_a.CharacterId))!.Gold, (await _characters.LoadAsync(_b.CharacterId))!.Gold));
        Assert.Equal(1, await _kit.CountAsync("SELECT count(*) FROM transfer_ops WHERE op_key = 'trade:r:1' AND save_id_a = @a AND save_id_b = @b",
            new { a = job.A.SaveId, b = job.B.SaveId }));
        Assert.Equal(2, await _kit.CountAsync("SELECT count(*) FROM gold_ledger WHERE op_key LIKE 'trade:%' AND flow = 2"));
        Assert.Equal(0, await _kit.CountAsync("SELECT count(*) FROM gold_ledger WHERE op_key LIKE 'trade:%' AND flow <> 2")); // no faucet
        await AssertConservedAsync();

        // After a relogin the sword lies in B's bag with its owner history; both characters load.
        var relogin = await _kit.AttachAsync(_a.CharacterId, _b.CharacterId);
        var sword = SwordAt(relogin, _b.CharacterId);
        long instance = relogin.CurrentInventory(_b.CharacterId).At(sword).InstanceId;
        Assert.True(relogin.TryGetProperties(_b.CharacterId, instance, out var properties));
        Assert.Equal(new[] { _a.CharacterId }, properties.OwnerHistory);
        Assert.Equal(0, await _kit.CountAsync("SELECT count(*) FROM item_instances WHERE character_id = @a", new { a = _a.CharacterId }));
    }

    [Fact]
    public async Task A_double_confirm_saves_the_identical_pair_once()
    {
        var job = Trade(await _kit.AttachAsync(_a.CharacterId, _b.CharacterId));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _characters.SaveTransferAsync(job)));

        Assert.All(results, r => Assert.True(r.Saved));
        Assert.Equal(1, await _kit.CountAsync("SELECT count(*) FROM transfer_ops"));
        Assert.Equal((6, 101), (await AshOf(_a.CharacterId), await AshOf(_b.CharacterId)));
        _before = _before.Replace("demonAsh, 0, 105)", "demonAsh, 0, 107)"); // the pickup's 2 ash rode along
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_retry_after_an_unknown_outcome_is_recognised_by_both_save_ids()
    {
        var job = Trade(await _kit.AttachAsync(_a.CharacterId, _b.CharacterId), withPickup: false);
        Assert.True((await _characters.SaveTransferAsync(job)).Saved); // committed, but the world never heard

        var retry = await _characters.SaveTransferAsync(job);

        Assert.True(retry.Saved);
        Assert.Equal((_a.Version + 1, _b.Version + 1), (retry.A.Version, retry.B.Version));
        Assert.Equal((150L, 450L), ((await _characters.LoadAsync(_a.CharacterId))!.Gold, (await _characters.LoadAsync(_b.CharacterId))!.Gold));
        await AssertConservedAsync();
    }

    [Theory]
    [InlineData("gold_ledger")]
    [InlineData("audit_log")]
    public async Task A_crash_between_the_two_characters_rows_writes_both_or_neither(string table)
    {
        var job = Trade(await _kit.AttachAsync(_a.CharacterId, _b.CharacterId), withPickup: false);
        // A's row, keys, items and ledger are written first (lower id); the crash hits B's rows.
        long second = Math.Max(_a.CharacterId, _b.CharacterId);
        await _kit.CrashOnAsync(table, second);

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveTransferAsync(job));

        Assert.Equal(_before, await _kit.HoldingsAsync(_a.CharacterId, _b.CharacterId));
        Assert.Equal(_a.Version, (await _characters.LoadAsync(_a.CharacterId))!.Version); // neither written
        Assert.Equal(_b.Version, (await _characters.LoadAsync(_b.CharacterId))!.Version);
        Assert.Equal(0, await _kit.CountAsync("SELECT count(*) FROM transfer_ops"));
        Assert.Equal((10, 95), (await AshOf(_a.CharacterId), await AshOf(_b.CharacterId)));

        await _kit.NoCrashAsync(table);
        Assert.True((await _characters.SaveTransferAsync(job)).Saved); // the identical retry applies once
        Assert.Equal((6, 99), (await AshOf(_a.CharacterId), await AshOf(_b.CharacterId)));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_version_conflict_on_the_second_character_rolls_back_the_first()
    {
        var job = Trade(await _kit.AttachAsync(_a.CharacterId, _b.CharacterId), withPickup: false);
        // Another writer saved the higher-id character meanwhile (its version moved).
        var second = _a.CharacterId > _b.CharacterId ? _a : _b;
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(second.CharacterId, second.State, second.Version, Guid.NewGuid())).Status);

        var result = await _characters.SaveTransferAsync(job);

        Assert.False(result.Saved);
        var (first, other) = second == _a ? (result.B, result.A) : (result.A, result.B);
        Assert.Equal(SaveStatus.VersionConflict, other.Status);
        Assert.NotEqual(SaveStatus.Saved, first.Status); // neither side is taken as saved: both fault
        var lower = second == _a ? _b : _a;
        Assert.Equal(lower.Version, (await _characters.LoadAsync(lower.CharacterId))!.Version);
        Assert.Equal(_before, await _kit.HoldingsAsync(_a.CharacterId, _b.CharacterId));
        Assert.Equal(0, await _kit.CountAsync("SELECT count(*) FROM transfer_ops"));
    }

    [Fact]
    public async Task A_transfer_key_applied_before_writes_nothing_for_either()
    {
        var job = Trade(await _kit.AttachAsync(_a.CharacterId, _b.CharacterId), withPickup: false);
        await _kit.ExecuteAsync("""
            INSERT INTO transfer_ops (op_key, kind, character_a, character_b, save_id_a, save_id_b, summary, created_at)
            VALUES ('trade:r:1', 1, @a, @b, gen_random_uuid(), gen_random_uuid(), '{}', now())
            """, new { a = Math.Min(_a.CharacterId, _b.CharacterId), b = Math.Max(_a.CharacterId, _b.CharacterId) });

        var result = await _characters.SaveTransferAsync(job);

        Assert.Equal((SaveStatus.DuplicateOperation, SaveStatus.DuplicateOperation), (result.A.Status, result.B.Status));
        Assert.Equal(_before, await _kit.HoldingsAsync(_a.CharacterId, _b.CharacterId));
        Assert.Equal(_a.Version, (await _characters.LoadAsync(_a.CharacterId))!.Version);
    }

    [Fact]
    public async Task An_operation_key_of_either_side_applied_before_writes_nothing_for_either()
    {
        var job = Trade(await _kit.AttachAsync(_a.CharacterId, _b.CharacterId), withPickup: false);
        string key = job.B.Inventory!.Ops[0].OpKey;
        await _kit.ExecuteAsync("""
            INSERT INTO inventory_ops (op_key, character_id, reason, source, save_id, created_at)
            VALUES (@key, @b, 'Trade', 'test', gen_random_uuid(), now())
            """, new { key, b = _b.CharacterId });

        var result = await _characters.SaveTransferAsync(job);

        Assert.Equal((SaveStatus.DuplicateOperation, SaveStatus.DuplicateOperation), (result.A.Status, result.B.Status));
        Assert.Equal(_before, await _kit.HoldingsAsync(_a.CharacterId, _b.CharacterId));
        Assert.Equal(0, await _kit.CountAsync("SELECT count(*) FROM transfer_ops"));
    }

    [Fact]
    public async Task A_second_world_saving_one_side_concurrently_has_one_winner_and_nothing_duplicates()
    {
        var job = Trade(await _kit.AttachAsync(_a.CharacterId, _b.CharacterId), withPickup: false);
        // A second world that also thinks it owns A sells nothing but saves A's row from the same version.
        var rival = _characters.SaveAsync(_a.CharacterId, _a.State, _a.Version, Guid.NewGuid());
        var pair = _characters.SaveTransferAsync(job);

        await Task.WhenAll(rival, pair);
        var (rivalResult, pairResult) = (await rival, await pair); // both done: awaiting only reads the results

        Assert.True(rivalResult.Status == SaveStatus.Saved ^ pairResult.Saved); // exactly one wins
        long[] ids = [_a.CharacterId, _b.CharacterId];
        if (pairResult.Saved)
            await AssertConservedAsync();
        else
            Assert.Equal(_before, await _kit.HoldingsAsync(ids));
    }

    [Fact]
    public async Task Two_transfers_sharing_a_character_never_deadlock_and_one_wins()
    {
        var c = await _kit.CreateAsync(gold: 300);
        _before = await _kit.HoldingsAsync(_a.CharacterId, _b.CharacterId, c.CharacterId);
        var worldOne = await _kit.AttachAsync(_a.CharacterId, _b.CharacterId);
        var first = Trade(worldOne, "trade:r:1", withPickup: false);
        // A rival world trades B with C from the same loaded version of B (B's gold to C).
        var worldTwo = await _kit.AttachAsync(_b.CharacterId, c.CharacterId);
        Assert.Equal(TransferRefusal.None, worldTwo.TryTransfer(new TransferSide(c.CharacterId, null, 0), new TransferSide(_b.CharacterId, null, 70),
            "trade:r:2", InventoryReason.Trade, 16, out _));
        var second = new TransferSaveJob(Job(worldTwo, c), Job(worldTwo, _b),
            TransferRecord.Create("trade:r:2", TransferKind.Trade, c.CharacterId, _b.CharacterId, "{}"));

        var results = await Task.WhenAll(_characters.SaveTransferAsync(first), _characters.SaveTransferAsync(second));

        Assert.Equal(1, results.Count(r => r.Saved)); // no deadlock exception; the loser saw B's version move
        Assert.Equal(_before, await _kit.HoldingsAsync(_a.CharacterId, _b.CharacterId, c.CharacterId));
        await _kit.AssertGoldMovesAsync("trade:");
        foreach (long id in new[] { _a.CharacterId, _b.CharacterId, c.CharacterId })
            await _kit.AssertRebuildsFromAuditAsync(id);
    }
}
