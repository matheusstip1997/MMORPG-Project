#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Net;
using Shared.Data;
using Shared.Protocol;
using Shared.World;
using UnityEngine.SceneManagement;

namespace Game.Dev.Qa
{
    /// <summary>A hit the server reported, with the tick it happened in.</summary>
    public struct QaHit
    {
        public uint Tick;
        public HitEntry Entry;
    }

    /// <summary>A projectile seen by the tap: its def, when it spawned and how it ended.</summary>
    public sealed class QaProjectile
    {
        public uint Id;
        public string DefId = "";
        public uint SeenAtTick;
        public uint? DespawnTick;
        public ProjectileDespawnReason Reason;
    }

    /// <summary>A ground area seen by the tap (protocol 9): its effect, when it spawned and how it ended.</summary>
    public sealed class QaGroundArea
    {
        public uint Id;
        public string EffectId = "";
        public uint SeenAtTick;
        public uint? DespawnTick;
        public GroundAreaDespawnReason Reason;
    }

    /// <summary>An entity entering or leaving this client's interest (or dying), with the snapshot tick.</summary>
    public struct QaEntityEvent
    {
        public uint Tick;
        public uint EntityId;
        public EntityKind Kind;
        public ushort DefId;
        public bool Spawned;
        public int XCm;
        public int YCm;
        public byte HpBucket;
    }

    /// <summary>
    /// Read-only observer of the world connection for the QA run. When the world scene loads (after its <c>Awake</c>,
    /// before <c>PredictionDevClient.Start</c> takes the connection), it takes the login's <see cref="WorldEntry"/> and
    /// offers a new one whose relay it feeds: connection → login relay → this tap → new relay → the world's client.
    /// Every message is forwarded unchanged and also decoded into the tap's own <see cref="EntityRegistry"/> and
    /// <see cref="ProjectileTracker"/> and <see cref="GroundAreaTracker"/>, so the scenario sees what the server sent (HP
    /// buckets, positions, hits, projectile and ground area despawn reasons, own build-up meters) without reading the
    /// game's private state. Main thread only.
    /// </summary>
    public sealed class QaWorldTap : IClientConnectionListener, IDisposable
    {
        private readonly string _worldScene;
        private readonly ClientConnectionRelay _downstream = new ClientConnectionRelay();
        private readonly HitEntry[] _hitBuffer = new HitEntry[HitMessage.MaxHits];
        private readonly ProjectileTracker _projectileTracker;
        private readonly Dictionary<uint, QaProjectile> _projectileById = new Dictionary<uint, QaProjectile>();
        private readonly GroundAreaTracker _groundAreaTracker;
        private readonly Dictionary<uint, QaGroundArea> _groundAreaById = new Dictionary<uint, QaGroundArea>();
        private uint _snapshotTick;

        public QaWorldTap(ConfigSet config, WorldGrid? grid, string worldScene)
        {
            _worldScene = worldScene;
            var settings = PredictionSettings.From(config);
            TickMs = settings.TickMs;
            Registry = new EntityRegistry(settings);
            Registry.Spawned += entity => Events.Add(EventOf(entity, true));
            Registry.Despawned += entity => Events.Add(EventOf(entity, false));
            _projectileTracker = ProjectileTracker.From(config, settings.TickRateHz, grid);
            _projectileTracker.Spawned += OnProjectileSpawned;
            _groundAreaTracker = GroundAreaTracker.From(config);
            _groundAreaTracker.Spawned += OnGroundAreaSpawned;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>True once the world connection runs through the tap.</summary>
        public bool Attached { get; private set; }

        public double TickMs { get; }

        public EntityRegistry Registry { get; }

        /// <summary>Newest server tick of a snapshot.</summary>
        public uint ServerTick { get; private set; }

        public bool HasOwn { get; private set; }

        /// <summary>The local player's server position on the ground plane, in cm.</summary>
        public int OwnXCm { get; private set; }
        public int OwnYCm { get; private set; }

        public List<QaHit> Hits { get; } = new List<QaHit>();

        public List<QaProjectile> Projectiles { get; } = new List<QaProjectile>();

        public List<QaGroundArea> GroundAreas { get; } = new List<QaGroundArea>();

        public List<QaEntityEvent> Events { get; } = new List<QaEntityEvent>();

        /// <summary>Refused actions and disconnects, as text for the step logs.</summary>
        public List<string> Notes { get; } = new List<string>();

        /// <summary>The newest bag the server sent (backlog G: as saved); null before the first one.</summary>
        public InventoryContents? Inventory { get; private set; }

        /// <summary>Pickup answers received (backlog G), oldest first.</summary>
        public List<PickupResult> Pickups { get; } = new List<PickupResult>();

        public bool Disconnected { get; private set; }

        /// <summary>The newest ground position of a remote entity in cm; false when it has no sample yet.</summary>
        public static bool TryGroundCm(RemoteEntity entity, out int xCm, out int yCm)
        {
            xCm = 0;
            yCm = 0;
            if (entity.Samples.Count == 0 || !entity.Samples.TrySample(entity.Samples.NewestTick, out var pose))
                return false;
            xCm = (int)Math.Round(pose.XCm);
            yCm = (int)Math.Round(pose.ZCm); // snapshot Z is the ground Y (SnapshotAxes)
            return true;
        }

        public void Dispose() => SceneManager.sceneLoaded -= OnSceneLoaded;

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (Attached || scene.path != _worldScene || !WorldHandoff.TryTake(out var entry) || entry == null)
                return;
            entry.Relay.Target = this;
            WorldHandoff.Offer(new WorldEntry(entry.Connection, _downstream, entry.RealmId, entry.CharacterId));
            Attached = true;
        }

        public void OnConnected() => _downstream.OnConnected();

        public void OnDisconnected(ClientDisconnectReason reason, ConnectRejectReason rejectReason, ushort serverProtocolVersion)
        {
            Disconnected = true;
            Notes.Add($"disconnected: {reason} {rejectReason}");
            _downstream.OnDisconnected(reason, rejectReason, serverProtocolVersion);
        }

        public void OnReliableReceived(ReadOnlySpan<byte> message)
        {
            if (ActionResult.TryRead(message, out ushort sequence, out ushort actionId, out byte slot, out ActionReason reason))
                Notes.Add($"tick {ServerTick}: action {actionId} (slot {slot}) of input {sequence} refused: {reason}");
            else if (ReliableMessages.TryRead(message, out InventoryContents contents))
                Inventory = contents;
            else if (ReliableMessages.TryRead(message, out PickupResult pickup))
            {
                Pickups.Add(pickup);
                Notes.Add($"tick {ServerTick}: pickup of drop {pickup.DropEntityId}: {pickup.Reason}, took {pickup.Taken}");
            }
            _downstream.OnReliableReceived(message);
        }

        public void OnUnreliableReceived(ReadOnlySpan<byte> message)
        {
            Observe(message);
            _downstream.OnUnreliableReceived(message);
        }

        private void Observe(ReadOnlySpan<byte> message)
        {
            if (!HotMessage.TryPeekId(message, out var id))
                return;
            if (id == HotMessageId.ProjectileSpawn || id == HotMessageId.ProjectileDespawn)
            {
                _projectileTracker.Apply(message);
                foreach (var projectile in _projectileTracker.Projectiles.Values)
                {
                    if (_projectileById.TryGetValue(projectile.Id, out var seen) && projectile.Reason != ProjectileDespawnReason.None)
                    {
                        seen.Reason = projectile.Reason;
                        seen.DespawnTick = projectile.DespawnTick;
                    }
                }
                return;
            }
            if (id == HotMessageId.GroundAreaSpawn || id == HotMessageId.GroundAreaDespawn)
            {
                _groundAreaTracker.Apply(message);
                foreach (var area in _groundAreaTracker.Areas.Values)
                {
                    if (_groundAreaById.TryGetValue(area.Id, out var seen) && area.Reason != GroundAreaDespawnReason.None)
                    {
                        seen.Reason = area.Reason;
                        seen.DespawnTick = area.DespawnTick;
                    }
                }
                return;
            }
            if (id == HotMessageId.Hit)
            {
                if (HitMessage.TryRead(message, out uint tick, _hitBuffer, out int count))
                {
                    for (int i = 0; i < count; i++)
                        Hits.Add(new QaHit { Tick = tick, Entry = _hitBuffer[i] });
                }
                return;
            }
            int firstEvent = Events.Count;
            if (!Registry.ApplySnapshot(message, out var header, out var own, out bool hasOwn))
                return;
            if ((int)(header.ServerTick - ServerTick) <= 0 && ServerTick != 0)
                return; // a late snapshot only adds samples
            ServerTick = header.ServerTick;
            _snapshotTick = header.ServerTick;
            // Spawns are raised before the snapshot's samples are read: stamp them with this tick and their position now.
            for (int i = firstEvent; i < Events.Count; i++)
            {
                var spawn = Events[i];
                spawn.Tick = _snapshotTick;
                if (Registry.Entities.TryGetValue(spawn.EntityId, out var entity) && TryGroundCm(entity, out int x, out int y))
                {
                    spawn.XCm = x;
                    spawn.YCm = y;
                    spawn.HpBucket = entity.HpBucket;
                }
                Events[i] = spawn;
            }
            // Leavers go at once: the tap draws nothing, so it does not wait for an interpolation delay.
            Registry.Update(header.ServerTick);
            _projectileTracker.Update(header.ServerTick);
            _groundAreaTracker.Update(header.ServerTick);
            if (hasOwn)
            {
                HasOwn = true;
                OwnXCm = own.XCm;
                OwnYCm = own.ZCm;
            }
        }

        private void OnProjectileSpawned(ClientProjectile projectile)
        {
            var seen = new QaProjectile { Id = projectile.Id, DefId = projectile.Def.Id, SeenAtTick = ServerTick };
            _projectileById[projectile.Id] = seen;
            Projectiles.Add(seen);
        }

        private void OnGroundAreaSpawned(ClientGroundArea area)
        {
            var seen = new QaGroundArea { Id = area.Id, EffectId = area.Effect.Id, SeenAtTick = ServerTick };
            _groundAreaById[area.Id] = seen;
            GroundAreas.Add(seen);
        }

        private QaEntityEvent EventOf(RemoteEntity entity, bool spawned)
        {
            TryGroundCm(entity, out int x, out int y);
            return new QaEntityEvent
            {
                Tick = _snapshotTick, EntityId = entity.Id, Kind = entity.Kind, DefId = entity.DefId, Spawned = spawned,
                XCm = x, YCm = y, HpBucket = entity.HpBucket,
            };
        }
    }
}
