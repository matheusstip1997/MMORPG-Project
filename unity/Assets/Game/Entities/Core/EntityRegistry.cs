#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Prediction;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>A remote entity the client knows (not the local player): what it is and its recent samples.</summary>
    public sealed class RemoteEntity
    {
        internal RemoteEntity(uint id, EntityKind kind, ushort defId, int sampleCapacity)
        {
            Id = id;
            Kind = kind;
            DefId = defId;
            Samples = new SnapshotBuffer(sampleCapacity);
        }

        public uint Id { get; }

        /// <summary><see cref="EntityKind.None"/> when the spawn event was lost and the entity was first seen in a snapshot.</summary>
        public EntityKind Kind { get; internal set; }

        /// <summary><c>ClassDef.netId</c> of a player, <c>MonsterDef.netId</c> of a monster (spec 1.7); 0 when unknown.</summary>
        public ushort DefId { get; internal set; }

        /// <summary>
        /// HP bar of the newest snapshot (spec 9.4): 0 dead to <see cref="Snapshot.TopHpBucket"/> full; see
        /// <see cref="Quantize.Bucket"/>. Use <see cref="HpFraction"/> for a bar.
        /// </summary>
        public byte HpBucket { get; internal set; } = Snapshot.TopHpBucket;

        /// <summary>The bar fill for <see cref="HpBucket"/>, 0 to 1.</summary>
        public float HpFraction => HpBucket / (float)Snapshot.TopHpBucket;

        /// <summary>
        /// Parry color of the attack the entity runs in the newest snapshot (R-3.1-05, R-3.3-04), None when uncolored or idle.
        /// TODO(I): the telegraph draws it with shape or text as well as color (spec 9.1).
        /// </summary>
        public Shared.Data.Tables.ParryColor TelegraphColor { get; internal set; }

        public SnapshotBuffer Samples { get; }

        /// <summary>Newest snapshot tick that listed this entity.</summary>
        internal uint LastSeenTick { get; set; }

        /// <summary>Set once the entity left interest: it is removed when the render time reaches this tick.</summary>
        internal uint? LeaveTick { get; set; }
    }

    /// <summary>
    /// The remote entities of the snapshots (spec 1.7, 2.3), raising <see cref="Spawned"/> and <see cref="Despawned"/>
    /// so views react to events instead of polling (spec 9.9).
    /// <list type="bullet">
    /// <item>The first entity of every snapshot is the observer itself (spec 1.7); it goes to prediction, not here.</item>
    /// <item>Every snapshot lists every entity in interest, so a lost spawn event only means the entity appears on first
    /// sight (kind <see cref="EntityKind.None"/> until a spawn names it), and an entity missing from a newer snapshot has
    /// left even if its despawn event was lost.</item>
    /// <item>An entity that left stays until the render time reaches the snapshot it was gone from, so it does not
    /// vanish before it has been drawn up to its last position.</item>
    /// <item>Only the newest snapshot changes who is in interest; an older one that arrives late only adds samples to
    /// entities already known.</item>
    /// </list>
    /// Allocates only when an entity spawns.
    /// </summary>
    public sealed class EntityRegistry
    {
        private readonly Dictionary<uint, RemoteEntity> _entities = new Dictionary<uint, RemoteEntity>();
        private readonly List<RemoteEntity> _scratch = new List<RemoteEntity>();
        private readonly int _sampleCapacity;
        private uint _snapshotTick;
        private bool _snapshotIsNewest;
        private bool _hasSnapshot;
        private uint _newestTick;
        private bool _ownSeen;
        private readonly OwnStatus[] _ownStatuses = new OwnStatus[Snapshot.MaxOwnStatuses];
        private readonly OwnBuildUp[] _ownBuildUps = new OwnBuildUp[Snapshot.MaxOwnBuildUps];

        public EntityRegistry(PredictionSettings settings)
        {
            _sampleCapacity = SnapshotBuffer.CapacityFor(settings.InterpolationDelayMs, settings.TickMs);
        }

        public event Action<RemoteEntity>? Spawned;
        public event Action<RemoteEntity>? Despawned;

        /// <summary>The entity this client controls: the first entity of the newest snapshot.</summary>
        public uint? OwnEntityId { get; private set; }

        /// <summary>
        /// Definition id of the own entity's spawn (a player's <c>ClassDef.netId</c>, spec 1.7), 0 until a spawn named it:
        /// the client's class, for its kit (backlog E) and its visual. TODO(B4): the enter-world message carries the class,
        /// since the own spawn rides one unreliable snapshot.
        /// </summary>
        public ushort OwnDefId { get; private set; }

        /// <summary>False until the newest snapshot carried the local player's exact resources (spec 9.4).</summary>
        public bool HasOwnResources { get; private set; }

        /// <summary>The local player's HP, MP and stamina with their maxima, from the newest snapshot (the resource panel).</summary>
        public OwnResources OwnResources { get; private set; }

        /// <summary>
        /// The local player's statuses for the buff row (spec 3.7, 9.3; protocol 7), from the newest snapshot, in the order
        /// they were applied: net id (<c>StatusEffectDef.netId</c>, kind and icon from <c>BuffIconRegistry</c>), stacks, and
        /// ticks left after <see cref="OwnStatusesTick"/>. TODO(I): the buff row draws them.
        /// </summary>
        public ReadOnlySpan<OwnStatus> OwnStatuses => new ReadOnlySpan<OwnStatus>(_ownStatuses, 0, OwnStatusCount);

        public int OwnStatusCount { get; private set; }

        /// <summary>Server tick of the snapshot <see cref="OwnStatuses"/> came from; a status ends at this tick plus its ticks left.</summary>
        public uint OwnStatusesTick { get; private set; }

        /// <summary>
        /// The local player's build-up meters above 0 (spec 3.8, 9.3 <c>buildup_meters</c>; protocol 9), from the newest
        /// snapshot: <c>BuildUpDef.netId</c> and whole points (100 while the build-up's effect runs). Only the own meters are
        /// ever sent. TODO(I): the HUD draws them.
        /// </summary>
        public ReadOnlySpan<OwnBuildUp> OwnBuildUps => new ReadOnlySpan<OwnBuildUp>(_ownBuildUps, 0, OwnBuildUpCount);

        public int OwnBuildUpCount { get; private set; }

        /// <summary>
        /// Gets the action state of every remote entity of each newest snapshot (backlog D2); late, reordered snapshots
        /// are not reported, so a phase never steps back. Null for none.
        /// </summary>
        public RemoteActionTracker? Actions { get; set; }

        public IReadOnlyDictionary<uint, RemoteEntity> Entities => _entities;

        /// <summary>Removes everything (disconnect), raising <see cref="Despawned"/> for each.</summary>
        public void Clear()
        {
            _scratch.Clear();
            _scratch.AddRange(_entities.Values);
            _entities.Clear();
            foreach (var entity in _scratch)
                Despawned?.Invoke(entity);
            _scratch.Clear();
            OwnEntityId = null;
            OwnDefId = 0;
            HasOwnResources = false;
            OwnResources = default;
            OwnStatusCount = 0;
            OwnBuildUpCount = 0;
            _hasSnapshot = false;
        }

        /// <summary>
        /// Reads a whole snapshot. Returns false (and changes nothing) when it does not decode. The local entity's state
        /// comes back in <paramref name="own"/> for prediction.
        /// </summary>
        public bool ApplySnapshot(ReadOnlySpan<byte> message, out SnapshotHeader header, out EntitySnapshot own, out bool hasOwn)
        {
            own = default;
            hasOwn = false;
            if (!SnapshotReader.TryBegin(message, out var reader))
            {
                header = default;
                return false;
            }
            header = reader.Header;
            // The reader is a value: a copy finds the first entity (the observer) without moving the real one, so its
            // own spawn event is not mistaken for a remote entity.
            var peek = reader;
            Begin(header.ServerTick, peek.TryReadEntity(out var first) ? first.EntityId : (uint?)null);
            if (_snapshotIsNewest)
            {
                HasOwnResources = header.HasOwnResources;
                OwnResources = header.Own;
                OwnStatusCount = 0;
                OwnStatusesTick = header.ServerTick;
                while (reader.TryReadOwnStatus(out var status))
                    _ownStatuses[OwnStatusCount++] = status;
                OwnBuildUpCount = 0;
                while (reader.TryReadOwnBuildUp(out var buildUp))
                    _ownBuildUps[OwnBuildUpCount++] = buildUp;
            }
            while (reader.TryReadDespawn(out uint despawn))
                Despawn(despawn);
            while (reader.TryReadSpawn(out var spawn))
                Spawn(spawn);
            while (reader.TryReadEntity(out var entity))
            {
                if (Entity(entity))
                {
                    own = entity;
                    hasOwn = true;
                }
            }
            End();
            return true;
        }

        /// <summary>Removes the entities whose leave tick the render time has reached.</summary>
        public void Update(double renderTick)
        {
            _scratch.Clear();
            foreach (var entity in _entities.Values)
            {
                if (entity.LeaveTick.HasValue && renderTick >= entity.LeaveTick.Value)
                    _scratch.Add(entity);
            }
            foreach (var entity in _scratch)
            {
                _entities.Remove(entity.Id);
                Despawned?.Invoke(entity);
            }
            _scratch.Clear();
        }

        private void Begin(uint tick, uint? firstEntityId)
        {
            _snapshotTick = tick;
            _snapshotIsNewest = !_hasSnapshot || (int)(tick - _newestTick) > 0;
            _ownSeen = false;
            if (!_snapshotIsNewest)
                return;
            _hasSnapshot = true;
            _newestTick = tick;
            if (firstEntityId.HasValue && firstEntityId != OwnEntityId)
            {
                OwnEntityId = firstEntityId;
                OwnDefId = 0; // a new own entity: its own spawn names it
                // It may have been known as a remote entity (a lost first snapshot); prediction owns it now.
                if (_entities.TryGetValue(firstEntityId.Value, out var stray))
                {
                    _entities.Remove(stray.Id);
                    Despawned?.Invoke(stray);
                }
            }
        }

        private void Despawn(uint id)
        {
            if (_snapshotIsNewest && _entities.TryGetValue(id, out var entity) && !entity.LeaveTick.HasValue)
                entity.LeaveTick = _snapshotTick;
        }

        private void Spawn(in SpawnEvent spawn)
        {
            if (spawn.EntityId == OwnEntityId)
            {
                OwnDefId = spawn.DefId; // the own entity goes to prediction, not to the remote entities
                return;
            }
            if (!_snapshotIsNewest)
                return;
            if (_entities.TryGetValue(spawn.EntityId, out var entity))
            {
                // Back in interest before it was removed, or a late spawn naming an entity first seen without one.
                entity.Kind = spawn.Kind;
                entity.DefId = spawn.DefId;
                entity.LeaveTick = null;
                return;
            }
            Add(spawn.EntityId, spawn.Kind, spawn.DefId);
        }

        /// <summary>True for the observer's own entity (the first one of the snapshot).</summary>
        private bool Entity(in EntitySnapshot snapshot)
        {
            if (!_ownSeen)
            {
                _ownSeen = true;
                return snapshot.EntityId == OwnEntityId;
            }

            if (!_entities.TryGetValue(snapshot.EntityId, out var entity))
            {
                if (!_snapshotIsNewest)
                    return false;
                entity = Add(snapshot.EntityId, EntityKind.None, 0);
            }
            if (_snapshotIsNewest)
            {
                entity.LastSeenTick = _snapshotTick;
                entity.LeaveTick = null;
                entity.HpBucket = snapshot.HpBucket;
                entity.TelegraphColor = snapshot.ParryColor;
                Actions?.Apply(_snapshotTick, snapshot);
            }
            entity.Samples.Add(new EntitySample
            {
                Tick = _snapshotTick, XCm = snapshot.XCm, YCm = snapshot.YCm, ZCm = snapshot.ZCm, Facing = snapshot.Facing,
            });
            return false;
        }

        private void End()
        {
            if (!_snapshotIsNewest)
                return;
            foreach (var entity in _entities.Values)
            {
                if (entity.LastSeenTick != _snapshotTick && !entity.LeaveTick.HasValue)
                    entity.LeaveTick = _snapshotTick;
            }
        }

        private RemoteEntity Add(uint id, EntityKind kind, ushort defId)
        {
            var entity = new RemoteEntity(id, kind, defId, _sampleCapacity) { LastSeenTick = _snapshotTick };
            _entities.Add(id, entity);
            Spawned?.Invoke(entity);
            return entity;
        }
    }
}
