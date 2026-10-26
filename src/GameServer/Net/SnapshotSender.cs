using GameServer.Entities;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

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
/// Action state: the entity's ActionMachine state code with its action id and age (backlog D2). Resources (spec 9.4): the
/// header carries the observer's exact HP, MP and stamina; every entity carries its HP bucket (full for an entity without
/// a combatant). Protocol 7: the header also carries the observer's statuses (spec 3.7 buff row: net id, stacks, ticks left),
/// in the order they were applied, at most <see cref="Snapshot.MaxOwnStatuses"/>; protocol 9: then its build-up meters
/// above 0 (spec 3.8, only the owner's). Spawns carry the entity's definition id (<see cref="Entity.DefId"/>). TODO(spec 1.7): armed, red-name,
/// disguised, mounted, pet flags. TODO: priority and deltas when interest outgrows one datagram; for now what does
/// not fit is left out and counted. One datagram is the smaller of <c>maxSnapshotBytes</c> and the peer's current limit
/// (<see cref="ITransport.MaxUnreliableBytes"/>: LiteNetLib does not fragment unreliable messages).
/// </summary>
public sealed class SnapshotSender
{
    private readonly ITransport _transport;
    private readonly byte[] _buffer;
    private readonly OwnStatus[] _ownStatuses = new OwnStatus[Snapshot.MaxOwnStatuses];
    private readonly OwnBuildUp[] _ownBuildUps = new OwnBuildUp[Snapshot.MaxOwnBuildUps];

    public SnapshotSender(ITransport transport, int maxSnapshotBytes)
    {
        if (maxSnapshotBytes < Snapshot.HeaderSizeWithOwn(Snapshot.MaxOwnStatuses, Snapshot.MaxOwnBuildUps))
            throw new ArgumentOutOfRangeException(nameof(maxSnapshotBytes), "must hold a snapshot header");
        _transport = transport;
        _buffer = new byte[maxSnapshotBytes];
    }

    /// <summary>The build-ups behind the own meters (spec 3.8); null sends none (a world without combat rules).</summary>
    internal GameServer.Combat.BuildUpCatalog? BuildUps { get; set; }

    /// <summary>
    /// Snapshots that left out at least one despawn, spawn, or entity because the datagram was full (the buffer or the
    /// peer's limit, <see cref="ITransport.MaxUnreliableBytes"/>), or that were not sent because not even the header fit.
    /// </summary>
    public long Truncated { get; private set; }

    /// <summary>Own statuses left out because the observer had more than <see cref="Snapshot.MaxOwnStatuses"/> (TODO(open): buff row capacity, spec 9.7).</summary>
    public long StatusesLeftOut { get; private set; }

    /// <param name="nowMs">The world's simulation time, for the resource meters (<c>CombatRules.MsAt</c>).</param>
    public void Send(Player observer, uint serverTick, long nowMs)
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
        if (observer.Combatant is { } own)
        {
            var meters = own.Meters;
            header.HasOwnResources = true;
            header.Own = new OwnResources
            {
                Hp = own.Hp(nowMs),
                MaxHp = meters.Max(ResourceKind.Hp, nowMs),
                Mp = meters.Current(ResourceKind.Mp, nowMs),
                MaxMp = meters.Max(ResourceKind.Mp, nowMs),
                Stamina = meters.Current(ResourceKind.Stamina, nowMs),
                MaxStamina = meters.Max(ResourceKind.Stamina, nowMs),
            };
        }
        int statuses = observer.Combatant is { } self ? FillOwnStatuses(self, serverTick) : 0;
        int buildUps = observer.Combatant is { } mine ? FillOwnBuildUps(mine, serverTick) : 0;
        // One datagram: within the buffer and the peer's current limit (its MTU, R-2.5-03), so the transport never refuses it.
        int limit = Math.Min(_buffer.Length, _transport.MaxUnreliableBytes(observer.PeerId));
        if (limit <= 0)
            return; // the peer is gone
        if (limit < (header.HasOwnResources ? Snapshot.HeaderSizeWithOwn(statuses, buildUps) : Snapshot.HeaderSize))
        {
            Truncated++;
            return;
        }
        var writer = new SnapshotWriter(_buffer.AsSpan(0, limit), header, new ReadOnlySpan<OwnStatus>(_ownStatuses, 0, statuses),
            new ReadOnlySpan<OwnBuildUp>(_ownBuildUps, 0, buildUps));
        var interest = observer.Interest;
        bool complete = true;

        foreach (uint id in interest.Left)
            complete &= writer.TryAddDespawn(id);

        foreach (var entity in interest.Entered)
            complete &= writer.TryAddSpawn(new SpawnEvent { EntityId = entity.Id, Kind = entity.Kind, DefId = entity.DefId });

        complete &= TryAddEntity(ref writer, header, observer, nowMs);
        var visible = interest.Visible;
        for (int i = 0; i < visible.Count; i++)
        {
            if (visible[i] != observer)
                complete &= TryAddEntity(ref writer, header, visible[i], nowMs);
        }

        if (!complete)
            Truncated++;
        int length = writer.Finish();
        _transport.Send(observer.PeerId, Channel.UnreliableSequenced, _buffer.AsSpan(0, length));
    }

    /// <summary>Spec 9.4: the bucket of the entity's HP bar; full for an entity without a combatant.</summary>
    public static byte HpBucketOf(Entity entity, long nowMs)
    {
        if (entity.Combatant is not { } combatant)
            return Snapshot.TopHpBucket;
        var meters = combatant.Meters;
        return (byte)Quantize.Bucket(combatant.Hp(nowMs), meters.Max(ResourceKind.Hp, nowMs),
            Snapshot.TopHpBucket);
    }

    /// <summary>The observer's statuses for the buff row, in the order they were applied; returns how many.</summary>
    private int FillOwnStatuses(GameServer.Combat.Combatant own, uint serverTick)
    {
        var set = own.Statuses;
        int count = Math.Min(set.Count, _ownStatuses.Length);
        if (set.Count > count)
            StatusesLeftOut += set.Count - count;
        for (int i = 0; i < count; i++)
        {
            ref var instance = ref set[i];
            long remaining = (long)instance.EndTick - serverTick;
            _ownStatuses[i] = new OwnStatus
            {
                NetId = (ushort)instance.Definition.NetId,
                Stacks = (byte)instance.Stacks,
                RemainingTicks = (ushort)Math.Max(1, Math.Min(Snapshot.MaxStatusRemainingTicks, remaining)),
            };
        }
        return count;
    }

    /// <summary>
    /// Protocol 9: the observer's build-up meters above 0 (spec 3.8: only its own, one byte each), in build-up order, whole
    /// points rounded up; returns how many.
    /// </summary>
    private int FillOwnBuildUps(GameServer.Combat.Combatant own, uint serverTick)
    {
        var catalog = BuildUps;
        if (catalog == null)
            return 0;
        var meters = own.BuildUps;
        int count = 0;
        for (int i = 0; i < meters.Length && i < catalog.Count && count < _ownBuildUps.Length; i++)
        {
            var rule = catalog[i];
            int points = meters[i].PointsAt(rule, serverTick);
            if (points > 0)
                _ownBuildUps[count++] = new OwnBuildUp { NetId = (byte)rule.NetId, Points = (byte)points };
        }
        return count;
    }

    private static bool TryAddEntity(ref SnapshotWriter writer, in SnapshotHeader header, Entity entity, long nowMs)
    {
        var action = ActionMachine.ToWire(entity.Actions);
        var snapshot = new EntitySnapshot
        {
            EntityId = entity.Id,
            XCm = entity.XCm,
            YCm = entity.HeightCm,
            ZCm = entity.YCm,
            Facing = entity.Movement.Facing,
            State = action.State,
            ActionId = action.ActionId,
            ParryColor = GameServer.Combat.ParryColors.Running(entity), // the telegraph color (R-3.1-05)
            StateAge = action.Age,
            StateDurationTicks = action.DurationTicks,
            HpBucket = HpBucketOf(entity, nowMs),
            Flags = EntityFlags.None,
        };
        // Interest is far smaller than the offset range, so this only fails on a bug; skip rather than crash the tick.
        return Snapshot.FitsOrigin(header, snapshot) && writer.TryAddEntity(snapshot);
    }
}
