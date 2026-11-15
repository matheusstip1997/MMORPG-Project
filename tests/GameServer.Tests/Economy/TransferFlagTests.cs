using Dapper;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Telemetry;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;
using Shared.Simulation.Economy;

namespace GameServer.Tests.Economy;

/// <summary>
/// Large one-way transfers flagged (WP-M4-15; spec 4.5 R-4.5-02, 7.6; M4 decision 17) in a real PostgreSQL: the scan over the
/// committed trades (<c>transfer_ops</c>) and auction or stall purchases (<c>listing_sales</c>) sums each pair of accounts'
/// UTC day, flags it over a threshold with one <c>transfer_flags</c> row and one <c>TransferFlagged</c> record per newly
/// crossed threshold, and never changes a transfer. The scan's rule 5 tests: a transfer is counted once over a double scan,
/// a retry after a crash in the middle and two concurrent scanners; a character's own alts (one account) are left out.
/// </summary>
[Trait("Category", "NoDuplication")]
public sealed class TransferFlagTests : IAsyncLifetime
{
    private const long Now = 1_800_000_000_000; // FakeClock's start: 2027-01-15 08:00 UTC
    private const long DayMs = 86_400_000;
    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private static readonly TransferFlagSettings Settings = new()
    {
        GoldPerPairDay = 1_000, ValuePerPairDay = 2_000, OneSidedGoldPercent = 10, ScanIntervalMs = 30_000, ScanBatchMax = 100, LookbackMs = 2 * DayMs,
    };

    /// <summary>Ash (net id 2) is worth 5 a unit, a sword (net id 3) 600 (60 x the rare 1,000 %).</summary>
    private static readonly ItemValue Values = new(
        [
            new ItemDef { Id = "m1.demonAsh", NetId = 2, Category = ItemCategory.Material },
            new ItemDef { Id = "m1.sword", NetId = 3, Category = ItemCategory.Equipment },
        ],
        [
            new ItemValueBand { Id = "material.t0", Category = ItemCategory.Material, Tier = 0, GoldPerUnit = 5 },
            new ItemValueBand { Id = "equipment.t0", Category = ItemCategory.Equipment, Tier = 0, GoldPerUnit = 60 },
        ],
        [],
        [new RarityDef { Id = "common", Tier = 0 }, new RarityDef { Id = "legendary", Tier = 4 }],
        [new RarityMult { RarityId = "common", PricePercent = 100 }, new RarityMult { RarityId = "legendary", PricePercent = 1_000 }]);

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new(Now);
    private NpgsqlDataSource _realmDb = null!;
    private TransferFlagRepository _flags = null!;
    private long _ann, _bob, _bobAlt, _cid; // Ann (account 10), Bob and his alt (account 20), Cid (account 30)
    private int _nextOp;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _flags = new TransferFlagRepository(_realmDb, _clock);
        var characters = new CharacterRepository(_realmDb, 1, _clock);
        async Task<long> Create(long account, int slot, string name) =>
            (await characters.CreateAsync(new NewCharacter(account, slot, name, "warrior", Spawn, Guid.NewGuid()))).Character!.CharacterId;
        _ann = await Create(10, 0, "Ann");
        _bob = await Create(20, 0, "Bob");
        _bobAlt = await Create(20, 1, "Bobalt");
        _cid = await Create(30, 0, "Cid");
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    /// <summary>A committed trade as <c>CharacterRepository.SaveTransferAsync</c> writes its row (the summary's "a" the lower id).</summary>
    private async Task<string> TradeAsync(long from, long to, long gold, string items = "", long atMs = Now - 60_000, long goldBack = 0)
    {
        string op = $"trade:test:{_nextOp++}";
        bool fromIsA = from < to;
        string side = $$"""{"items":[{{items}}],"gold":{{gold}}}""", back = $$"""{"items":[],"gold":{{goldBack}}}""";
        string summary = fromIsA ? $$"""{"aToB":{{side}},"bToA":{{back}}}""" : $$"""{"aToB":{{back}},"bToA":{{side}}}""";
        await using var connection = await _realmDb.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO transfer_ops (op_key, kind, character_a, character_b, save_id_a, save_id_b, summary, created_at)
            VALUES (@op, 1, @a, @b, gen_random_uuid(), gen_random_uuid(), @summary::jsonb, @at)
            """, new { op, a = Math.Min(from, to), b = Math.Max(from, to), summary, at = DateTime.UnixEpoch.AddMilliseconds(atMs) });
        return op;
    }

    /// <summary>A purchase of <paramref name="count"/> units of a listing by <paramref name="buyer"/> from <paramref name="seller"/>.</summary>
    private async Task SaleAsync(long seller, long buyer, string defId, int netId, int rarity, int count, long price, long tax, long atMs = Now - 30_000)
    {
        int n = ++_nextOp;
        var at = DateTime.UnixEpoch.AddMilliseconds(atMs);
        await using var connection = await _realmDb.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO listings (listing_id, create_op_key, kind, seller_id, escrow_id, def_id, item_net_id, category, rarity, min_level,
                job_net_id, armor_tier, unit_price, count_initial, count_left, fee_paid, created_at, expires_at, state, create_save_id)
            VALUES (@n, 'list:' || @n, 1, @seller, 'escrow:' || @n, @defId, @netId, 1, @rarity, 0, 0, 0, @unit, @count + 1, 1, 0, @at, @at + interval '1 day', 1,
                gen_random_uuid());
            INSERT INTO listing_sales (op_key, listing_id, buyer_id, count, unit_price, price, tax, to_mail, save_id, sold_at)
            VALUES ('buy:' || @n, @n, @buyer, @count, @unit, @price, @tax, false, gen_random_uuid(), @at);
            """, new { n = (long)n, seller, buyer, defId, netId, rarity = (short)rarity, unit = price / count, count, price, tax, at });
    }

    private async Task<IReadOnlyList<TransferFlagRow>> FlagsAsync() => await _flags.ListAsync(TransferFlagMath.UtcDay(Now) - 5, 100);

    [Fact]
    public async Task A_trade_over_the_gold_threshold_is_flagged_once_with_its_telemetry_and_the_transfer_is_untouched()
    {
        string op = await TradeAsync(_ann, _bob, 1_200);
        var before = await TransferRowAsync(op);
        var queue = new TelemetryQueue(64);
        var worker = new TransferFlagWorker(_flags, Settings, Values, queue, _clock, 1);

        var scan = await worker.ScanOnceAsync();

        Assert.Equal(1, scan.Counted);
        var crossing = Assert.Single(scan.Crossings);
        Assert.Equal((10L, 20L, _ann, _bob), (crossing.GiverAccount, crossing.ReceiverAccount, crossing.GiverCharacter, crossing.ReceiverCharacter));
        Assert.Equal((1_200L, 1_200L, TransferFlagMath.GoldCrossed), (crossing.NetGold, crossing.NetValue, crossing.NewlyCrossed));
        var flag = Assert.Single(await FlagsAsync());
        Assert.Equal(("Ann", "Bob", 1_200L, 1_200L, 1, 1), (flag.GiverName, flag.ReceiverName, flag.NetGold, flag.NetValue, flag.Crossed, flag.Transfers));
        Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(Now).ToString("yyyy-MM-dd"), flag.Day);
        var records = new TelemetryRecord[8];
        Assert.Equal(1, queue.DrainTo(records));
        Assert.Equal(TelemetryKind.TransferFlagged, records[0].Kind);
        Assert.Equal((_ann, 10L, _bob, 1_200L, 1_200L, 1L, 20L, TransferFlagMath.UtcDay(Now)),
            (records[0].CharacterId, records[0].AccountId, records[0].Target, records[0].V0, records[0].V1, records[0].V2, records[0].V3, records[0].Subject));

        // A double scan counts nothing twice and records nothing again; the transfer's row is exactly as it was committed.
        var again = await worker.ScanOnceAsync();
        Assert.Equal((0, 0), (again.Counted, again.Crossings.Count));
        Assert.Equal(0, queue.DrainTo(records));
        Assert.Equal(before, await TransferRowAsync(op));
    }

    [Fact]
    public async Task A_day_under_both_thresholds_is_not_flagged_until_a_later_transfer_of_that_day_crosses()
    {
        await TradeAsync(_ann, _bob, 600, atMs: Now - 120_000);
        Assert.Empty((await _flags.ScanAsync(Settings, Values)).Crossings);
        Assert.Empty(await FlagsAsync());

        // Bob pays 100 back, then Ann gives 700 more and 40 ash (200 of value): net gold 1,200 one way, net value 1,400.
        await TradeAsync(_bob, _ann, 100, atMs: Now - 90_000);
        await TradeAsync(_ann, _bob, 700, "{\"item\":2,\"rarity\":0,\"count\":40,\"instance\":0}", Now - 60_000);
        var scan = await _flags.ScanAsync(Settings, Values);

        Assert.Equal(2, scan.Counted);
        var crossing = Assert.Single(scan.Crossings);
        Assert.Equal((1_200L, 1_400L, TransferFlagMath.GoldCrossed, 3), (crossing.NetGold, crossing.NetValue, crossing.NewlyCrossed, crossing.Transfers));

        // A sword given away the same day crosses the value threshold too: one more record, the same flag row.
        await TradeAsync(_ann, _bob, 0, "{\"item\":3,\"rarity\":4,\"count\":1,\"instance\":77}", Now - 30_000);
        var more = await _flags.ScanAsync(Settings, Values);
        Assert.Equal(TransferFlagMath.ValueCrossed, Assert.Single(more.Crossings).NewlyCrossed);
        var flag = Assert.Single(await FlagsAsync());
        Assert.Equal((3, 2_000L, 4), (flag.Crossed, flag.NetValue, flag.Transfers));
    }

    [Fact]
    public async Task Another_day_is_another_pair_day_and_an_alt_of_the_same_account_is_left_out()
    {
        await TradeAsync(_ann, _bob, 600, atMs: Now - DayMs);
        await TradeAsync(_ann, _bob, 600);
        await TradeAsync(_bob, _bobAlt, 5_000); // one account: R-4.5-02 flags transfers between accounts
        var scan = await _flags.ScanAsync(Settings, Values);
        Assert.Equal((3, 1), (scan.Counted, scan.SameAccount));
        Assert.Empty(scan.Crossings);
        Assert.Empty(await FlagsAsync());
        await using var connection = await _realmDb.OpenConnectionAsync();
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM transfer_pair_days"));
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM transfer_flag_ops"));
    }

    [Fact]
    public async Task A_purchase_counts_the_gold_that_reached_the_seller_against_the_units_value()
    {
        // Cid buys one ash from Ann for 3,000 (150 tax): 2,850 reached Ann, 5 of value went to Cid. Net gold and value from Cid.
        await SaleAsync(_ann, _cid, "m1.demonAsh", 2, 0, 1, 3_000, 150);
        var crossing = Assert.Single((await _flags.ScanAsync(Settings, Values)).Crossings);
        Assert.Equal((30L, 10L, _cid, _ann), (crossing.GiverAccount, crossing.ReceiverAccount, crossing.GiverCharacter, crossing.ReceiverCharacter));
        Assert.Equal((2_850L, 2_845L, 3), (crossing.NetGold, crossing.NetValue, crossing.NewlyCrossed));

        // An ordinary purchase (1,100 gold for a sword worth 600) is not flagged: the gold is not one-sided (the user's answer of
        // 2026-11-15: 600 came back, at least 10 % of the net gold) and the net value (500) is under its threshold.
        await SaleAsync(_bob, _ann, "m1.sword", 3, 4, 1, 1_100, 0);
        var fair = await _flags.ScanAsync(Settings, Values);
        Assert.Equal(1, fair.Counted);
        Assert.Empty(fair.Crossings);
        Assert.DoesNotContain(await FlagsAsync(), f => f.GiverAccount == 10 && f.ReceiverAccount == 20);
    }

    [Fact]
    public async Task Transfers_older_than_the_look_back_are_not_read_and_a_full_batch_leaves_the_rest_for_the_next_scan()
    {
        await TradeAsync(_ann, _bob, 5_000, atMs: Now - 3 * DayMs);
        for (int i = 0; i < 3; i++)
            await TradeAsync(_ann, _cid, 100, atMs: Now - 1_000 * (10 - i));
        var small = new TransferFlagSettings
        {
            GoldPerPairDay = 1_000, ValuePerPairDay = 2_000, OneSidedGoldPercent = 10, ScanIntervalMs = 30_000, ScanBatchMax = 2, LookbackMs = 2 * DayMs,
        };
        Assert.Equal(2, (await _flags.ScanAsync(small, Values)).Counted);
        Assert.Equal(1, (await _flags.ScanAsync(small, Values)).Counted);
        Assert.Equal(0, (await _flags.ScanAsync(small, Values)).Counted);
    }

    [Fact]
    public async Task A_crash_in_the_middle_of_a_scan_writes_nothing_and_the_retry_counts_each_transfer_once()
    {
        await TradeAsync(_ann, _bob, 800, atMs: Now - 90_000);
        await TradeAsync(_ann, _cid, 1_500, atMs: Now - 60_000);
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            // The second pair-day write fails after the first transfer was counted in the same transaction.
            await connection.ExecuteAsync("""
                CREATE FUNCTION fail_pair() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF NEW.account_high = 30 THEN RAISE EXCEPTION 'crash'; END IF; RETURN NEW; END $$;
                CREATE TRIGGER fail_pair BEFORE INSERT ON transfer_pair_days FOR EACH ROW EXECUTE FUNCTION fail_pair();
                """);
        }
        await Assert.ThrowsAsync<PostgresException>(() => _flags.ScanAsync(Settings, Values));
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM transfer_flag_ops"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM transfer_pair_days"));
            await connection.ExecuteAsync("DROP TRIGGER fail_pair ON transfer_pair_days; DROP FUNCTION fail_pair();");
        }

        var retry = await _flags.ScanAsync(Settings, Values);
        Assert.Equal(2, retry.Counted);
        var flag = Assert.Single(await FlagsAsync());
        Assert.Equal(("Ann", "Cid", 1_500L, 1), (flag.GiverName, flag.ReceiverName, flag.NetGold, flag.Transfers));
        Assert.Equal(0, (await _flags.ScanAsync(Settings, Values)).Counted);
    }

    /// <summary>A <c>transfer_ops</c> row with a raw summary and kind, as any writer of the shared table might leave it.</summary>
    private async Task<string> RawTransferAsync(short kind, string summary, long atMs)
    {
        string op = $"raw:test:{_nextOp++}";
        await using var connection = await _realmDb.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO transfer_ops (op_key, kind, character_a, character_b, save_id_a, save_id_b, summary, created_at)
            VALUES (@op, @kind, @a, @b, gen_random_uuid(), gen_random_uuid(), @summary::jsonb, @at)
            """, new { op, kind, a = Math.Min(_ann, _bob), b = Math.Max(_ann, _bob), summary, at = DateTime.UnixEpoch.AddMilliseconds(atMs) });
        return op;
    }

    [Fact]
    public async Task A_row_the_scan_cannot_read_is_counted_with_nothing_added_and_the_rows_after_it_are_counted()
    {
        // PR #258 review: one unreadable row failed every scan (it stayed the oldest uncounted row until it left the look-back).
        string notObject = await RawTransferAsync(1, "\"x\"", Now - 90_000);
        string oddFields = await RawTransferAsync(1, """{"aToB":{"gold":"lots","items":[3,{"item":"a","count":2}]},"bToA":null}""", Now - 80_000);
        await RawTransferAsync(9, """{"aToB":{"items":[],"gold":99999},"bToA":{"items":[],"gold":0}}""", Now - 70_000); // not a trade
        await TradeAsync(_ann, _cid, 1_500, atMs: Now - 60_000);
        var log = new List<string>();
        var worker = new TransferFlagWorker(_flags, Settings, Values, null, _clock, 1, log.Add);

        var scan = await worker.ScanOnceAsync();

        Assert.Equal(3, scan.Counted); // the two odd trades and the good one; the other kind is not read as a trade
        Assert.Equal([notObject], scan.Unreadable); // the odd fields read as nothing: no gold, no item of value
        Assert.Single(log, l => l.Contains(notObject, StringComparison.Ordinal));
        Assert.DoesNotContain(oddFields, scan.Unreadable);
        var crossing = Assert.Single(scan.Crossings);
        Assert.Equal((_ann, _cid, 1_500L), (crossing.GiverCharacter, crossing.ReceiverCharacter, crossing.NetGold));
        Assert.Empty((await _flags.ScanAsync(Settings, Values)).Unreadable); // counted once, never read again
        await using var connection = await _realmDb.OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT transfers FROM transfer_pair_days WHERE account_low = 10 AND account_high = 20")); // the odd-fields trade, nothing in it
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
            "SELECT gold_low_to_high + gold_high_to_low + item_value_low_to_high + item_value_high_to_low FROM transfer_pair_days WHERE account_low = 10 AND account_high = 20"));
    }

    [Fact]
    public async Task A_flag_shows_the_days_current_net_when_the_pair_is_back_under_the_thresholds()
    {
        // PR #258 review: A gives B 6,000 (flagged), then B gives the 6,000 back: the row shows net 0 over 2 transfers, crossed kept.
        await TradeAsync(_ann, _bob, 6_000, atMs: Now - 90_000);
        Assert.Single((await _flags.ScanAsync(Settings, Values)).Crossings);
        await TradeAsync(_bob, _ann, 6_000, atMs: Now - 60_000);

        var scan = await _flags.ScanAsync(Settings, Values);

        Assert.Empty(scan.Crossings);
        var flag = Assert.Single(await FlagsAsync());
        Assert.Equal((0L, 0L, 2, TransferFlagMath.GoldCrossed | TransferFlagMath.ValueCrossed),
            (flag.NetGold, flag.NetValue, flag.Transfers, flag.Crossed));
    }

    [Fact]
    public async Task Two_scanners_at_once_count_each_transfer_once()
    {
        for (int i = 0; i < 20; i++)
            await TradeAsync(i % 2 == 0 ? _ann : _bob, i % 2 == 0 ? _bob : _ann, 100 + i, atMs: Now - 100_000 + i);
        var other = new TransferFlagRepository(_realmDb, _clock);

        var scans = await Task.WhenAll(_flags.ScanAsync(Settings, Values), other.ScanAsync(Settings, Values), _flags.ScanAsync(Settings, Values));

        Assert.Equal(20, scans.Sum(s => s.Counted));
        await using var connection = await _realmDb.OpenConnectionAsync();
        var day = await connection.QuerySingleAsync<(long LowToHigh, long HighToLow, int Transfers)>(
            "SELECT gold_low_to_high, gold_high_to_low, transfers FROM transfer_pair_days");
        Assert.Equal((Enumerable.Range(0, 20).Where(i => i % 2 == 0).Sum(i => 100L + i), Enumerable.Range(0, 20).Where(i => i % 2 == 1).Sum(i => 100L + i), 20),
            (day.LowToHigh, day.HighToLow, day.Transfers));
    }

    private async Task<string> TransferRowAsync(string op)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<string>(
            "SELECT concat_ws('|', kind, character_a, character_b, save_id_a, save_id_b, summary::text, created_at) FROM transfer_ops WHERE op_key = @op",
            new { op }) ?? "";
    }
}
