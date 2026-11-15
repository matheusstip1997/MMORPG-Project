using BotClient.LoadTest;
using Dapper;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;

namespace GameServer.Tests.Economy;

/// <summary>
/// The dashboard's economy data in a real PostgreSQL (WP-M4-5; spec 7.1, 7.2 R-7.2-02, 7.4): faucet and sink totals per UTC
/// hour and by reason from a seeded <c>gold_ledger</c> (moves never count; the hour boundary; the window's edges), the same
/// figures as the BotClient report over matching telemetry, and the <c>faucet_sink_snapshots</c> rows (written once, read
/// back with their reasons).
/// </summary>
public sealed class EconomyRepositoryTests : IAsyncLifetime
{
    private const long H0 = 1_800_000_000_000; // a UTC hour start
    private const long Hour = EconomyRepository.HourMs;
    private const short Faucet = 0, Sink = 1, Move = 2;
    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private NpgsqlDataSource _realmDb = null!;
    private EconomyRepository _economy = null!;
    private long _character;
    private int _nextOp;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _economy = new EconomyRepository(_realmDb);
        var created = await new CharacterRepository(_realmDb, 1, new FakeClock())
            .CreateAsync(new NewCharacter(1, 0, "Ledger", "warrior", Spawn, Guid.NewGuid()));
        _character = created.Character!.CharacterId;
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    /// <summary>One gold change as a <c>gold_ledger</c> row (with its <c>inventory_ops</c> row), written at <paramref name="atMs"/>.</summary>
    private async Task LedgerAsync(long atMs, long delta, short flow, string reason)
    {
        string op = $"seed:{_nextOp++}";
        var at = DateTime.UnixEpoch.AddMilliseconds(atMs);
        await using var connection = await _realmDb.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO inventory_ops (op_key, character_id, reason, source, save_id, created_at)
            VALUES (@op, @character, @reason, 'test', @save, @at);
            INSERT INTO gold_ledger (op_key, character_id, delta, balance_after, flow, reason, source, save_id, created_at)
            VALUES (@op, @character, @delta, 1000, @flow, @reason, 'test', @save, @at);
            """, new { op, character = _character, reason, save = Guid.NewGuid(), at, delta, flow });
    }

    /// <summary>A ledger over three hours: faucets, sinks and a trade's two moves, an Admin grant and charge, rows outside the window.</summary>
    private async Task SeedAsync()
    {
        await LedgerAsync(H0 - 1, 999, Faucet, nameof(InventoryReason.OutpostSale)); // the hour before
        await LedgerAsync(H0 + 1_000, 100, Faucet, nameof(InventoryReason.OutpostSale));
        await LedgerAsync(H0 + 2_000, -25, Sink, nameof(InventoryReason.VendorPurchase));
        await LedgerAsync(H0 + 3_000, 500, Move, "Trade"); // gold between two players: neither (R-4.5-02)
        await LedgerAsync(H0 + 3_000, -500, Move, "Trade");
        await LedgerAsync(H0 + Hour - 1, 7, Faucet, nameof(InventoryReason.Admin)); // hh:59:59.999
        await LedgerAsync(H0 + Hour, -3, Sink, nameof(InventoryReason.Admin)); // hh+1:00:00.000
        await LedgerAsync(H0 + Hour + 10, 40, Faucet, nameof(InventoryReason.OutpostSale));
        await LedgerAsync(H0 + 2 * Hour + 5, -50, Sink, nameof(InventoryReason.VendorPurchase));
        await LedgerAsync(H0 + 3 * Hour, 11, Faucet, nameof(InventoryReason.OutpostSale)); // the hour after
    }

    [Fact]
    public async Task Hourly_totals_sum_faucets_and_sinks_by_reason_and_never_count_moves()
    {
        await SeedAsync();

        var hours = await _economy.HourlyTotalsAsync(H0, H0 + 3 * Hour);

        Assert.Equal([H0, H0 + Hour, H0 + 2 * Hour], hours.Select(h => h.HourStartMs));
        Assert.Equal((107L, 25L), (hours[0].GoldCreated, hours[0].GoldDestroyed));
        Assert.Equal(new Dictionary<string, long> { ["admin"] = 7, ["outpostSale"] = 100 }, hours[0].CreatedByReason);
        Assert.Equal(new Dictionary<string, long> { ["vendorPurchase"] = 25 }, hours[0].DestroyedByReason);
        Assert.Equal((40L, 3L), (hours[1].GoldCreated, hours[1].GoldDestroyed));
        Assert.Equal(new Dictionary<string, long> { ["admin"] = 3 }, hours[1].DestroyedByReason);
        Assert.Equal((0L, 50L), (hours[2].GoldCreated, hours[2].GoldDestroyed));
        Assert.DoesNotContain(hours, h => h.CreatedByReason.ContainsKey("trade") || h.DestroyedByReason.ContainsKey("trade"));
    }

    /// <summary>A mail of <paramref name="kind"/> with a <c>mail_ops</c> row of <paramref name="opKind"/> moving <paramref name="gold"/> at <paramref name="atMs"/>.</summary>
    private async Task MailOpAsync(long atMs, short kind, MailOpKind opKind, long gold)
    {
        string op = $"mailseed:{_nextOp++}";
        var at = DateTime.UnixEpoch.AddMilliseconds(atMs);
        await using var connection = await _realmDb.OpenConnectionAsync();
        long mailId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO mail (recipient_id, kind, subject_id, gold, state, created_at, expires_at, closed_at, send_op_key, source)
            VALUES (@character, @kind, 'mail.subject.test', 0, 2, @created, @at, @at, @op, 'test')
            RETURNING mail_id
            """, new { character = _character, kind, created = at.AddDays(-30), at, op = op + ":send" });
        await connection.ExecuteAsync("""
            INSERT INTO mail_ops (op_key, kind, mail_id, character_id, gold, created_at) VALUES (@op, @opKind, @mailId, @character, @gold, @at)
            """, new { op, opKind = (short)opKind, mailId, character = _character, gold, at });
    }

    [Fact]
    public async Task Expired_mail_gold_is_a_sink_of_its_hour_but_an_admin_compensations_and_a_claim_are_not()
    {
        // WP-M4-15: the gold of an expired mail never had a character balance, so it is in mail_ops, not gold_ledger; the dashboard
        // counts it as the sink mailExpired, as EconomyLedger and the report do. A compensation's expiry destroyed nothing counted.
        await SeedAsync();
        await MailOpAsync(H0 + 4_000, (short)Shared.Protocol.MailKind.ListingProceeds, MailOpKind.Expire, 30);
        await MailOpAsync(H0 + Hour + 5, (short)Shared.Protocol.MailKind.ListingReturn, MailOpKind.Expire, 0); // items only
        await MailOpAsync(H0 + Hour + 6, (short)Shared.Protocol.MailKind.AdminCompensation, MailOpKind.Expire, 900);
        await MailOpAsync(H0 + Hour + 7, (short)Shared.Protocol.MailKind.ListingProceeds, MailOpKind.Claim, 40); // a move
        await MailOpAsync(H0 + 2 * Hour + 9, (short)Shared.Protocol.MailKind.LootOverflow, MailOpKind.Expire, 8);

        var hours = await _economy.HourlyTotalsAsync(H0, H0 + 3 * Hour);

        Assert.Equal(new Dictionary<string, long> { ["mailExpired"] = 30, ["vendorPurchase"] = 25 }, hours[0].DestroyedByReason);
        Assert.Equal((107L, 55L), (hours[0].GoldCreated, hours[0].GoldDestroyed));
        Assert.Equal(new Dictionary<string, long> { ["admin"] = 3 }, hours[1].DestroyedByReason);
        Assert.Equal(new Dictionary<string, long> { ["mailExpired"] = 8, ["vendorPurchase"] = 50 }, hours[2].DestroyedByReason);
        var figures = EconomyDashboard.Build(hours, []);
        Assert.Equal(38, figures.DestroyedByReason["mailExpired"]);
        Assert.Equal(25 + 30 + 3 + 50 + 8, figures.GoldDestroyed);
    }

    [Fact]
    public async Task The_window_includes_its_start_and_excludes_its_end()
    {
        await SeedAsync();

        var first = await _economy.HourlyTotalsAsync(H0, H0 + Hour);
        Assert.Equal((107L, 25L), (Assert.Single(first).GoldCreated, first[0].GoldDestroyed)); // hh+1:00 not in it
        var last = await _economy.HourlyTotalsAsync(H0 + Hour, H0 + Hour + 1);
        Assert.Equal((0L, 3L), (Assert.Single(last).GoldCreated, last[0].GoldDestroyed));
        Assert.Empty(await _economy.HourlyTotalsAsync(H0 + 4 * Hour, H0 + 5 * Hour));
    }

    [Fact]
    public async Task The_ledger_totals_equal_the_economy_report_over_the_same_changes()
    {
        // The telemetry the server writes for those changes (GoldChanged: delta, reason, flow), and the report over it.
        await SeedAsync();
        using var directory = new TempDirectory();
        Directory.CreateDirectory(directory.Path);
        var lines = new List<string> { """{"schema":"mmorpg.telemetry","v":1,"run":"seed","startedMs":1,"file":0}""" };
        void Gold(long t, long delta, InventoryReason reason, short flow) => lines.Add(
            $$"""{"k":"GoldChanged","t":{{t}},"delta":{{delta}},"balance":1000,"reason":{{(int)reason}},"flow":{{flow}}}""");
        Gold(H0 + 1_000, 100, InventoryReason.OutpostSale, Faucet);
        Gold(H0 + 2_000, -25, InventoryReason.VendorPurchase, Sink);
        Gold(H0 + 3_000, 500, (InventoryReason)99, Move);
        Gold(H0 + 3_000, -500, (InventoryReason)99, Move);
        Gold(H0 + Hour - 1, 7, InventoryReason.Admin, Faucet);
        Gold(H0 + Hour, -3, InventoryReason.Admin, Sink);
        Gold(H0 + Hour + 10, 40, InventoryReason.OutpostSale, Faucet);
        Gold(H0 + 2 * Hour + 5, -50, InventoryReason.VendorPurchase, Sink);
        // WP-M4-15: expired mail (MailExpired: kind, gold) and its mail_ops rows; the compensation's left out on both sides.
        void Expired(long t, Shared.Protocol.MailKind kind, long gold) => lines.Add(
            $$"""{"k":"MailExpired","t":{{t}},"mail":1,"recipient":1,"kind":{{(int)kind}},"gold":{{gold}},"units":0}""");
        Expired(H0 + 4_000, Shared.Protocol.MailKind.ListingProceeds, 30);
        Expired(H0 + Hour + 6, Shared.Protocol.MailKind.AdminCompensation, 900);
        await MailOpAsync(H0 + 4_000, (short)Shared.Protocol.MailKind.ListingProceeds, MailOpKind.Expire, 30);
        await MailOpAsync(H0 + Hour + 6, (short)Shared.Protocol.MailKind.AdminCompensation, MailOpKind.Expire, 900);
        await File.WriteAllLinesAsync(Path.Combine(directory.Path, "seed-0000.jsonl"), lines);

        var report = EconomyReport.Read(directory.Path);

        EconomyDashboardData.AssertSameHours(await _economy.HourlyTotalsAsync(H0, H0 + 3 * Hour), report);
    }

    [Fact]
    public async Task A_snapshot_row_is_written_once_and_read_back_with_its_reasons()
    {
        var snapshot = new EconomySnapshot(H0 + 60_000, H0, 150, 25, 125, 3,
            [new EconomyReasonDelta(InventoryReason.OutpostSale, 150, 0, 20), new EconomyReasonDelta(InventoryReason.VendorPurchase, 0, 25, 0)]);

        await _economy.InsertSnapshotAsync(1, snapshot);
        await _economy.InsertSnapshotAsync(1, snapshot); // a retry after an unknown outcome
        await _economy.InsertSnapshotAsync(1, snapshot with { TakenAtMs = H0 + 120_000, PeriodStartMs = H0 + 60_000, ByReason = [] });

        var rows = await _economy.SnapshotsAsync(H0, H0 + Hour, limit: 10);
        Assert.Equal([H0 + 60_000, H0 + 120_000], rows.Select(r => r.TakenAtMs));
        var first = rows[0];
        Assert.Equal((1, H0, 150L, 25L, 125L, 3), (first.WorldId, first.PeriodStartMs, first.GoldCreated, first.GoldDestroyed,
            first.GoldOnline, first.ActivePlayers));
        Assert.Equal(new EconomyReasonFigures(150, 0, 20), first.ByReason["outpostSale"]);
        Assert.Equal(new EconomyReasonFigures(0, 25, 0), first.ByReason["vendorPurchase"]);
        Assert.Empty(rows[1].ByReason);

        // The newest rows when a page is smaller than the window, still oldest first.
        Assert.Equal([H0 + 120_000], (await _economy.SnapshotsAsync(H0, H0 + Hour, limit: 1)).Select(r => r.TakenAtMs));
    }

    [Fact]
    public async Task The_snapshot_worker_writes_every_snapshot_to_the_database_and_drains_on_dispose()
    {
        var worker = new EconomySnapshotWorker(_economy, worldId: 4, new EconomySettings { SnapshotQueueMax = 10 });
        for (int i = 1; i <= 3; i++)
            worker.Save(new EconomySnapshot(H0 + i * 1_000, H0 + (i - 1) * 1_000, i, 0, 0, 0, []));
        await worker.DisposeAsync();

        Assert.Equal((3L, 0L), (worker.Saved, worker.Failures));
        Assert.Equal([1L, 2L, 3L], (await _economy.SnapshotsAsync(H0, H0 + Hour, 10)).Select(r => r.GoldCreated));
    }
}
