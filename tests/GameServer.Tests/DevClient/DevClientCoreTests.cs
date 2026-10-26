using BotClient.LoadTest;
using Game.Gameplay.Dev;
using GameServer.Telemetry;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Actions;

namespace GameServer.Tests.DevClient;

/// <summary>
/// The engine-free cores of the dev scene's tools (spec 11.7), compiled from the Unity sources (ClientCore): the telemetry
/// tail against files the real server sink writes, the tick window against the load test's percentiles, the timeline
/// rings, and the hitbox model's fire detection and server-trace judging.
/// </summary>
public sealed class DevClientCoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mmorpg-devtail-" + Guid.NewGuid().ToString("N"));

    public DevClientCoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private JsonLinesTelemetrySink Sink(string runId, int maxFileBytes = 1 << 20) =>
        new(_directory, runId, new FakeClock(), maxFileBytes);

    private static TelemetryRecord Tick(long tick, long us, long late = 0) =>
        new(TelemetryKind.TickTiming, 1_000 + tick, WorldId: 1, V0: tick, V1: us, V2: late);

    private static List<DevTelemetryRecord> Poll(TelemetryTail tail)
    {
        var records = new List<DevTelemetryRecord>();
        tail.Poll(records);
        return records;
    }

    [Fact]
    public void The_layout_names_the_fields_as_the_server_schema_writes_them()
    {
        foreach (var layout in DevTelemetryLayout.Kinds)
        {
            Assert.True(TelemetrySchema.TryGet(layout.Name, out var server), layout.Name);
            Assert.Equal(server.Subject, layout.Subject);
            Assert.Equal(server.Target, layout.Target);
            Assert.Equal(server.V0, layout.V0);
            Assert.Equal(server.V1, layout.V1);
            Assert.Equal(server.V2, layout.V2);
            Assert.Equal(server.V3, layout.V3);
        }
        Assert.Equal((long)ConfigReloadResult.Accepted, DevTelemetryLayout.ReloadAccepted);
        Assert.Equal((long)ConfigReloadResult.Rejected, DevTelemetryLayout.ReloadRejected);
        Assert.Equal((long)ConfigReloadResult.NotServer, DevTelemetryLayout.ReloadNotServer);
    }

    [Fact]
    public void The_tail_skips_the_history_of_the_file_it_starts_on_and_reads_what_follows()
    {
        using var sink = Sink("20261026T120000Z-aaaaaaaa");
        sink.Write([Tick(1, 100), Tick(2, 200)]);
        sink.Flush();
        var tail = new TelemetryTail(_directory);
        Assert.Empty(Poll(tail));

        sink.Write([Tick(3, 300), new TelemetryRecord(TelemetryKind.Damaged, 5, V0: 1), Tick(4, 400, late: 7)]);
        sink.Flush();
        var records = Poll(tail);

        // The first line after the start may be cut: the tail starts at the next whole line, so tick 3 can be lost.
        Assert.Contains(records, r => r.Kind == DevTelemetryKind.TickTiming && r.V0 == 4 && r.V1 == 400 && r.V2 == 7);
        Assert.DoesNotContain(records, r => r.V0 <= 2);
        Assert.All(records, r => Assert.Equal(DevTelemetryKind.TickTiming, r.Kind)); // Damaged is not a dev kind
    }

    [Fact]
    public void A_server_started_after_the_tail_is_read_from_its_first_record_across_rotations()
    {
        var tail = new TelemetryTail(_directory);
        Assert.Empty(Poll(tail));
        var seen = new List<DevTelemetryRecord>();
        using (var sink = Sink("20261026T130000Z-bbbbbbbb", maxFileBytes: 65_536))
        {
            for (long tick = 1; tick <= 3_000; tick++)
            {
                sink.Write([Tick(tick, tick * 10)]);
                if (tick % 500 == 0)
                {
                    sink.Flush();
                    seen.AddRange(Poll(tail));
                }
            }
        }
        seen.AddRange(Poll(tail));

        Assert.True(Directory.GetFiles(_directory, "*.jsonl").Length > 1, "the sink should have rotated");
        Assert.Equal(Enumerable.Range(1, 3_000).Select(t => (long)t), seen.Select(r => r.V0));
    }

    [Fact]
    public void A_torn_line_is_parsed_once_it_is_complete()
    {
        var tail = new TelemetryTail(_directory);
        var path = Path.Combine(_directory, "20261026T140000Z-cccccccc-0000.jsonl");
        File.WriteAllText(path, "{\"k\":\"ConfigReload\",\"t\":5,\"tables\":2,\"tick\":77,\"res");
        Assert.Empty(Poll(tail));
        File.AppendAllText(path, "ult\":1,\"hash\":-12345,\"errors\":0}\n");

        var record = Assert.Single(Poll(tail));
        Assert.Equal(DevTelemetryKind.ConfigReload, record.Kind);
        Assert.Equal(2, record.Subject);
        Assert.Equal(77, record.V0);
        Assert.Equal(DevTelemetryLayout.ReloadAccepted, record.V1);
        Assert.Equal(-12345, record.V2);
    }

    [Fact]
    public void The_tick_window_summarises_like_the_load_test_report()
    {
        var random = new Random(7);
        var window = new TickTimingWindow(capacity: 200, budgetUs: 50_000);
        var kept = new List<long>();
        for (uint tick = 0; tick < 500; tick++)
        {
            long us = random.Next(500, 60_000);
            window.AddTick(tick, us, lateUs: tick == 450 ? 9_000 : 10);
            if (tick >= 300)
                kept.Add(us);
        }
        window.AddStats(overrunsTotal: 3);

        var summary = window.Summarize();
        kept.Sort();
        Assert.Equal(200, summary.Ticks);
        Assert.Equal(TickReport.Percentile(kept, 50), summary.P50Us);
        Assert.Equal(TickReport.Percentile(kept, 99), summary.P99Us);
        Assert.Equal(kept[^1], summary.WorstUs);
        Assert.Equal(kept.Count(us => us > 50_000), summary.OverBudget);
        Assert.Equal(9_000, summary.WorstLateUs);
        Assert.Equal(3, summary.Overruns);
        Assert.Equal(499u, summary.LastTick);
        Assert.Equal(200, TickTimingWindow.CapacityFor(10_000, 20));
        Assert.Equal(50_000, TickTimingWindow.BudgetUsFor(20));
    }

    [Fact]
    public void The_timeline_keeps_its_last_samples_and_counts_mismatches()
    {
        var model = new ActionTimelineModel(capacity: 4);
        for (uint tick = 1; tick <= 6; tick++)
            model.AddServer(tick, ActionState.Startup, 1, (int)tick, mismatch: tick == 5);
        model.AddServer(6, ActionState.Active, 1, 6, mismatch: true); // a second snapshot of the same tick is ignored

        Assert.Equal(4, model.ServerCount);
        Assert.Equal(6u, model.Server(0).Tick);
        Assert.Equal(3u, model.Server(3).Tick);
        Assert.Equal(1, model.Mismatches);

        model.AddTarget(10, 1, ActionState.Idle, 0, 0);
        model.AddTarget(10, 2, ActionState.Startup, 7, 0);
        model.AddTarget(11, 2, ActionState.Idle, 0, 0); // another target starts its row over
        Assert.Equal(11u, model.TargetId);
        Assert.Equal(1, model.TargetCount);
    }

    [Fact]
    public void The_hitbox_model_places_the_local_strike_on_its_fire_tick_and_judges_the_server_trace()
    {
        var tables = TestCombat.Tables();
        var catalog = ActionCatalog.From(tables);
        var model = new HitboxModel(catalog, tables, holdMs: 500);
        var state = new ActionMachineState(catalog, catalog.Loadout("warrior"));
        var attack = catalog.Find("attack.warrior")!;
        int strike = Array.FindIndex(attack.Effects, e => e.Kind == EffectKind.Strike);
        var resources = UnlimitedActionResources.Instance;
        var fired = new FiredEffect[ActionMachine.MaxFiredPerStep];
        ActionMachine.Step(state, new ActionInput(true, Shared.Protocol.InputActions.Attack, 0, 0, 1), resources, fired);
        model.ObserveLocal(1, state, FixVec2.Zero, nowMs: 0);
        while (state.ActionIndex >= 0 && state.Elapsed < attack.Effects[strike].FireTick)
        {
            ActionMachine.Step(state, new ActionInput(true, Shared.Protocol.InputActions.Attack, 0, 0, 1), resources, fired);
            model.ObserveLocal(1, state, FixVec2.Zero, nowMs: 0);
        }
        var shapes = model.ShapesOf(attack.Effects[strike].Def.Id);
        Assert.NotEmpty(shapes);
        Assert.Equal(shapes.Count, model.PredictedAdded);
        Assert.All(model.Shapes, s => Assert.Equal(HitboxSource.Predicted, s.Source));

        // The server's trace of the same strike: a target 1 m ahead is touched, one 20 m ahead is not.
        long actionEffect = ((long)attack.NetId << 8) | (uint)strike;
        model.AddServerStrike(new DevTelemetryRecord(DevTelemetryKind.StrikePlaced, 0, 1, actionEffect, 9, 0, 0, 0), nowMs: 0);
        model.AddServerTarget(new DevTelemetryRecord(DevTelemetryKind.StrikeTested, 0, 1, 2, 9, 0, FixedPoint.FromCm(100),
            FixedPoint.FromCm(50)), nowMs: 0);
        model.AddServerTarget(new DevTelemetryRecord(DevTelemetryKind.StrikeTested, 0, 1, 3, 9, 0, FixedPoint.FromCm(2_000),
            FixedPoint.FromCm(50)), nowMs: 0);

        Assert.Equal(shapes.Count, model.ServerAdded);
        Assert.Equal([true, false], model.Targets.Select(t => t.Hit));
        Assert.Equal(0, model.Unmatched);

        model.Expire(nowMs: 500);
        Assert.Empty(model.Shapes);
        Assert.Empty(model.Targets);
    }
}
