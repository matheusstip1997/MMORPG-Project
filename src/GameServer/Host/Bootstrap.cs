using GameServer.Events;
using GameServer.Net;
using GameServer.Net.Tickets;
using GameServer.Telemetry;
using GameServer.World;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Util;

namespace GameServer.Host;

/// <summary>The config rows the game server reads at start.</summary>
public sealed record ServerConfig(
    StartingValuesM1 StartingValues,
    NetSecurity NetSecurity,
    ServerLoop ServerLoop,
    EventBusLimits EventBusLimits,
    TelemetrySettings Telemetry)
{
    public static ServerConfig From(ConfigSet set) =>
        new(set.Single<StartingValuesM1>(), set.Single<NetSecurity>(), set.Single<ServerLoop>(),
            set.Single<EventBusLimits>(), set.Single<TelemetrySettings>());
}

/// <summary>
/// Builds and runs one game server process (spec 1.6): the main world on its simulation thread, the transport polled by
/// a network thread, and the telemetry writer thread (spec 7.2). Instances and the WorldManager come later (spec 2.3).
/// </summary>
public sealed class Bootstrap : IDisposable
{
    /// <summary>The main map. TODO(WorldManager, spec 2.3): instances get their own ids and buses.</summary>
    public const int MainWorldId = 1;

    private readonly ServerSettings _settings;
    private readonly NetworkPump _pump;
    private readonly TelemetryWriter _telemetryWriter;
    private bool _started;
    private bool _stopped;

    /// <param name="map">The verified world from <see cref="WorldLoader"/> (spec 11.6: checked at startup).</param>
    public Bootstrap(ServerConfig config, ServerSettings settings, IClock clock, ITickClock tickClock, WorldMap map)
    {
        _settings = settings;
        var net = config.NetSecurity;
        var keys = new TicketKeyRing().Add(settings.TicketKeyId, settings.TicketSecret);
        var tickets = new TicketValidator(keys, new InMemoryNonceCache(clock), clock, settings.RealmId, net.TicketLifetimeMs);

        // Telemetry is process-wide: one queue that every thread enqueues into without waiting, drained by one writer.
        TelemetryQueue = new TelemetryQueue(config.Telemetry);
        TelemetryRunId = Telemetry.TelemetryRunId.Create(clock);
        var sink = new JsonLinesTelemetrySink(settings.TelemetryDirectory, TelemetryRunId, clock, config.Telemetry.MaxFileBytes);
        _telemetryWriter = new TelemetryWriter(TelemetryQueue, sink, config.Telemetry, clock);

        var inbox = new WorldInbox();
        Router = new InputRouter(inbox, config.ServerLoop.MaxQueuedInputFrames);
        Transport = new LiteNetLibTransport(new ConnectionApprover(tickets), Router, net, clock,
            new TelemetrySecurityEventSink(TelemetryQueue)); // R-2.5-06

        // The bus belongs to the world's thread (spec 1.9). Consumer failures are counted on the bus and reported by
        // WorldTelemetry; nothing is printed from the tick.
        Events = new EventBus(MainWorldId, clock, config.EventBusLimits);
        TelemetryEventConsumer.Attach(Events, TelemetryQueue);
        var worldSettings = WorldSettings.From(config.StartingValues, config.ServerLoop, net);
        var worldTelemetry = new WorldTelemetry(MainWorldId, TelemetryQueue, _telemetryWriter, clock, config.Telemetry,
            worldSettings.TickRateHz, Router, Events);
        var world = new GameWorld(worldSettings, map, inbox, Transport,
            eventDispatch: new EventDispatchHook(Events), telemetryFlush: worldTelemetry, events: Events,
            violations: new TelemetryMovementViolationSink(MainWorldId, TelemetryQueue, clock));
        WorldThread = new WorldThread(world, tickClock, worldTelemetry.ReportCrash);
        worldTelemetry.Attach(WorldThread);
        _pump = new NetworkPump(Transport, config.ServerLoop.NetPollIntervalMs);
    }

    public LiteNetLibTransport Transport { get; }

    public InputRouter Router { get; }

    public WorldThread WorldThread { get; }

    public GameWorld World => WorldThread.World;

    /// <summary>The main world's bus. Simulation thread only while the world runs.</summary>
    public EventBus Events { get; }

    public TelemetryQueue TelemetryQueue { get; }

    /// <summary>Names this run's files in <see cref="ServerSettings.TelemetryDirectory"/>: <c>&lt;run&gt;-&lt;nnnn&gt;.jsonl</c>.</summary>
    public string TelemetryRunId { get; }

    /// <summary>Starts the telemetry writer, binds the port, then starts the simulation and network threads.</summary>
    public void Start()
    {
        if (_started)
            throw new InvalidOperationException("Already started.");
        _started = true;
        _telemetryWriter.Start();
        Transport.Start(_settings.Port);
        WorldThread.Start();
        _pump.Start();
    }

    /// <summary>
    /// Stops the simulation first so nothing sends during shutdown, then ends the sessions it held, then stops the
    /// network thread and the transport, which disconnects every peer. The telemetry writer goes last: it drains what
    /// the others wrote and closes the file. TODO(H1): flush pending saves before the transport goes (save classes, rule 11).
    /// </summary>
    public void Stop()
    {
        if (_stopped)
            return;
        _stopped = true;
        WorldThread.Stop();
        // The simulation thread has ended, so this thread may touch the world. A crashed world is left as it is: its
        // state is not trusted, and its crash is already recorded.
        if (WorldThread.Failure == null)
            World.Shutdown();
        _pump.Stop();
        Transport.Stop();
        _telemetryWriter.Dispose();
    }

    public void Dispose()
    {
        Stop();
        WorldThread.Dispose();
        _pump.Dispose();
        Transport.Dispose();
    }
}
