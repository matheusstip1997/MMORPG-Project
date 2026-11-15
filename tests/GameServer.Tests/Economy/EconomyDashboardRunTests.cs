using System.Text.Json;
using BotClient.LoadTest;
using Dapper;
using GameServer.Economy;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;

namespace GameServer.Tests.Economy;

/// <summary>
/// The comparison of M4 decision 13 (WP-M4-15; spec 7.4, 12.5): over <b>one recorded run</b>, an <c>m4-exit</c> run on its own
/// databases (fixture <c>Bots/Fixtures/economy-m4-exit-run</c>: the run's economy telemetry and its <c>gold_ledger</c> and
/// <c>mail_ops</c> expiry rows, README there), the admin panel's dashboard figures (<see cref="EconomyDashboard"/> over
/// <see cref="EconomyRepository.HourlyTotalsAsync"/>, what <c>GET /api/economy</c> serves) equal <c>BotClient report --kind
/// economy</c>'s over the run's telemetry: gold created and destroyed, by reason over the window and hour by hour. The run has
/// M4 sinks (fees) and moves (a trade, listing payments, mail claims), which neither side counts as faucets or sinks.
/// </summary>
public sealed class EconomyDashboardRunTests : IAsyncLifetime
{
    private static readonly string Run =
        Path.Combine(RepositoryPaths.Root(), "tests", "GameServer.Tests", "Bots", "Fixtures", "economy-m4-exit-run");

    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private NpgsqlDataSource _realmDb = null!;
    private long _character;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        var created = await new CharacterRepository(_realmDb, 1, new FakeClock())
            .CreateAsync(new NewCharacter(1, 0, "Run", "warrior", Spawn, Guid.NewGuid()));
        _character = created.Character!.CharacterId;
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    /// <summary>The recorded rows: every <c>gold_ledger</c> row of the run (faucets, sinks and moves) and every mail expiry.</summary>
    private sealed record Ledger(List<GoldRow> Gold, List<ExpiredMail> MailExpired);

    private sealed record GoldRow(long AtMs, long Delta, short Flow, string Reason);

    private sealed record ExpiredMail(long AtMs, short Kind, long Gold);

    private async Task<Ledger> SeedAsync()
    {
        var ledger = JsonSerializer.Deserialize<Ledger>(await File.ReadAllTextAsync(Path.Combine(Run, "ledger.json")),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        await using var connection = await _realmDb.OpenConnectionAsync();
        int n = 0;
        foreach (var row in ledger.Gold)
        {
            string op = $"run:{n++}";
            await connection.ExecuteAsync("""
                INSERT INTO inventory_ops (op_key, character_id, reason, source, save_id, created_at)
                VALUES (@op, @character, @Reason, 'run', gen_random_uuid(), @at);
                INSERT INTO gold_ledger (op_key, character_id, delta, balance_after, flow, reason, source, save_id, created_at)
                VALUES (@op, @character, @Delta, 0, @Flow, @Reason, 'run', gen_random_uuid(), @at);
                """, new { op, character = _character, row.Reason, row.Delta, row.Flow, at = DateTime.UnixEpoch.AddMilliseconds(row.AtMs) });
        }
        foreach (var mail in ledger.MailExpired)
        {
            string op = $"run:{n++}";
            var at = DateTime.UnixEpoch.AddMilliseconds(mail.AtMs);
            long mailId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO mail (recipient_id, kind, subject_id, gold, state, created_at, expires_at, closed_at, send_op_key, source)
                VALUES (@character, @Kind, 'mail.subject.run', 0, 2, @created, @at, @at, @op, 'run') RETURNING mail_id
                """, new { character = _character, mail.Kind, created = at.AddDays(-30), at, op });
            await connection.ExecuteAsync("""
                INSERT INTO mail_ops (op_key, kind, mail_id, character_id, gold, created_at) VALUES (@op, 3, @mailId, @character, @Gold, @at)
                """, new { op = "mailExpire:" + mailId, mailId, character = _character, mail.Gold, at });
        }
        return ledger;
    }

    [Fact]
    public async Task The_dashboards_figures_equal_the_reports_over_the_recorded_m4_exit_run()
    {
        var ledger = await SeedAsync();
        long from = ledger.Gold.Min(g => g.AtMs) - EconomyRepository.HourMs, to = ledger.Gold.Max(g => g.AtMs) + EconomyRepository.HourMs;
        var dashboard = EconomyDashboard.Build(await new EconomyRepository(_realmDb).HourlyTotalsAsync(from, to), []);
        var report = EconomyReport.Read(Path.Combine(Run, "telemetry"));

        // The run is an M4 run: faucets, the fee sinks and moves between the two players are all in it.
        Assert.Contains(ledger.Gold, g => g.Flow == (short)GoldFlow.Move);
        Assert.True(dashboard.CreatedByReason.GetValueOrDefault("outpostSale") > 0);
        Assert.True(dashboard.DestroyedByReason.GetValueOrDefault("fee") > 0);
        Assert.DoesNotContain("trade", dashboard.CreatedByReason.Keys.Concat(dashboard.DestroyedByReason.Keys));

        // The same figures: the window's totals by reason, and hour by hour.
        EconomyDashboardData.AssertSameTotals(dashboard.Hours, report);
        EconomyDashboardData.AssertSameHours(dashboard.Hours, report);
        Assert.Equal((report.GoldCreated, report.GoldDestroyed), (dashboard.GoldCreated, dashboard.GoldDestroyed));
    }
}
