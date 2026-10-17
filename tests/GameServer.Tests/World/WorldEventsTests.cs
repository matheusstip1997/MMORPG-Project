using GameServer.Events;
using GameServer.Telemetry;
using GameServer.Tests.Support;
using GameServer.World;

namespace GameServer.Tests.World;

/// <summary>Backlog H2 wiring: the world's bus and telemetry in tick phases 11 and 12 (spec 1.6, 8.1).</summary>
public class WorldEventsTests
{
    private const int WorldId = 1;

    [Fact]
    public void Phase_11_dispatch_then_phase_12_flush_run_after_every_other_phase()
    {
        var order = new List<string>();
        var harness = new WorldHarness(events: new NamedHook("dispatch", order), telemetry: new NamedHook("flush", order));
        harness.World.PhaseStarted = phase => order.Add(phase.ToString());

        harness.Ticks(2);

        var oneTick = Enum.GetValues<TickPhase>().OrderBy(p => (int)p).Select(p => p.ToString()).ToList();
        oneTick.Insert(oneTick.IndexOf(nameof(TickPhase.EventDispatch)) + 1, "dispatch");
        Assert.Equal(nameof(TickPhase.TelemetryFlush), oneTick[^1]);
        oneTick.Add("flush");
        Assert.Equal(oneTick.Concat(oneTick), order);
    }

    [Fact]
    public void Join_and_leave_publish_session_events_delivered_in_the_same_tick_and_recorded_by_telemetry()
    {
        var pipeline = new Pipeline();

        var player = pipeline.Harness.Join(peerId: 7, accountId: 42);

        Assert.Equal([new SessionStarted(42, player.Id, Reattached: false)], pipeline.Seen);
        Assert.Equal(0, pipeline.Bus.PendingCount);

        pipeline.Harness.Leave(7);
        pipeline.Harness.Ticks(1);

        Assert.Equal(new SessionEnded(42, player.Id, SessionEndReason.Disconnected), pipeline.Seen[^1]);
        var records = pipeline.DrainTelemetry();
        Assert.Equal([TelemetryKind.SessionStarted, TelemetryKind.SessionEnded], records.Select(r => r.Kind));
        Assert.All(records, r => Assert.Equal((WorldId, 42L, (long)player.Id), (r.WorldId, r.AccountId, r.Subject)));
        Assert.Equal((long)SessionEndReason.Disconnected, records[1].V0);
        Assert.Equal([1L, 2L], records.Select(r => r.EventId));
    }

    [Fact]
    public void Shutdown_ends_every_open_session_in_entity_order_and_dispatches_at_once()
    {
        var pipeline = new Pipeline();
        var first = pipeline.Harness.Join(peerId: 5, accountId: 10);
        var second = pipeline.Harness.Join(peerId: 3, accountId: 20);

        pipeline.Harness.World.Shutdown();

        Assert.Equal(
            [
                new SessionEnded(10, first.Id, SessionEndReason.ServerShutdown),
                new SessionEnded(20, second.Id, SessionEndReason.ServerShutdown),
            ],
            pipeline.Seen.OfType<SessionEnded>());
        Assert.Empty(pipeline.Harness.World.Entities);
        Assert.Equal(0, pipeline.Bus.PendingCount);
    }

    [Fact]
    public void A_world_without_a_bus_publishes_nothing_and_still_runs()
    {
        var harness = new WorldHarness();

        harness.Join(peerId: 1);
        harness.Leave(1);
        harness.Ticks(1);
        harness.World.Shutdown();

        Assert.Empty(harness.World.Entities);
    }

    /// <summary>A world wired the way Bootstrap wires it: bus, phase-11 dispatch, telemetry consumer.</summary>
    private sealed class Pipeline : IEventConsumer<SessionStarted>, IEventConsumer<SessionEnded>
    {
        public Pipeline()
        {
            Bus = new EventBus(WorldId, new FakeClock(), TestPipelineSettings.BusLimits());
            Queue = new TelemetryQueue(64);
            TelemetryEventConsumer.Attach(Bus, Queue);
            Bus.Subscribe<SessionStarted>(this);
            Bus.Subscribe<SessionEnded>(this);
            Harness = new WorldHarness(events: new EventDispatchHook(Bus), bus: Bus);
        }

        public EventBus Bus { get; }

        public TelemetryQueue Queue { get; }

        public WorldHarness Harness { get; }

        public List<IGameEvent> Seen { get; } = [];

        public void Handle(in EventEnvelope envelope, in SessionStarted payload) => Seen.Add(payload);

        public void Handle(in EventEnvelope envelope, in SessionEnded payload) => Seen.Add(payload);

        public List<TelemetryRecord> DrainTelemetry()
        {
            var buffer = new TelemetryRecord[Queue.Capacity];
            return [.. buffer.Take(Queue.DrainTo(buffer))];
        }
    }

    private sealed class NamedHook(string name, List<string> order) : ITickHook
    {
        public void Run(uint serverTick) => order.Add(name);
    }
}
