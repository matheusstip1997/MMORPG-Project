using GameServer.Events;
using GameServer.Net;
using GameServer.Net.Tickets;
using GameServer.Persistence;
using GameServer.Sessions;
using GameServer.Telemetry;
using GameServer.World;
using GameServer.World.Map;
using Npgsql;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Util;
using StackExchange.Redis;

namespace GameServer.Host;

/// <summary>The config rows the game server reads at start.</summary>
public sealed record ServerConfig(
    StartingValuesM1 StartingValues,
    NetSecurity NetSecurity,
    ServerLoop ServerLoop,
    EventBusLimits EventBusLimits,
    TelemetrySettings Telemetry,
    SessionSettings Sessions)
{
    public static ServerConfig From(ConfigSet set) =>
        new(set.Single<StartingValuesM1>(), set.Single<NetSecurity>(), set.Single<ServerLoop>(),
            set.Single<EventBusLimits>(), set.Single<TelemetrySettings>(), set.Single<SessionSettings>());
}

/// <summary>
/// Builds and runs one game server process (spec 1.6): the main world on its simulation thread, the transport polled by
/// a network thread, the persistence worker, and the telemetry writer thread (spec 7.2). Redis holds the ticket nonces
/// and the character ownership locks (spec 2.1, 2.5); the realm database holds the characters (spec 7.1).
/// Instances and the WorldManager come later (spec 2.3).
/// </summary>
public sealed class Bootstrap : IDisposable
{
    /// <summary>The main map. TODO(WorldManager, spec 2.3): instances get their own ids and buses.</summary>
    public const int MainWorldId = 1;

    private readonly ServerSettings _settings;
    private readonly NetworkPump _pump;
    private readonly TelemetryWriter _telemetryWriter;
    private readonly ConnectionMultiplexer _redis;
    private readonly NpgsqlDataSource _realmDb;
    private readonly PersistenceWorker _persistence;
    private bool _started;
    private bool _stopped;

    /// <param name="map">The verified world from <see cref="WorldLoader"/> (spec 11.6: checked at startup).</param>
    public Bootstrap(ServerConfig config, ServerSettings settings, IClock clock, ITickClock tickClock, WorldMap map)
    {
        _settings = settings;
        var net = config.NetSecurity;
        ServerInstanceId = Guid.NewGuid().ToString("N");

        // Fails at start, not at the first login, if Redis cannot be reached.
        _redis = ConnectionMultiplexer.Connect(settings.Redis);
        _realmDb = NpgsqlDataSource.Create(settings.RealmDatabase);

        var keys = new TicketKeyRing().Add(settings.TicketKeyId, settings.TicketSecret);
        var nonces = new RedisNonceCache(_redis, settings.RedisKeyPrefix, clock); // single use across servers (R-2.5-01)
        var tickets = new TicketValidator(keys, nonces, clock, settings.RealmId, net.TicketLifetimeMs);

        // Telemetry is process-wide: one queue that every thread enqueues into without waiting, drained by one writer.
        TelemetryQueue = new TelemetryQueue(config.Telemetry);
        TelemetryRunId = Telemetry.TelemetryRunId.Create(clock);
        var sink = new JsonLinesTelemetrySink(settings.TelemetryDirectory, TelemetryRunId, clock, config.Telemetry.MaxFileBytes);
        _telemetryWriter = new TelemetryWriter(TelemetryQueue, sink, config.Telemetry, clock);

        var inbox = new WorldInbox();
        Router = new InputRouter(inbox, config.ServerLoop.MaxQueuedInputFrames);
        Approver = new ConnectionApprover(tickets);
        Transport = new LiteNetLibTransport(Approver, Router, net, clock,
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

        // One persistence worker per world keeps its saves in order (spec 1.6, 7.1).
        var characters = new CharacterRepository(_realmDb, settings.RealmId, clock);
        _persistence = new PersistenceWorker(characters, clock, TelemetryQueue);
        var locks = new RedisOwnershipLocks(_redis, settings.RedisKeyPrefix, settings.RealmId, config.Sessions.OwnershipLockTtlMs);
        Sessions = new SessionManager(world, new RepositoryCharacterSource(characters), locks, _persistence, _persistence,
            Transport, clock, config.StartingValues, config.Sessions, ServerInstanceId);

        WorldThread = new WorldThread(world, tickClock, worldTelemetry.ReportCrash);
        worldTelemetry.Attach(WorldThread);
        _pump = new NetworkPump(Transport, config.ServerLoop.NetPollIntervalMs);
    }

    /// <summary>Names this process in ownership lock tokens.</summary>
    public string ServerInstanceId { get; }

    public LiteNetLibTransport Transport { get; }

    public ConnectionApprover Approver { get; }

    public InputRouter Router { get; }

    public WorldThread WorldThread { get; }

    public GameWorld World => WorldThread.World;

    /// <summary>The main world's sessions. Simulation thread only while the world runs.</summary>
    public SessionManager Sessions { get; }

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
    /// Graceful shutdown (R-2.1-09, save classes rule 11): block logins, stop the simulation so nothing changes or sends,
    /// end every session, save every character and release its lock (World.Shutdown), then stop the network thread and
    /// the transport (which sends the queued ServerMaintenance kicks and disconnects every peer), finish the queued
    /// saves, and last the telemetry writer, which drains what the others wrote. TODO(B4): announce the shutdown to
    /// players before it starts once a system message exists.
    /// </summary>
    public void Stop()
    {
        if (_stopped)
            return;
        _stopped = true;
        Approver.BlockLogins();
        WorldThread.Stop();
        // The simulation thread has ended, so this thread may touch the world. A crashed world is left as it is: its
        // state is not trusted and is not saved, its crash is already recorded, and its locks expire with their TTL.
        if (WorldThread.Failure == null)
            World.Shutdown();
        _pump.Stop();
        Transport.Stop();
        _persistence.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _telemetryWriter.Dispose();
    }

    public void Dispose()
    {
        Stop();
        WorldThread.Dispose();
        _pump.Dispose();
        Transport.Dispose();
        _realmDb.Dispose();
        _redis.Dispose();
    }
}
