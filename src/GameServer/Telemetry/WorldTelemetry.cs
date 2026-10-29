using GameServer.Economy;
using GameServer.Events;
using GameServer.Host;
using GameServer.Net;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Util;

namespace GameServer.Telemetry;

/// <summary>
/// One world's telemetry at tick phase 12 (spec 1.6, 7.2). Every tick it asks the <see cref="TelemetryWriter"/> to drain;
/// every <see cref="TelemetrySettings.StatsIntervalMs"/> it records the world's server health: a
/// <see cref="TelemetryKind.TickStats"/> record (last and max tick time in the interval, overruns, max start lateness) and
/// a <see cref="TelemetryKind.WorldCounters"/> record (running totals), plus <see cref="TelemetryKind.RequestCounters"/>
/// and, with chat on, <see cref="TelemetryKind.ChatCounters"/>. With <see cref="TelemetrySettings.TickTimingLog"/> it
/// also records every tick's timing (<see cref="TelemetryKind.TickTiming"/>, backlog J). It records a crash of the world's thread.
/// </summary>
/// <remarks>
/// Runs on the simulation thread. It only enqueues and signals, never waits and never does I/O (architecture rule 10),
/// and allocates nothing. A full queue drops the records and counts them.
/// </remarks>
public sealed class WorldTelemetry : ITickHook
{
    private const int MsPerSecond = 1_000;

    private readonly int _worldId;
    private readonly TelemetryQueue _queue;
    private readonly TelemetryWriter _writer;
    private readonly IClock _clock;
    private readonly int _statsIntervalTicks;
    private readonly bool _tickTimingLog;
    private readonly InputRouter _router;
    private readonly EventBus _bus;
    private GameWorld? _world;
    private TickLoop? _loop;
    private int _ticksSinceStats;
    private long _maxTickMicros;
    private long _maxLatenessMicros;
    private long _economySteps;

    public WorldTelemetry(
        int worldId,
        TelemetryQueue queue,
        TelemetryWriter writer,
        IClock clock,
        TelemetrySettings settings,
        int tickRateHz,
        InputRouter router,
        EventBus bus)
    {
        _worldId = worldId;
        _queue = queue;
        _writer = writer;
        _clock = clock;
        _statsIntervalTicks = StatsIntervalTicks(settings, tickRateHz);
        _tickTimingLog = settings.TickTimingLog;
        _router = router;
        _bus = bus;
    }

    /// <summary>The stats interval in whole ticks, at least one.</summary>
    public static int StatsIntervalTicks(TelemetrySettings settings, int tickRateHz) =>
        Math.Max(1, (int)((long)settings.StatsIntervalMs * tickRateHz / MsPerSecond));

    /// <summary>
    /// The world takes this hook in its constructor, so the world and its loop attach afterwards, before the thread
    /// starts. Until then only the writer is flushed.
    /// </summary>
    public void Attach(WorldThread thread)
    {
        _world = thread.World;
        _loop = thread.Loop;
    }

    public void Run(uint serverTick)
    {
        if (_world != null && _loop != null)
            SampleStats(_world, _loop);
        _writer.Flush();
    }

    /// <summary>For <see cref="WorldThread"/>'s crash callback; runs on the dying simulation thread.</summary>
    public void ReportCrash(Exception exception)
    {
        // The exception text goes to stderr (WorldThread); records are fixed-size and hold the fact and the tick.
        _queue.TryEnqueue(new TelemetryRecord(TelemetryKind.WorldCrashed, _clock.UtcNowMs, WorldId: _worldId,
            V0: _world?.ServerTick ?? 0));
        _writer.Flush();
    }

    private void SampleStats(GameWorld world, TickLoop loop)
    {
        // In phase 12 the loop still holds the previous tick's timing (it measures a tick after it returns).
        if (_tickTimingLog && loop.TicksRun > 0)
            _queue.TryEnqueue(new TelemetryRecord(TelemetryKind.TickTiming, _clock.UtcNowMs, WorldId: _worldId,
                V0: world.ServerTick - 1, V1: loop.LastTickMicros, V2: loop.LastStartLatenessMicros));
        _maxTickMicros = Math.Max(_maxTickMicros, loop.LastTickMicros);
        _maxLatenessMicros = Math.Max(_maxLatenessMicros, loop.LastStartLatenessMicros);
        SampleEconomy(world);
        if (++_ticksSinceStats < _statsIntervalTicks)
            return;

        long now = _clock.UtcNowMs;
        _queue.TryEnqueue(new TelemetryRecord(TelemetryKind.TickStats, now, WorldId: _worldId,
            V0: loop.LastTickMicros, V1: _maxTickMicros, V2: loop.Overruns, V3: _maxLatenessMicros));
        _queue.TryEnqueue(new TelemetryRecord(TelemetryKind.WorldCounters, now, WorldId: _worldId,
            V0: _router.MalformedPackets, V1: _router.StaleFrames, V2: world.SnapshotsTruncated, V3: _bus.ConsumerFailures));
        _queue.TryEnqueue(new TelemetryRecord(TelemetryKind.RequestCounters, now, WorldId: _worldId,
            V0: _router.MalformedRequests, V1: _router.ChatLinesDropped));
        var lastTooLarge = world.LastUnreliableTooLarge;
        _queue.TryEnqueue(new TelemetryRecord(TelemetryKind.SendCounters, now, WorldId: _worldId, Subject: lastTooLarge.PeerId,
            V0: world.UnreliableTooLarge, V1: lastTooLarge.Bytes, V2: lastTooLarge.LimitBytes));
        if (world.Chat is { } chat)
        {
            _queue.TryEnqueue(new TelemetryRecord(TelemetryKind.ChatCounters, now, WorldId: _worldId,
                V0: chat.Accepted, V1: world.ChatDeliveries, V2: chat.Refused, V3: chat.RateLimited));
        }

        _ticksSinceStats = 0;
        _maxTickMicros = 0;
        _maxLatenessMicros = 0;
    }

    /// <summary>
    /// The economy snapshot (R-4.2-07, spec 7.2; WP-M2-4) after each demand control step: running faucet and sink totals,
    /// the gold online characters hold, the active players, and the same totals by reason.
    /// </summary>
    private void SampleEconomy(GameWorld world)
    {
        if (world.Demand is not { } demand || world.Ledger is not { } ledger || demand.Steps == _economySteps)
            return;
        _economySteps = demand.Steps;
        long now = _clock.UtcNowMs;
        _queue.TryEnqueue(new TelemetryRecord(TelemetryKind.EconomyTotals, now, WorldId: _worldId,
            V0: ledger.GoldCreated, V1: ledger.GoldDestroyed, V2: world.Inventory?.GoldHeld() ?? 0, V3: demand.ActivePlayers));
        for (int reason = 1; reason < EconomyLedger.ReasonCount; reason++)
        {
            var value = (InventoryReason)reason;
            long created = ledger.GoldCreatedBy(value), destroyed = ledger.GoldDestroyedBy(value), items = ledger.ItemsDestroyedBy(value);
            if (created != 0 || destroyed != 0 || items != 0)
                _queue.TryEnqueue(new TelemetryRecord(TelemetryKind.EconomyByReason, now, WorldId: _worldId, Subject: reason,
                    V0: created, V1: destroyed, V2: items));
        }
    }
}
