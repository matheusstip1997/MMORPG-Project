using System.Diagnostics;
using GameServer.Events;
using GameServer.Net;
using GameServer.Net.Tickets;
using GameServer.Telemetry;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Tests.Telemetry;

/// <summary>Backlog H2 wiring: a world's phase-12 telemetry (spec 7.2, architecture rule 10).</summary>
public class WorldTelemetryTests
{
    private const int WorldId = 4;

    [Theory]
    [InlineData(1_000, 20, 20)]
    [InlineData(200, 20, 4)]
    [InlineData(50, 20, 1)]
    [InlineData(50, 10, 1)] // shorter than a tick: every tick
    public void Stats_interval_is_rounded_to_whole_ticks(int intervalMs, int tickRateHz, int expectedTicks)
    {
        var settings = TestPipelineSettings.Telemetry(statsIntervalMs: intervalMs);

        Assert.Equal(expectedTicks, WorldTelemetry.StatsIntervalTicks(settings, tickRateHz));
    }

    [Fact]
    public void Tick_timing_and_counters_are_recorded_once_per_interval_with_the_max_of_that_interval()
    {
        // 200 ms at 20 Hz: a report every 4 ticks.
        var rig = new Rig(new TelemetryQueue(64), TestPipelineSettings.Telemetry(statsIntervalMs: 200));
        long[] durations = [100, 900, 200, 300, 400, 500, 600, 200];
        rig.World.PhaseStarted = phase =>
        {
            if (phase == TickPhase.Movement)
                rig.TickClock.Advance(durations[rig.World.ServerTick]);
        };
        rig.Router.OnPeerConnected(1, new SessionTicket(9, 1, 0, 0, new byte[16], 1));
        rig.Router.OnDataReceived(1, Channel.UnreliableSequenced, [0xFF, 0xFF, 0xFF]);
        rig.Router.OnDataReceived(1, Channel.ReliableOrdered, [0xFF]);

        for (int i = 0; i < durations.Length; i++)
            rig.Thread.Loop.RunOne();

        var records = rig.Drain();
        Assert.Equal(
            [TelemetryKind.TickStats, TelemetryKind.WorldCounters, TelemetryKind.RequestCounters, TelemetryKind.SendCounters,
                TelemetryKind.TickStats, TelemetryKind.WorldCounters, TelemetryKind.RequestCounters, TelemetryKind.SendCounters],
            records.Select(r => r.Kind));
        Assert.All(records, r => Assert.Equal((WorldId, rig.Clock.UtcNowMs), (r.WorldId, r.UtcMs)));
        // Phase 12 of tick n sees tick n-1's time: the first report covers ticks 0-2, the second ticks 3-6.
        Assert.Equal((200L, 900L, 0L, 0L), (records[0].V0, records[0].V1, records[0].V2, records[0].V3));
        Assert.Equal((600L, 600L), (records[4].V0, records[4].V1));
        Assert.Equal((1L, 0L, 0L, 0L), (records[1].V0, records[1].V1, records[1].V2, records[1].V3));
        Assert.Equal((1L, 0L), (records[2].V0, records[2].V1)); // the unknown reliable id
        Assert.Equal(0L, records[3].V0); // no unreliable message over a peer's limit
    }

    [Fact]
    public void Chat_totals_are_recorded_with_the_counters_but_never_the_text()
    {
        var rig = new Rig(new TelemetryQueue(64), TestPipelineSettings.Telemetry(statsIntervalMs: 50),
            chat: TestWorlds.Chat(burst: 1, messagesPerPeriod: 1, periodMs: 60_000));
        var chat = rig.World.Chat!;
        // Two lines from a player built by hand (the rig has no characters to log in): one accepted, one rate limited.
        var player = new GameServer.Entities.Player(99, GameServer.Entities.Player.NoPeer, 1, new InputQueue(1),
            default, 0);
        chat.Submit(player, "secret words", rig.Clock.UtcNowMs);
        chat.Submit(player, "more", rig.Clock.UtcNowMs);

        rig.Thread.Loop.RunOne();

        var record = Assert.Single(rig.Drain(), r => r.Kind == TelemetryKind.ChatCounters);
        Assert.Equal((1L, 0L, 1L, 1L), (record.V0, record.V1, record.V2, record.V3));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_per_tick_timing_log_records_every_tick_it_measured_only_when_on(bool on)
    {
        // Backlog J per-tick timing log: the load test's tick p99 (spec 11.5). Stats only every 600 s, so only TickTiming shows.
        var rig = new Rig(new TelemetryQueue(64), TestPipelineSettings.Telemetry(statsIntervalMs: 600_000, tickTimingLog: on));
        long[] durations = [100, 900, 200, 300];
        rig.World.PhaseStarted = phase =>
        {
            if (phase == TickPhase.Movement)
                rig.TickClock.Advance(durations[rig.World.ServerTick]);
        };

        for (int i = 0; i < durations.Length; i++)
            rig.Thread.Loop.RunOne();

        var records = rig.Drain();
        if (!on)
        {
            Assert.Empty(records);
            return;
        }
        // Phase 12 of tick n sees tick n-1's time; tick 0 has no earlier tick, the last tick is recorded by the next one.
        Assert.All(records, r => Assert.Equal((TelemetryKind.TickTiming, WorldId), (r.Kind, r.WorldId)));
        Assert.Equal([(0L, 100L), (1L, 900L), (2L, 200L)], records.Select(r => (r.V0, r.V1)));
    }

    [Fact]
    public async Task A_full_queue_with_a_stuck_writer_drops_and_counts_without_slowing_the_tick()
    {
        var queue = new TelemetryQueue(64);
        var settings = TestPipelineSettings.Telemetry(queueCapacity: 64, maxBatchRecords: 64, flushIntervalMs: 10, statsIntervalMs: 50);
        var sink = new GatedSink();
        var rig = new Rig(queue, settings, sink);
        rig.Writer.Start();

        // Park the writer inside the sink, then fill the queue: nothing drains from here on.
        queue.TryEnqueue(new TelemetryRecord(TelemetryKind.Moved, 1));
        rig.Writer.Flush();
        Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(5)), "The writer never reached the sink.");
        while (queue.TryEnqueue(new TelemetryRecord(TelemetryKind.Moved, 1)))
        {
        }
        long droppedBefore = queue.Dropped;

        const int ticks = 1_000;
        var watch = new Stopwatch();
        var run = Task.Run(() =>
        {
            watch.Start();
            for (int i = 0; i < ticks; i++)
                rig.Thread.Loop.RunOne();
            watch.Stop();
        });

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(finished == run, "The tick blocked on a full telemetry queue.");
        await run;
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"{ticks} ticks took {watch.Elapsed}.");
        Assert.Equal(droppedBefore + 4 * ticks, queue.Dropped); // TickStats, WorldCounters, RequestCounters and SendCounters every tick

        sink.Open();
        rig.Writer.Dispose();
        var report = Assert.Single(sink.Records, r => r.Kind == TelemetryKind.QueueDropped);
        Assert.Equal(queue.Dropped, report.V1);
    }

    [Fact]
    public void A_crashed_world_thread_records_the_tick_it_died_on()
    {
        var rig = new Rig(new TelemetryQueue(64), TestPipelineSettings.Telemetry(statsIntervalMs: 600_000));
        rig.World.PhaseStarted = phase =>
        {
            if (phase == TickPhase.HitResolution && rig.World.ServerTick == 3)
                throw new InvalidOperationException("test crash");
        };

        rig.Thread.Start();
        var watch = Stopwatch.StartNew();
        while (rig.Thread.IsRunning)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "The world thread did not stop.");
            Thread.Sleep(1);
        }

        Assert.IsType<InvalidOperationException>(rig.Thread.Failure);
        var record = Assert.Single(rig.Drain());
        Assert.Equal(new TelemetryRecord(TelemetryKind.WorldCrashed, rig.Clock.UtcNowMs, WorldId: WorldId, V0: 3), record);
    }

    /// <summary>A world built the way Bootstrap builds it, with a fake transport and a fake tick clock.</summary>
    private sealed class Rig
    {
        public Rig(TelemetryQueue queue, TelemetrySettings settings, ITelemetrySink? sink = null,
            Shared.Data.Tables.ChatSettings? chat = null)
        {
            Queue = queue;
            Writer = new TelemetryWriter(queue, sink ?? new GatedSink(open: true), settings, Clock);
            var worldSettings = TestWorlds.Settings();
            var inbox = new WorldInbox();
            Router = new InputRouter(inbox, worldSettings.MaxQueuedInputFrames, 0, chat?.MaxQueuedLines ?? 0);
            var bus = new EventBus(WorldId, Clock, TestPipelineSettings.BusLimits());
            var telemetry = new WorldTelemetry(WorldId, queue, Writer, Clock, settings, worldSettings.TickRateHz, Router, bus);
            var transport = new FakeTransport { Record = false };
            World = new GameWorld(worldSettings, TestMaps.Open(), inbox, transport,
                eventDispatch: new EventDispatchHook(bus), telemetryFlush: telemetry, events: bus, clock: Clock, chat: chat);
            var saves = new FakeSaveWorker();
            _ = new GameServer.Sessions.SessionManager(World, new FakeCharacterSource(),
                new InMemoryOwnershipLocks(Clock, 30_000), saves, saves, transport, Clock, TestSessions.Values(),
                TestSessions.Settings(), "test-server");
            Thread = new WorldThread(World, TickClock, telemetry.ReportCrash);
            telemetry.Attach(Thread);
        }

        public FakeClock Clock { get; } = new();

        public FakeTickClock TickClock { get; } = new();

        public TelemetryQueue Queue { get; }

        public TelemetryWriter Writer { get; }

        public InputRouter Router { get; }

        public GameWorld World { get; }

        public WorldThread Thread { get; }

        public List<TelemetryRecord> Drain()
        {
            var buffer = new TelemetryRecord[Queue.Capacity];
            return [.. buffer.Take(Queue.DrainTo(buffer))];
        }
    }

    /// <summary>Keeps what it is given; while closed, <see cref="Write"/> blocks like a stuck disk.</summary>
    private sealed class GatedSink(bool open = false) : ITelemetrySink
    {
        private readonly ManualResetEventSlim _gate = new(open);
        private readonly List<TelemetryRecord> _records = [];

        public ManualResetEventSlim Entered { get; } = new();

        public List<TelemetryRecord> Records
        {
            get
            {
                lock (_records)
                    return [.. _records];
            }
        }

        public void Open() => _gate.Set();

        public void Write(ReadOnlySpan<TelemetryRecord> records)
        {
            Entered.Set();
            _gate.Wait();
            lock (_records)
                _records.AddRange(records);
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
