using GameServer.Host;
using GameServer.Net;
using GameServer.World;
using Shared.Protocol;

namespace GameServer.Tests.Support;

/// <summary>Monotonic fake: waiting moves time to the deadline; tests move it further to simulate work.</summary>
public sealed class FakeTickClock(long nowMicros = 1_000_000) : ITickClock
{
    public long NowMicros { get; private set; } = nowMicros;

    public void Advance(long micros) => NowMicros += micros;

    public void WaitUntil(long micros, CancellationToken cancellation)
    {
        if (micros > NowMicros)
            NowMicros = micros;
    }
}

/// <summary>Records what the world sends. With <see cref="Record"/> off it only counts, so it allocates nothing.</summary>
public sealed class FakeTransport : ITransport
{
    public bool Record { get; set; } = true;

    public int SendCount { get; private set; }

    public List<(int PeerId, Channel Channel, byte[] Data)> Sent { get; } = [];

    public int LocalPort => 0;

    public int ConnectedPeers => 0;

    public void Start(int port)
    {
    }

    public void Stop()
    {
    }

    public void PollEvents()
    {
    }

    public void Send(int peerId, Channel channel, ReadOnlySpan<byte> data)
    {
        SendCount++;
        if (Record)
            Sent.Add((peerId, channel, data.ToArray()));
    }

    /// <summary>Kicks the world asked for, in order.</summary>
    public List<(int PeerId, ConnectRejectReason Reason)> Kicks { get; } = [];

    public void Disconnect(int peerId, ConnectRejectReason reason = ConnectRejectReason.None) => Kicks.Add((peerId, reason));

    public void Dispose()
    {
    }

    public List<DecodedSnapshot> SnapshotsFor(int peerId) =>
        Sent.Where(s => s.PeerId == peerId && s.Channel == Channel.UnreliableSequenced)
            .Select(s => DecodedSnapshot.Decode(s.Data)).ToList();

    /// <summary>Refused actions sent to a peer on the reliable channel (backlog D2).</summary>
    public List<(ushort Sequence, ushort ActionId, byte Slot, ActionReason Reason)> ActionResultsFor(int peerId)
    {
        var results = new List<(ushort, ushort, byte, ActionReason)>();
        foreach (var (peer, channel, data) in Sent)
        {
            if (peer != peerId || channel != Channel.ReliableOrdered)
                continue;
            Assert.True(ActionResult.TryRead(data, out ushort sequence, out ushort actionId, out byte slot, out ActionReason reason));
            results.Add((sequence, actionId, slot, reason));
        }
        return results;
    }
}

/// <summary>A snapshot read back with the shared <see cref="SnapshotReader"/>.</summary>
public sealed record DecodedSnapshot(
    SnapshotHeader Header,
    List<uint> Despawns,
    List<SpawnEvent> Spawns,
    List<EntitySnapshot> Entities)
{
    public static DecodedSnapshot Decode(byte[] data)
    {
        Assert.True(SnapshotReader.TryBegin(data, out var reader), "Not a valid snapshot.");
        var despawns = new List<uint>();
        var spawns = new List<SpawnEvent>();
        var entities = new List<EntitySnapshot>();
        while (reader.TryReadDespawn(out var id))
            despawns.Add(id);
        while (reader.TryReadSpawn(out var spawn))
            spawns.Add(spawn);
        while (reader.TryReadEntity(out var entity))
            entities.Add(entity);
        return new DecodedSnapshot(reader.Header, despawns, spawns, entities);
    }

    /// <summary>The observer's own entity is always the first one (SnapshotSender).</summary>
    public EntitySnapshot Self => Entities[0];
}

public static class TestWorlds
{
    /// <summary>The M1 values from data/tables, written out so a table change does not silently change these tests.</summary>
    public static WorldSettings Settings(int snapshotRateHz = 20, int maxQueuedInputFrames = 4) => new(
        TickRateHz: 20,
        SnapshotRateHz: snapshotRateHz,
        MoveSpeedCmPerSec: 400,
        InterestRadiusCm: 3_000,
        GridCellCm: 1_500,
        PlayerSpawnMarkerId: TestMaps.SpawnId,
        SpeedTolerancePercent: 10,
        MaxQueuedInputFrames: maxQueuedInputFrames,
        MaxSnapshotBytes: 1_200);

    public static InputFrame Frame(ushort sequence, MoveKeys keys = MoveKeys.None, ushort facing = 0,
        InputActions actions = InputActions.None, byte skillSlot = 0) => new()
    {
        Sequence = sequence,
        ClientTick = 1_000u + sequence,
        Keys = keys,
        Facing = facing,
        Actions = actions,
        SkillSlot = skillSlot,
    };
}

/// <summary>A world with a fake transport, driven tick by tick from the test thread.</summary>
public sealed class WorldHarness
{
    /// <param name="values">Session values (linger, save intervals); default: no linger, see <see cref="TestSessions.Values"/>.</param>
    public WorldHarness(WorldSettings? settings = null, IVisibilityQuery? visibility = null, ITickHook? events = null,
        ITickHook? telemetry = null, GameServer.Events.EventBus? bus = null, GameServer.World.Map.WorldMap? map = null,
        IMovementViolationSink? violations = null,
        Shared.Data.Tables.StartingValuesM1? values = null, Shared.Data.Tables.SessionSettings? sessionSettings = null,
        Shared.Simulation.Actions.ActionCatalog? actions = null, GameServer.Combat.CombatRules? combat = null)
    {
        var resolved = settings ?? TestWorlds.Settings();
        var session = sessionSettings ?? TestSessions.Settings();
        Inbox = new WorldInbox();
        Router = new InputRouter(Inbox, resolved.MaxQueuedInputFrames);
        World = new GameWorld(resolved, map ?? TestMaps.Open(), Inbox, Transport, visibility, events, telemetry, bus, violations,
            actions, combat);
        Locks = new InMemoryOwnershipLocks(Clock, session.OwnershipLockTtlMs);
        Saves.Store = Characters;
        Sessions = new GameServer.Sessions.SessionManager(World, Characters, Locks, Saves, Saves, Transport, Clock,
            values ?? TestSessions.Values(), session, "test-server");
    }

    public FakeClock Clock { get; } = new();

    public FakeCharacterSource Characters { get; } = new();

    public InMemoryOwnershipLocks Locks { get; }

    public FakeSaveWorker Saves { get; } = new();

    public GameServer.Sessions.SessionManager Sessions { get; }

    public FakeTransport Transport { get; } = new();

    public WorldInbox Inbox { get; }

    public InputRouter Router { get; }

    public GameWorld World { get; }

    /// <summary>
    /// Connects a peer through the router, as the transport would, and runs a tick so it spawns. Each peer is its own
    /// account unless <paramref name="accountId"/> says otherwise (one session per account, R-2.1-04).
    /// </summary>
    public GameServer.Entities.Player Join(int peerId, long? accountId = null, byte slot = 0)
    {
        Connect(peerId, accountId, slot);
        World.Tick();
        Assert.True(World.TryGetPlayer(peerId, out var player));
        return player;
    }

    /// <summary>Connects a peer through the router without ticking.</summary>
    public void Connect(int peerId, long? accountId = null, byte slot = 0) =>
        Router.OnPeerConnected(peerId, new GameServer.Net.Tickets.SessionTicket(accountId ?? peerId, 1, slot, 0, new byte[16], 1));

    public void Leave(int peerId) => Router.OnPeerDisconnected(peerId, PeerDisconnectReason.ClosedByClient);

    /// <summary>Sends an input packet through the router, as the transport would after verifying its tag.</summary>
    public void SendInputs(int peerId, params InputFrame[] newestFirst)
    {
        var buffer = new byte[InputPacket.MaxSize];
        int length = InputPacket.Write(buffer, newestFirst);
        Router.OnDataReceived(peerId, Channel.UnreliableSequenced, buffer.AsSpan(0, length));
    }

    public void Ticks(int count)
    {
        for (int i = 0; i < count; i++)
            World.Tick();
    }

    public DecodedSnapshot LastSnapshot(int peerId) => Transport.SnapshotsFor(peerId)[^1];
}
