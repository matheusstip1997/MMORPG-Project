using GameServer.Dev;
using GameServer.Host.Config;
using GameServer.Telemetry;
using GameServer.Tests.Support;
using GameServer.World;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;

namespace GameServer.Tests.Dev;

/// <summary>
/// The dev hot reload's checks and decisions (spec 11.7) on a copy of the committed tables: a value change is accepted
/// and posted; anything any check refuses, or that needs a restart, is rejected as a whole and leaves the world as it is.
/// </summary>
public sealed class TableReloaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mmorpg-reload-" + Guid.NewGuid().ToString("N"));
    private readonly RetuneMailbox _mailbox = new();
    private readonly TelemetryQueue _telemetry = new(1024);
    private readonly StringWriter _log = new();
    private readonly TableReloader _reloader;

    public TableReloaderTests()
    {
        Directory.CreateDirectory(_directory);
        foreach (var file in Directory.EnumerateFiles(RepositoryPaths.Tables(), "*.json"))
            File.Copy(file, Path.Combine(_directory, Path.GetFileName(file)));
        var running = ConfigChecks.LoadAndCheck(_directory);
        Assert.True(running.Ok, string.Join("\n", running.Errors));
        var map = WorldLoader.Load(RepositoryPaths.M1World(), running.Set).Map!;
        _reloader = new TableReloader(_directory, map, running.Set, _mailbox, _telemetry, new FakeClock(), 1, _log);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void Edit(string table, string from, string to)
    {
        var path = Path.Combine(_directory, table + ".json");
        var text = File.ReadAllText(path);
        Assert.Contains(from, text);
        File.WriteAllText(path, text.Replace(from, to));
    }

    private List<TelemetryRecord> Records()
    {
        var records = new TelemetryRecord[64];
        int count = _telemetry.DrainTo(records);
        return records.Take(count).ToList();
    }

    [Fact]
    public void A_changed_timing_is_checked_and_posted_with_the_hash_the_client_computes()
    {
        // The warrior's attack (and any action with the same timings).
        Edit("ActionDef", "\"startupMs\": 300, \"channelMs\": 0, \"activeMs\": 100, \"recoveryMs\": 300,",
            "\"startupMs\": 300, \"channelMs\": 0, \"activeMs\": 100, \"recoveryMs\": 500,");

        var report = _reloader.Reload();

        Assert.Equal(ReloadOutcome.Posted, report.Outcome);
        Assert.Equal(["ActionDef"], report.ChangedTables);
        Assert.Equal(TableReloader.HashOf(_directory), report.Hash);
        var plan = _mailbox.Take();
        Assert.Same(report.Plan, plan);
        Assert.Equal(6 + 2 + 10, plan!.Actions.Find("attack.warrior")!.TotalTicks); // 300/100/500 ms at 20 Hz
        Assert.NotNull(plan.MonsterAi);
        Assert.NotNull(plan.Projectiles);
        Assert.Empty(Records()); // the accepted record waits for the world's tick boundary
    }

    [Fact]
    public void An_accepted_plan_is_recorded_with_the_tick_the_world_took_it_at()
    {
        Edit("DamageConstants", "\"critChanceCapPercent\": 75,", "\"critChanceCapPercent\": 70,");
        var report = _reloader.Reload();
        Assert.Equal(ReloadOutcome.Posted, report.Outcome);

        report.Plan!.MarkApplied(1234);
        Assert.True(SpinWait.SpinUntil(() => _telemetry.Count > 0, TimeSpan.FromSeconds(5)));

        var record = Assert.Single(Records());
        Assert.Equal(TelemetryKind.ConfigReload, record.Kind);
        Assert.Equal(1234, record.V0);
        Assert.Equal((long)ConfigReloadResult.Accepted, record.V1);
        Assert.Equal(unchecked((long)report.Hash), record.V2);
        Assert.Contains("accepted at tick 1234", _log.ToString());
    }

    [Theory]
    [InlineData("ActionDef", "\"recoveryMs\": 300,", "\"recoveryMs\": 300,,,", "InvalidJson")] // bad JSON
    [InlineData("ActionDef", "\"recoveryMs\": 300,", "\"recoveryMs\": -5,", "OutOfRange")] // out of range
    [InlineData("DamageConstants", "\"rankFactorMaxPercent\": 200", "\"rankFactorMaxPercent\": 99", "RankFactor")] // cross-table
    [InlineData("UiStrings", "{ \"id\": \"action.attack.warrior\", \"text\": \"Sword Attack\" },", "", "action.attack.warrior")] // coverage
    public void A_change_any_check_refuses_rejects_the_whole_reload(string table, string from, string to, string reason)
    {
        Edit("DamageConstants", "\"critChanceCapPercent\": 75,", "\"critChanceCapPercent\": 70,"); // a good change in the same reload
        Edit(table, from, to);

        var report = _reloader.Reload();

        Assert.Equal(ReloadOutcome.Rejected, report.Outcome);
        Assert.Contains(report.Errors, e => e.Contains(reason, StringComparison.Ordinal));
        Assert.Null(_mailbox.Take());
        var record = Assert.Single(Records());
        Assert.Equal((long)ConfigReloadResult.Rejected, record.V1);
        Assert.Equal(report.Errors.Count, record.V3);
        Assert.Contains("REJECTED", _log.ToString());
    }

    [Fact]
    public void A_table_read_only_at_start_needs_a_restart()
    {
        Edit("ServerLoop", "\"netPollIntervalMs\": 1,", "\"netPollIntervalMs\": 2,");

        var report = _reloader.Reload();

        Assert.Equal(ReloadOutcome.Rejected, report.Outcome);
        Assert.Contains(report.Errors, e => e.Contains("ServerLoop") && e.Contains("restart"));
    }

    [Fact]
    public void A_fixed_rows_table_may_not_renumber_a_row()
    {
        Edit("ActionDef", "{ \"id\": \"attack.warrior\", \"netId\": 1,", "{ \"id\": \"attack.warrior\", \"netId\": 999,");

        var report = _reloader.Reload();

        Assert.Equal(ReloadOutcome.Rejected, report.Outcome);
        Assert.Contains(report.Errors, e => e.Contains("ActionDef") && e.Contains("netId"));
    }

    [Fact]
    public void Tables_the_server_does_not_read_give_nothing_to_swap_and_a_revert_is_no_change()
    {
        Edit("HudSettings", "\"toastMs\": 1500,", "\"toastMs\": 1600,");
        var report = _reloader.Reload();
        Assert.Equal(ReloadOutcome.NotServer, report.Outcome);
        Assert.Equal(["HudSettings"], report.ChangedTables);
        Assert.Null(_mailbox.Take());
        Assert.Equal((long)ConfigReloadResult.NotServer, Assert.Single(Records()).V1);

        Assert.Equal(ReloadOutcome.Unchanged, _reloader.Reload().Outcome);
        Edit("HudSettings", "\"toastMs\": 1600,", "\"toastMs\": 1500,");
        Assert.Equal(ReloadOutcome.NotServer, _reloader.Reload().Outcome);
    }

    [Fact]
    public void After_a_rejection_the_fixed_files_reload()
    {
        Edit("ActionDef", "\"recoveryMs\": 300,", "\"recoveryMs\": -5,");
        Assert.Equal(ReloadOutcome.Rejected, _reloader.Reload().Outcome);

        Edit("ActionDef", "\"recoveryMs\": -5,", "\"recoveryMs\": 350,");

        Assert.Equal(ReloadOutcome.Posted, _reloader.Reload().Outcome);
    }

    [Fact]
    public void A_newer_plan_replaces_one_the_world_has_not_taken()
    {
        Edit("DamageConstants", "\"critChanceCapPercent\": 75,", "\"critChanceCapPercent\": 70,");
        var first = _reloader.Reload().Plan!;
        Edit("DamageConstants", "\"critChanceCapPercent\": 70,", "\"critChanceCapPercent\": 65,");
        var second = _reloader.Reload().Plan!;

        Assert.True(first.Applied.IsCanceled);
        Assert.Same(second, _mailbox.Take());
        Assert.Null(_mailbox.Take());
    }

    [Fact]
    public void The_watcher_reloads_once_after_a_burst_of_writes()
    {
        int calls = 0;
        using var reloaded = new SemaphoreSlim(0);
        using var watcher = new TableWatcher(_directory, debounceMs: 200, () =>
        {
            Interlocked.Increment(ref calls);
            reloaded.Release();
        });
        for (int i = 0; i < 5; i++)
        {
            Edit("HudSettings", i == 0 ? "\"toastMs\": 1500," : $"\"toastMs\": {1500 + i - 1},", $"\"toastMs\": {1500 + i},");
            Thread.Sleep(20);
        }

        Assert.True(reloaded.Wait(TimeSpan.FromSeconds(10)), "the watcher never called back");
        Thread.Sleep(600); // a second call would come within the debounce time
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(0, watcher.Failures);
    }
}
