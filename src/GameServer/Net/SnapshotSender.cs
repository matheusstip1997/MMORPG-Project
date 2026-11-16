using System.Runtime.InteropServices;
using GameServer.Entities;
using GameServer.Social;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Net;

/// <summary>
/// Builds one observer's snapshot from its interest set and sends it on the unreliable channel (spec 1.7, tick
/// phase 10). Writes into reused buffers, so sending allocates nothing once they have grown.
/// <para>
/// Contents: despawns for entities that left interest, spawns for entities that entered, then entity states. A spawn or a
/// despawn repeats in every snapshot until the client acknowledges (<see cref="InputQueue.SnapshotAck"/>, protocols 53 and
/// 54) a snapshot that carried it, and an entity's state waits until some snapshot has carried its spawn: a lost or full
/// datagram never leaves the client with an entity it cannot name, nor with one that left. The observer's own entity is
/// always the <b>first entity</b>: that is how the client knows which entity it controls until B4 adds a reliable enter-world
/// message. The header acknowledges the newest applied input, so the client reconciles by replaying later frames; the server
/// sends its authoritative state in every snapshot and the client corrects only when its error exceeds its tolerance (spec 2.2).
/// </para>
/// <para>
/// <b>Priority</b> (spec 1.7, user decision 2026-11-16, option A): when every state fits, every entity in interest goes out
/// (one update rate, R-2.3-03). When not, the client keeps an entity it does not hear about where it last saw it (protocol
/// 54: only a despawn removes one), and the states go in this order until the datagram is full: the own entity; the
/// critical ones (in an action within <see cref="SnapshotPriorityRules.TelegraphRadiusSq"/>, the observer's lock target, its
/// party members, any state at least <see cref="SnapshotPriorityRules.MaxStaleTicks"/> old); then the rest by (ticks since its
/// state was sent + 1) x its kind's weight x its distance weight; ties by entity id (rule 6). An entity still waiting for its
/// spawn goes with its spawn, as one unit. Without <see cref="Priority"/> (worlds built by hand) the order is the age alone.
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
/// disguised, mounted, pet flags. TODO(later, user decision 2026-11-16): deltas against the acknowledged snapshot (option C).
/// One datagram is the smaller of <c>maxSnapshotBytes</c> and the peer's current limit
/// (<see cref="ITransport.MaxUnreliableBytes"/>: LiteNetLib does not fragment unreliable messages).
/// </summary>
public sealed class SnapshotSender
{
    private readonly ITransport _transport;
    private readonly byte[] _buffer;
    private readonly OwnStatus[] _ownStatuses = new OwnStatus[Snapshot.MaxOwnStatuses];
    private readonly OwnBuildUp[] _ownBuildUps = new OwnBuildUp[Snapshot.MaxOwnBuildUps];
    // One candidate per entity in interest, by its index in the interest list; the order of the indexes when it overflows.
    private Candidate[] _candidates = new Candidate[64];
    private int[] _order = new int[64];
    // The candidate index of each pending spawn, by its index in the pending list.
    private int[] _spawnCandidate = new int[16];
    private readonly CandidateOrder _orderComparer = new();
    private bool _ranked; // the candidates of this snapshot have their priority

    public SnapshotSender(ITransport transport, int maxSnapshotBytes)
    {
        if (maxSnapshotBytes < Snapshot.HeaderSizeWithOwn(Snapshot.MaxOwnStatuses, Snapshot.MaxOwnBuildUps))
            throw new ArgumentOutOfRangeException(nameof(maxSnapshotBytes), "must hold a snapshot header");
        _transport = transport;
        _buffer = new byte[maxSnapshotBytes];
    }

    /// <summary>The build-ups behind the own meters (spec 3.8); null sends none (a world without combat rules).</summary>
    internal GameServer.Combat.BuildUpCatalog? BuildUps { get; set; }

    /// <summary>Which states win when a snapshot is full (<c>SnapshotPriority</c>); null orders them by age alone.</summary>
    public SnapshotPriorityRules? Priority { get; set; }

    /// <summary>Party members are critical in a full snapshot (spec 6.5); none without a party service.</summary>
    public IPartyQuery Parties { get; set; } = NoParties.Instance;

    /// <summary>
    /// Snapshots that left out at least one despawn, spawn, or entity because the datagram was full (the buffer or the
    /// peer's limit, <see cref="ITransport.MaxUnreliableBytes"/>), or that were not sent because not even the header fit.
    /// </summary>
    public long Truncated { get; private set; }

    /// <summary>Entity states left out of a snapshot for a later one (the client holds the entity where it last saw it).</summary>
    public long StatesDeferred { get; private set; }

    /// <summary>The longest any state waited, in ticks since that entity's state was last sent (1 while each goes every tick).</summary>
    public uint LongestStateWaitTicks { get; private set; }

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
        // Protocols 53 and 54: the client has the snapshot of its acknowledged tick, so the spawns and despawns that snapshot
        // carried are confirmed. A tick after the newest snapshot sent is not one the client can have: it confirms nothing (rule 1).
        uint ack = observer.Inputs.SnapshotAck;
        if (ack != 0 && interest.HasSentSnapshot && (int)(ack - interest.LastSnapshotTick) <= 0)
            interest.Confirm(ack);

        // The own entity is always the first entity (spec 1.7), with its own spawn while unconfirmed: its room is kept first.
        bool ownFits = TryBuildState(header, observer, nowMs, out var ownState);
        int ownSpawn = -1;
        for (int i = 0; i < interest.PendingSpawnCount; i++)
        {
            if (interest.PendingSpawnAt(i).Entity == observer)
                ownSpawn = i;
        }
        int headerBits = Snapshot.HeaderBits + (header.HasOwnResources
            ? Snapshot.OwnResourcesBits + Snapshot.OwnCountBits + statuses * Snapshot.OwnStatusBits + buildUps * Snapshot.OwnBuildUpBits
            : 0);
        int bits = limit * 8 - headerBits - (ownFits ? StateBits(ownState) : 0) - (ownSpawn >= 0 ? Snapshot.SpawnBits : 0);

        // Despawns first, oldest first: a despawn is the only thing that removes an entity from the client (protocol 54).
        for (int i = 0; i < interest.PendingDespawnCount; i++)
        {
            ref var despawn = ref interest.PendingDespawnAt(i);
            if (bits >= Snapshot.DespawnBits && writer.TryAddDespawn(despawn.EntityId))
            {
                bits -= Snapshot.DespawnBits;
                if (!despawn.InRun)
                {
                    despawn.InRun = true;
                    despawn.RunStartTick = serverTick;
                }
            }
            else
            {
                complete = false;
                despawn.InRun = false; // an acknowledgement of this tick cannot confirm it
            }
        }

        int count = Gather(observer, header, serverTick, nowMs, ref complete);
        _ranked = false;

        // Spawns before states (an entity's state waits for its spawn): the own one, then the chosen ones that the client has
        // not confirmed, oldest first. When they do not all fit, the priority order picks which.
        bits = ChooseSpawns(observer, count, bits);
        for (int i = 0; i < interest.PendingSpawnCount; i++)
        {
            ref var pending = ref interest.PendingSpawnAt(i);
            int index = i == ownSpawn ? -1 : _spawnCandidate[i];
            bool chosen = i == ownSpawn || (index >= 0 && _candidates[index].SpawnChosen);
            var entity = pending.Entity;
            if (chosen && writer.TryAddSpawn(new SpawnEvent { EntityId = entity.Id, Kind = entity.Kind, DefId = entity.DefId }))
            {
                if (!pending.InRun)
                {
                    pending.InRun = true;
                    pending.RunStartTick = serverTick;
                }
                pending.EverWritten = true;
                if (index >= 0)
                    _candidates[index].SpawnWritten = true;
                continue;
            }
            complete = false;
            pending.InRun = false; // an acknowledgement of this tick cannot confirm it
        }
        ChooseStates(observer, count, bits);

        // States: the own entity first, then the chosen ones in id order.
        if (!(ownFits && writer.TryAddEntity(ownState)))
            complete = false;
        for (int i = 0; i < count; i++)
        {
            ref var candidate = ref _candidates[i];
            if (candidate.Cost < 0)
                continue; // the observer itself, or counted when gathered
            if (candidate.Chosen && writer.TryAddEntity(candidate.State))
            {
                interest.LastStateSent[candidate.State.EntityId] = serverTick;
                if (candidate.Age > LongestStateWaitTicks)
                    LongestStateWaitTicks = candidate.Age;
                continue;
            }
            complete = false;
            StatesDeferred++;
        }
        for (int i = 0; i < count; i++)
            _candidates[i].Entity = null; // no stale entity references

        if (!complete)
            Truncated++;
        int length = writer.Finish();
        _transport.Send(observer.PeerId, Channel.UnreliableSequenced, _buffer.AsSpan(0, length));
        interest.SnapshotSent(serverTick);
    }

    /// <summary>
    /// One candidate per entity in interest (by index; the observer's own and any that cannot be sent have cost -1): its state,
    /// the ticks since its state last went out, its cost in bits, and whether its spawn is unconfirmed. Returns how many.
    /// </summary>
    private int Gather(Player observer, in SnapshotHeader header, uint serverTick, long nowMs, ref bool complete)
    {
        var interest = observer.Interest;
        var visible = interest.Visible;
        if (_candidates.Length < visible.Count)
        {
            int size = Math.Max(visible.Count, _candidates.Length * 2);
            _candidates = new Candidate[size];
            _order = new int[size];
        }
        for (int i = 0; i < visible.Count; i++)
        {
            var entity = visible[i];
            ref var candidate = ref _candidates[i];
            candidate = default;
            candidate.Entity = entity;
            if (entity == observer)
            {
                candidate.Cost = -1;
                continue;
            }
            // Interest is far smaller than the offset range, so this only fails on a bug; skip rather than crash the tick.
            if (!TryBuildState(header, entity, nowMs, out candidate.State))
            {
                candidate.Cost = -1;
                complete = false;
                continue;
            }
            ref uint lastSent = ref CollectionsMarshal.GetValueRefOrAddDefault(interest.LastStateSent, entity.Id, out bool known);
            if (!known)
                lastSent = serverTick - 1; // new in interest: as old as a state sent the tick before
            candidate.Age = serverTick - lastSent;
            candidate.Cost = StateBits(candidate.State);
        }
        if (_spawnCandidate.Length < interest.PendingSpawnCount)
            _spawnCandidate = new int[Math.Max(interest.PendingSpawnCount, _spawnCandidate.Length * 2)];
        for (int i = 0; i < interest.PendingSpawnCount; i++)
        {
            ref var pending = ref interest.PendingSpawnAt(i);
            int index = IndexOf(visible, pending.Entity.Id);
            _spawnCandidate[i] = index;
            if (index >= 0 && _candidates[index].Cost >= 0)
            {
                _candidates[index].SpawnPending = true;
                _candidates[index].SpawnWritten = pending.EverWritten;
            }
        }
        return visible.Count;
    }

    /// <summary>
    /// Marks the unconfirmed spawns that go out (all when they fit, else in the priority order) and returns the bits left.
    /// </summary>
    private int ChooseSpawns(Player observer, int count, int bits)
    {
        int sendable = 0;
        for (int i = 0; i < count; i++)
        {
            if (_candidates[i].Cost >= 0 && _candidates[i].SpawnPending)
                _order[sendable++] = i;
        }
        if ((long)sendable * Snapshot.SpawnBits > bits)
            Sort(observer, sendable);
        for (int i = 0; i < sendable && bits >= Snapshot.SpawnBits; i++)
        {
            _candidates[_order[i]].SpawnChosen = true;
            bits -= Snapshot.SpawnBits;
        }
        return bits;
    }

    /// <summary>
    /// Marks the states that go out: every one whose spawn some snapshot carried, all when they fit, else in the priority order
    /// of the class summary until the datagram is full.
    /// </summary>
    private void ChooseStates(Player observer, int count, int bits)
    {
        long total = 0;
        int sendable = 0;
        for (int i = 0; i < count; i++)
        {
            ref var candidate = ref _candidates[i];
            if (candidate.Cost < 0 || (candidate.SpawnPending && !candidate.SpawnWritten))
                continue; // no state before its spawn
            total += candidate.Cost;
            _order[sendable++] = i;
        }
        if (total <= bits)
        {
            for (int i = 0; i < sendable; i++)
                _candidates[_order[i]].Chosen = true;
            return;
        }
        Sort(observer, sendable);
        for (int i = 0; i < sendable && bits >= Snapshot.EntityBits; i++)
        {
            ref var candidate = ref _candidates[_order[i]];
            if (candidate.Cost > bits)
                continue; // a smaller one further down may still fit
            candidate.Chosen = true;
            bits -= candidate.Cost;
        }
    }

    /// <summary>Sorts the first <paramref name="length"/> indexes of the order into the priority order, ranking every candidate once.</summary>
    private void Sort(Player observer, int length)
    {
        if (!_ranked)
            Rank(observer);
        _orderComparer.Items = _candidates;
        Array.Sort(_order, 0, length, _orderComparer);
        _orderComparer.Items = null;
    }

    /// <summary>Critical or not, and the score: (ticks since the state went out + 1) x kind weight x distance weight.</summary>
    private void Rank(Player observer)
    {
        _ranked = true;
        var rules = Priority;
        uint lockTarget = observer.Inputs.LockTargetId;
        var visible = observer.Interest.Visible;
        for (int i = 0; i < visible.Count; i++)
        {
            ref var candidate = ref _candidates[i];
            if (candidate.Cost < 0)
                continue;
            var entity = candidate.Entity!;
            long age = candidate.Age;
            if (rules == null)
            {
                candidate.Score = age + 1;
                continue;
            }
            long dx = entity.XCm - (long)observer.XCm, dy = entity.YCm - (long)observer.YCm;
            long distanceSq = dx * dx + dy * dy;
            bool inAction = Snapshot.ExtraStateBits(candidate.State.State) == Snapshot.ActionStateBits;
            candidate.Critical = age >= rules.MaxStaleTicks
                || (inAction && distanceSq <= rules.TelegraphRadiusSq)
                || (lockTarget != 0 && entity.Id == lockTarget)
                || (entity is Player member && observer.CharacterId != 0
                    && Parties.SameParty(observer.CharacterId, member.CharacterId));
            int distanceWeight = distanceSq <= rules.NearRadiusSq ? 100 : rules.FarWeightPercent;
            candidate.Score = (age + 1) * rules.KindWeightPercent(entity.Kind) * distanceWeight;
        }
    }

    /// <summary>Index of the entity in the id-sorted interest list, -1 when it is not there.</summary>
    private static int IndexOf(IReadOnlyList<Entity> visible, uint id)
    {
        int low = 0, high = visible.Count - 1;
        while (low <= high)
        {
            int mid = (low + high) >> 1;
            uint midId = visible[mid].Id;
            if (midId == id)
                return mid;
            if (midId < id)
                low = mid + 1;
            else
                high = mid - 1;
        }
        return -1;
    }

    private static int StateBits(in EntitySnapshot state) => Snapshot.EntityBits + Snapshot.ExtraStateBits(state.State);

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

    private static bool TryBuildState(in SnapshotHeader header, Entity entity, long nowMs, out EntitySnapshot snapshot)
    {
        var action = ActionMachine.ToWire(entity.Actions);
        snapshot = new EntitySnapshot
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
            // Protocol 27 (spec 5.2, WP-M3-4): a harvested node shows its depleted look until it respawns.
            Flags = entity is ResourceNode { IsAvailable: false } ? EntityFlags.Depleted : EntityFlags.None,
        };
        return Snapshot.FitsOrigin(header, snapshot);
    }

    /// <summary>One entity in interest for one snapshot.</summary>
    private struct Candidate
    {
        public Entity? Entity;
        public EntitySnapshot State;
        /// <summary>Ticks since its state last went out.</summary>
        public uint Age;
        /// <summary>Bits of its state; -1 for the observer and for one that cannot be sent.</summary>
        public int Cost;
        public bool Critical;
        public long Score;
        /// <summary>Its state goes out in this snapshot.</summary>
        public bool Chosen;
        /// <summary>The client has not confirmed its spawn; <see cref="SpawnWritten"/>: some snapshot carried it (its state may go).</summary>
        public bool SpawnPending;
        public bool SpawnWritten;
        /// <summary>Its unconfirmed spawn goes out in this snapshot.</summary>
        public bool SpawnChosen;
    }

    /// <summary>Critical first, then the higher score, then the lower entity id (rule 6: a fixed order).</summary>
    private sealed class CandidateOrder : IComparer<int>
    {
        public Candidate[]? Items;

        public int Compare(int x, int y)
        {
            ref var a = ref Items![x];
            ref var b = ref Items![y];
            if (a.Critical != b.Critical)
                return a.Critical ? -1 : 1;
            if (a.Score != b.Score)
                return a.Score > b.Score ? -1 : 1;
            return a.State.EntityId.CompareTo(b.State.EntityId);
        }
    }
}
