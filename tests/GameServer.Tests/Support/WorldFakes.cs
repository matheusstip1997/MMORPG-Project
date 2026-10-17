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

    public void Disconnect(int peerId)
    {
    }

    public void Dispose()
    {
    }

    public List<DecodedSnapshot> SnapshotsFor(int peerId) =>
        Sent.Where(s => s.PeerId == peerId).Select(s => DecodedSnapshot.Decode(s.Data)).ToList();
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

    public static InputFrame Frame(ushort sequence, MoveKeys keys = MoveKeys.None, ushort facing = 0) => new()
    {
        Sequence = sequence,
        ClientTick = 1_000u + sequence,
        Keys = keys,
        Facing = facing,
    };
}

/// <summary>A world with a fake transport, driven tick by tick from the test thread.</summary>
public sealed class WorldHarness
{
    public WorldHarness(WorldSettings? settings = null, IVisibilityQuery? visibility = null, ITickHook? events = null,
        ITickHook? telemetry = null, GameServer.Events.EventBus? bus = null, GameServer.World.Map.WorldMap? map = null,
        IMovementViolationSink? violations = null)
    {
        var resolved = settings ?? TestWorlds.Settings();
        Inbox = new WorldInbox();
        Router = new InputRouter(Inbox, resolved.MaxQueuedInputFrames);
        World = new GameWorld(resolved, map ?? TestMaps.Open(), Inbox, Transport, visibility, events, telemetry, bus, violations);
    }

    public FakeTransport Transport { get; } = new();

    public WorldInbox Inbox { get; }

    public InputRouter Router { get; }

    public GameWorld World { get; }

    /// <summary>Connects a peer through the router, as the transport would, and runs a tick so it spawns.</summary>
    public GameServer.Entities.Player Join(int peerId, long accountId = 1)
    {
        Router.OnPeerConnected(peerId, new GameServer.Net.Tickets.SessionTicket(accountId, 1, 0, 0, new byte[16], 1));
        World.Tick();
        Assert.True(World.TryGetPlayer(peerId, out var player));
        return player;
    }

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
