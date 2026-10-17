using GameServer.Net;
using GameServer.Net.Security;
using GameServer.Net.Tickets;
using GameServer.World;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Util;

namespace GameServer.Host;

/// <summary>The config rows the game server reads at start.</summary>
public sealed record ServerConfig(StartingValuesM1 StartingValues, NetSecurity NetSecurity, ServerLoop ServerLoop)
{
    public static ServerConfig From(ConfigSet set) =>
        new(set.Single<StartingValuesM1>(), set.Single<NetSecurity>(), set.Single<ServerLoop>());
}

/// <summary>
/// Builds and runs one game server process (spec 1.6): the main world on its simulation thread and the transport
/// polled by a network thread. Instances and the WorldManager come later (spec 2.3).
/// </summary>
public sealed class Bootstrap : IDisposable
{
    private readonly ServerSettings _settings;
    private readonly NetworkPump _pump;
    private bool _started;
    private bool _stopped;

    public Bootstrap(ServerConfig config, ServerSettings settings, IClock clock, ITickClock tickClock,
        ISecurityEventSink? securityEvents = null)
    {
        _settings = settings;
        var net = config.NetSecurity;
        var keys = new TicketKeyRing().Add(settings.TicketKeyId, settings.TicketSecret);
        var tickets = new TicketValidator(keys, new InMemoryNonceCache(clock), clock, settings.RealmId, net.TicketLifetimeMs);

        var inbox = new WorldInbox();
        Router = new InputRouter(inbox, config.ServerLoop.MaxQueuedInputFrames);
        // TODO(H2): security events go to telemetry (R-2.5-06).
        Transport = new LiteNetLibTransport(new ConnectionApprover(tickets), Router, net, clock, securityEvents);
        // TODO(H2): pass the event bus dispatch and telemetry flush hooks for phases 11 and 12.
        var world = new GameWorld(WorldSettings.From(config.StartingValues, config.ServerLoop, net), inbox, Transport);
        WorldThread = new WorldThread(world, tickClock);
        _pump = new NetworkPump(Transport, config.ServerLoop.NetPollIntervalMs);
    }

    public LiteNetLibTransport Transport { get; }

    public InputRouter Router { get; }

    public WorldThread WorldThread { get; }

    public GameWorld World => WorldThread.World;

    /// <summary>Binds the port, then starts the simulation and network threads.</summary>
    public void Start()
    {
        if (_started)
            throw new InvalidOperationException("Already started.");
        _started = true;
        Transport.Start(_settings.Port);
        WorldThread.Start();
        _pump.Start();
    }

    /// <summary>
    /// Stops the simulation first so nothing sends during shutdown, then the network thread, then the transport,
    /// which disconnects every peer. TODO(H1): flush pending saves before the transport goes (save classes, rule 11).
    /// </summary>
    public void Stop()
    {
        if (_stopped)
            return;
        _stopped = true;
        WorldThread.Stop();
        _pump.Stop();
        Transport.Stop();
    }

    public void Dispose()
    {
        Stop();
        WorldThread.Dispose();
        _pump.Dispose();
        Transport.Dispose();
    }
}
