using GameServer.Entities;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Builds one observer's snapshot from its interest set and sends it on the unreliable channel (spec 1.7, tick
/// phase 10). Writes into one reused buffer, so sending allocates nothing.
/// <para>
/// Contents: despawns for entities that left interest, spawns for entities that entered, then the state of every
/// entity in interest. The observer's own entity is always the <b>first entity</b>: that is how the client knows
/// which entity it controls until B4 adds a reliable enter-world message. The header acknowledges the newest
/// applied input, so the client reconciles by replaying later frames; the server sends its authoritative state in
/// every snapshot and the client corrects only when its error exceeds its tolerance (spec 2.2).
/// </para>
/// <para>
/// Axes: snapshots are 3D with Y up (spec 1.7), so the ground-plane X and Y of MovementSim go out as X and Z, and
/// the height as Y.
/// </para>
/// TODO(D2): action state codes. TODO(D5): HP bucket from resource meters. TODO(spec 1.7): armed, red-name,
/// disguised, mounted, pet flags. TODO: priority and deltas when interest outgrows one datagram; for now what does
/// not fit is left out and counted.
/// </summary>
public sealed class SnapshotSender
{
    private readonly ITransport _transport;
    private readonly byte[] _buffer;

    public SnapshotSender(ITransport transport, int maxSnapshotBytes)
    {
        if (maxSnapshotBytes < Snapshot.HeaderSize)
            throw new ArgumentOutOfRangeException(nameof(maxSnapshotBytes), "must hold a snapshot header");
        _transport = transport;
        _buffer = new byte[maxSnapshotBytes];
    }

    /// <summary>Snapshots that left out at least one despawn, spawn, or entity because the datagram was full.</summary>
    public long Truncated { get; private set; }

    public void Send(Player observer, uint serverTick)
    {
        var header = new SnapshotHeader
        {
            ServerTick = serverTick,
            HasInputAck = observer.HasInputAck,
            LastInputSequence = observer.LastInputSequence,
            OriginXCm = observer.XCm,
            OriginYCm = observer.HeightCm,
            OriginZCm = observer.YCm,
        };
        var writer = new SnapshotWriter(_buffer, header);
        var interest = observer.Interest;
        bool complete = true;

        foreach (uint id in interest.Left)
            complete &= writer.TryAddDespawn(id);

        foreach (var entity in interest.Entered)
            complete &= writer.TryAddSpawn(new SpawnEvent { EntityId = entity.Id, Kind = entity.Kind, DefId = 0 });

        complete &= TryAddEntity(ref writer, header, observer);
        var visible = interest.Visible;
        for (int i = 0; i < visible.Count; i++)
        {
            if (visible[i] != observer)
                complete &= TryAddEntity(ref writer, header, visible[i]);
        }

        if (!complete)
            Truncated++;
        int length = writer.Finish();
        _transport.Send(observer.PeerId, Channel.UnreliableSequenced, _buffer.AsSpan(0, length));
    }

    private static bool TryAddEntity(ref SnapshotWriter writer, in SnapshotHeader header, Entity entity)
    {
        var snapshot = new EntitySnapshot
        {
            EntityId = entity.Id,
            XCm = entity.XCm,
            YCm = entity.HeightCm,
            ZCm = entity.YCm,
            Facing = entity.Movement.Facing,
            State = 0,
            HpBucket = Snapshot.TopHpBucket,
            Flags = EntityFlags.None,
        };
        // Interest is far smaller than the offset range, so this only fails on a bug; skip rather than crash the tick.
        return Snapshot.FitsOrigin(header, snapshot) && writer.TryAddEntity(snapshot);
    }
}
