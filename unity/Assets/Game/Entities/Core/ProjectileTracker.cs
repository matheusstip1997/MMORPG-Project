#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Combat;
using Shared.World;

namespace Game.Gameplay.Entities
{
    /// <summary>A projectile the client draws (spec 3.2): the server's path rebuilt from its spawn message.</summary>
    public sealed class ClientProjectile
    {
        internal ClientProjectile(uint id, ProjectileDef def, ProjectilePath path, int heightCm)
        {
            Id = id;
            Def = def;
            Path = path;
            HeightCm = heightCm;
        }

        public uint Id { get; }

        public ProjectileDef Def { get; }

        /// <summary>The same path the server simulates (shared <see cref="ProjectilePath"/>, stopped by the client's grid).</summary>
        public ProjectilePath Path { get; }

        /// <summary>Height of the origin in cm (the shooter's height at the spawn).</summary>
        public int HeightCm { get; }

        /// <summary>The tick the server ended it in, once its despawn arrived.</summary>
        public uint? DespawnTick { get; internal set; }

        public ProjectileDespawnReason Reason { get; internal set; }

        /// <summary>
        /// Where it is at <paramref name="renderTick"/> (server ticks, fractional), in ground cm: after phase 4 of tick T it
        /// is at <c>Path.PositionAt(T - StartTick + 1)</c>, blended between ticks, and it stays at its last position once
        /// its despawn tick or path end is reached. A spawn that arrives late is simply drawn from where it is now.
        /// Presentation only (floats are fine).
        /// </summary>
        public void GetPose(double renderTick, out float groundXCm, out float groundYCm)
        {
            double steps = renderTick - Path.StartTick + 1;
            double last = Path.FlightSteps;
            if (DespawnTick is uint end)
                last = Math.Min(last, Path.StepsAfter(end));
            steps = Math.Max(0, Math.Min(steps, last));
            int whole = (int)Math.Floor(steps);
            float fraction = (float)(steps - whole);
            var from = Path.PositionAt(whole);
            var to = Path.PositionAt(whole + 1);
            const float rawPerCm = FixedPoint.RawPerCm;
            groundXCm = (from.X + (to.X - from.X) * fraction) / rawPerCm;
            groundYCm = (from.Y + (to.Y - from.Y) * fraction) / rawPerCm;
        }

        /// <summary>The render tick at which it is gone: its despawn tick, or the end of its path if no despawn arrived.</summary>
        public double EndTick => DespawnTick is uint end ? Math.Min(end, Path.LastTick) + 1.0 : Path.LastTick + 1.0;
    }

    /// <summary>
    /// The projectiles of the hot messages <see cref="HotMessageId.ProjectileSpawn"/> and
    /// <see cref="HotMessageId.ProjectileDespawn"/> (spec 3.2), raising <see cref="Spawned"/> and <see cref="Despawned"/>
    /// so views react to events (spec 9.9).
    /// <list type="bullet">
    /// <item>The path is simulated deterministically from the start tick with the shared <see cref="ProjectilePath"/>; a
    /// projectile that stops on the world is cast against the client's own copy of the world grid, so it stops at the same
    /// raw point as on the server even when its despawn is lost.</item>
    /// <item>The server repeats every event a few ticks against loss: duplicates are dropped by projectile id, and a spawn
    /// for an id that already ended (a late repeat) is ignored.</item>
    /// <item>A despawn is applied when the render time reaches its tick, so the projectile is drawn up to where it ended.</item>
    /// </list>
    /// Allocates only when a projectile spawns.
    /// </summary>
    public sealed class ProjectileTracker
    {
        /// <summary>Ended ids remembered so late repeats of their spawn are ignored.</summary>
        public const int RecentCapacity = 1024;

        private readonly Dictionary<ushort, ProjectileDef> _defs = new Dictionary<ushort, ProjectileDef>();
        private readonly Dictionary<uint, ClientProjectile> _projectiles = new Dictionary<uint, ClientProjectile>();
        private readonly HashSet<uint> _ended = new HashSet<uint>();
        private readonly uint[] _endedRing = new uint[RecentCapacity];
        private readonly List<ClientProjectile> _scratch = new List<ClientProjectile>();
        private readonly int _tickRateHz;
        private readonly WorldGrid? _grid;
        private int _endedNext;

        /// <param name="grid">The world grid for projectiles that stop on the world; null draws them to their range.</param>
        public ProjectileTracker(IEnumerable<ProjectileDef> defs, int tickRateHz, WorldGrid? grid)
        {
            foreach (var def in defs)
                _defs[(ushort)def.NetId] = def;
            _tickRateHz = tickRateHz;
            _grid = grid;
        }

        /// <summary>The ProjectileDef rows of validated tables.</summary>
        public static ProjectileTracker From(ConfigSet set, int tickRateHz, WorldGrid? grid)
        {
            var defs = new List<ProjectileDef>();
            if (set.TryGet(typeof(ProjectileDef), out var table))
            {
                foreach (var row in table.Rows)
                    defs.Add((ProjectileDef)row);
            }
            return new ProjectileTracker(defs, tickRateHz, grid);
        }

        public event Action<ClientProjectile>? Spawned;
        public event Action<ClientProjectile>? Despawned;

        public IReadOnlyDictionary<uint, ClientProjectile> Projectiles => _projectiles;

        /// <summary>Spawns naming a <c>ProjectileDef.netId</c> this client does not have (tables out of date).</summary>
        public long UnknownDefs { get; private set; }

        /// <summary>Reads a projectile hot message; false (and nothing changes) when it is not one or does not decode.</summary>
        public bool Apply(ReadOnlySpan<byte> message)
        {
            if (ProjectileSpawnReader.TryBegin(message, out var spawns))
            {
                while (spawns.TryRead(out var spawn))
                    OnSpawn(spawn);
                return true;
            }
            if (ProjectileDespawnReader.TryBegin(message, out var despawns))
            {
                while (despawns.TryRead(out var despawn))
                    OnDespawn(despawn);
                return true;
            }
            return false;
        }

        /// <summary>Removes the projectiles the render time has passed the end of (their despawn tick or path end).</summary>
        public void Update(double renderTick)
        {
            _scratch.Clear();
            foreach (var projectile in _projectiles.Values)
            {
                if (renderTick >= projectile.EndTick)
                    _scratch.Add(projectile);
            }
            foreach (var projectile in _scratch)
                Remove(projectile);
            _scratch.Clear();
        }

        /// <summary>Removes everything (disconnect), raising <see cref="Despawned"/> for each.</summary>
        public void Clear()
        {
            _scratch.Clear();
            _scratch.AddRange(_projectiles.Values);
            _projectiles.Clear();
            foreach (var projectile in _scratch)
                Despawned?.Invoke(projectile);
            _scratch.Clear();
            _ended.Clear();
            Array.Clear(_endedRing, 0, _endedRing.Length);
            _endedNext = 0;
        }

        private void OnSpawn(in ProjectileSpawn spawn)
        {
            if (_projectiles.ContainsKey(spawn.ProjectileId) || _ended.Contains(spawn.ProjectileId))
                return;
            if (!_defs.TryGetValue(spawn.DefId, out var def))
            {
                UnknownDefs++;
                return;
            }
            var path = ProjectilePath.Create(FixVec2.FromCm(spawn.XCm, spawn.YCm), spawn.Facing, spawn.SpeedCmPerSec,
                def.RangeCm, spawn.StartTick, _tickRateHz, def.StopsOnWorld ? _grid : null);
            var projectile = new ClientProjectile(spawn.ProjectileId, def, path, spawn.HeightCm);
            _projectiles.Add(projectile.Id, projectile);
            Spawned?.Invoke(projectile);
        }

        private void OnDespawn(in ProjectileDespawn despawn)
        {
            if (_projectiles.TryGetValue(despawn.ProjectileId, out var projectile) && projectile.DespawnTick == null)
            {
                projectile.DespawnTick = despawn.Tick;
                projectile.Reason = despawn.Reason;
            }
        }

        private void Remove(ClientProjectile projectile)
        {
            _projectiles.Remove(projectile.Id);
            if (_ended.Count == RecentCapacity)
                _ended.Remove(_endedRing[_endedNext]);
            _endedRing[_endedNext] = projectile.Id;
            _endedNext = (_endedNext + 1) % RecentCapacity;
            _ended.Add(projectile.Id);
            Despawned?.Invoke(projectile);
        }
    }
}
