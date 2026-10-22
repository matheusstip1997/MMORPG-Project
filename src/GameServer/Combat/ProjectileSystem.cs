using GameServer.Entities;
using GameServer.Events;
using GameServer.World;
using GameServer.World.Map;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Combat;

namespace GameServer.Combat;

/// <summary>
/// Tick phase 4 (spec 1.6, 3.2, backlog D3): projectiles fly straight along their owner's locked facing, sweep each tick's
/// segment against the world grid and nearby combatants, and resolve every hit through the shared
/// <see cref="IHitTargetResolver"/> (R-3.2-02: the normal rules; this system never computes damage). The same system for
/// players and monsters (R-3.2-01); it only knows the owner as an <see cref="Entity"/>.
/// <para>
/// Order (rule 6): projectiles in id order; on one segment, targets by where the segment first touches them, ties by
/// entity id (<see cref="SweptCircle.Hits"/>). Per target: the resolver's <c>CanHit</c>, then <c>Resolve</c> with
/// <see cref="HitSource.Projectile"/> and <c>100 - k * falloffPercentPerHit</c> percent for hit k.
/// Damaged, Killed and Parried count toward <c>maxHits</c> (a parried projectile is blocked); Dodged does not, so it flies
/// on through a dodge; neither Dodged nor a counted target is tested again (no double hits); Ignored changes nothing.
/// </para>
/// <para>
/// A projectile ends, first that applies: <c>Hit</c> at maxHits, <c>World</c> at the wall its path was cut at
/// (<see cref="ProjectilePath.StoppedByWorld"/>), <c>Range</c> at its range, and <c>OwnerGone</c> when its owner leaves the
/// world (<see cref="OwnerRemoved"/>; a dead owner still in the world keeps its projectiles flying). Projectiles are
/// pooled (<c>ProjectileSettings.maxActive</c>) and capped per owner (<c>maxPerOwner</c>); a spawn over either limit is
/// refused and counted. Targets are not rewound (R-3.1-03 lag compensation is for strikes; spec 3.2 asks for none): the
/// projectile is simulated on the server over time. Allocation-free once its lists have grown.
/// </para>
/// </summary>
internal sealed class ProjectileSystem : IActionEffectSink
{
    private readonly ProjectileRules _rules;
    private readonly WorldMap _map;
    private readonly SpatialGrid _grid;
    private readonly EventBus? _events;
    private readonly int _maxBodyRadiusCm;
    private readonly Stack<Projectile> _pool;
    private readonly List<Projectile> _active;
    private readonly List<Entity> _candidates = new();
    private readonly List<Candidate> _hits = new();
    private uint _nextId = 1;

    /// <param name="resolver">
    /// The world's per-target hit pipeline (WP4's <c>HitResolver</c>); null in a world without combat rules, where nothing
    /// has a combatant to hit.
    /// </param>
    /// <param name="maxBodyRadiusCm">
    /// The largest <c>Combatant.BodyRadiusCm</c> (<c>CombatRules.MaxBodyRadiusCm</c>): how far around a segment the grid is
    /// searched. Each target is then swept with its own body radius plus the projectile's hit radius.
    /// </param>
    public ProjectileSystem(ProjectileRules rules, WorldMap map, SpatialGrid grid, EventBus? events,
        IHitTargetResolver? resolver, int maxBodyRadiusCm)
    {
        if (maxBodyRadiusCm < 0 || FixedPoint.FromCm(maxBodyRadiusCm + 2_000) > SweptCircle.MaxRaw)
            throw new ArgumentOutOfRangeException(nameof(maxBodyRadiusCm), "with the largest hit radius it must fit SweptCircle.MaxRaw");
        _rules = rules;
        _map = map;
        _grid = grid;
        _events = events;
        Resolver = resolver;
        _maxBodyRadiusCm = maxBodyRadiusCm;
        int capacity = rules.Settings.MaxActive;
        _pool = new Stack<Projectile>(capacity);
        _active = new List<Projectile>(capacity);
        for (int i = 0; i < capacity; i++)
            _pool.Push(new Projectile());
    }

    /// <summary>Where every hit goes (R-3.2-02); null when nothing can be hit.</summary>
    public IHitTargetResolver? Resolver { get; }

    public ProjectileRules Rules => _rules;

    /// <summary>Live and ending projectiles in id order (ending ones still owe their despawn events).</summary>
    public IReadOnlyList<Projectile> Active => _active;

    public int LiveCount
    {
        get
        {
            int count = 0;
            foreach (var projectile in _active)
            {
                if (projectile.IsLive)
                    count++;
            }
            return count;
        }
    }

    /// <summary>Projectiles spawned since the world started.</summary>
    public long Spawned { get; private set; }

    /// <summary>Spawns refused because the owner had <c>maxPerOwner</c> live projectiles.</summary>
    public long RefusedOwnerCap { get; private set; }

    /// <summary>Spawns refused because the pool was empty (<c>maxActive</c>).</summary>
    public long RefusedPoolFull { get; private set; }

    /// <summary>Projectile effects naming no loaded ProjectileDef (a bug: the validator refuses them).</summary>
    public long UnknownDefs { get; private set; }

    /// <summary>
    /// Phase 3: a <c>Projectile</c> effect an action fired (R-3.1-07) spawns its projectile along the facing locked at the
    /// action's start; the world sets this system as its <c>EffectRouter.Projectiles</c>.
    /// </summary>
    public void OnEffect(Entity entity, in FiredEffect effect, uint tick)
    {
        if (effect.Kind == EffectKind.Projectile)
            Spawn(entity, effect.Effect.Def, entity.Actions.Facing, tick);
    }

    /// <summary>
    /// Phase 3, from the effect a running action fired: a projectile from the owner's position (whole cm, so the spawn
    /// message carries it exactly) along the facing locked at the action's start. Null when refused.
    /// </summary>
    public Projectile? Spawn(Entity owner, ActionEffectDef effect, ushort lockedFacing, uint tick)
    {
        if (!_rules.TryGet(effect.ProjectileId, out var def))
        {
            UnknownDefs++;
            return null;
        }
        if (CountLive(owner) >= _rules.Settings.MaxPerOwner)
        {
            RefusedOwnerCap++;
            return null;
        }
        if (_pool.Count == 0)
        {
            RefusedPoolFull++;
            return null;
        }

        // Origin on whole cm and facing at the wire resolution (the 1024 steps FixedTrig uses), so the spawn message
        // carries exactly the path simulated here.
        var origin = FixVec2.FromCm(owner.XCm, owner.YCm);
        ushort facing = Quantize.RoundTripAngle(lockedFacing, ProjectileMessages.FacingBits);
        var path = ProjectilePath.Create(origin, facing, def.SpeedCmPerSec, def.RangeCm, tick, _rules.TickRateHz,
            def.StopsOnWorld ? _map.Grid : null);
        var projectile = _pool.Pop();
        projectile.Start(_nextId++, owner, def, effect, path, owner.HeightCm);
        _active.Add(projectile); // ids only grow, so the list stays in id order
        Spawned++;
        _events?.Publish(new ProjectileSpawned(projectile.Id, owner.Id, def.NetId, tick), CharacterIdOf(owner));
        return projectile;
    }

    /// <summary>Phase 4: every live projectile takes this tick's step, in id order.</summary>
    public void Step(uint tick)
    {
        for (int i = 0; i < _active.Count; i++)
        {
            var projectile = _active[i];
            if (projectile.IsLive)
                Fly(projectile, tick);
        }
    }

    /// <summary>The owner left the world (removed in phase 1 or 8): its live projectiles end with <c>OwnerGone</c>.</summary>
    public void OwnerRemoved(Entity owner, uint tick)
    {
        foreach (var projectile in _active)
        {
            if (projectile.IsLive && projectile.Owner == owner)
                projectile.End(ProjectileDespawnReason.OwnerGone, tick);
        }
    }

    /// <summary>
    /// After phase 10: projectiles that ended at least <c>eventRepeatTicks</c> ticks ago (so their despawn went out that
    /// many times) go back to the pool.
    /// </summary>
    public void ReleaseEnded(uint tick)
    {
        int repeats = _rules.Settings.EventRepeatTicks;
        for (int i = 0; i < _active.Count; i++)
        {
            var projectile = _active[i];
            if (projectile.IsLive || tick - projectile.EndTick + 1 < (uint)repeats)
                continue;
            _active.RemoveAt(i--); // keeps the id order
            projectile.Release();
            _pool.Push(projectile);
        }
    }

    private void Fly(Projectile projectile, uint tick)
    {
        var path = projectile.Path;
        int steps = path.StepsAfter(tick);
        var start = path.PositionAt(steps - 1);
        var end = path.PositionAt(steps);
        projectile.SegmentStart = start;
        projectile.Position = end;

        if (Sweep(projectile, start, end, tick))
        {
            projectile.End(ProjectileDespawnReason.Hit, tick);
            return;
        }
        if (steps >= path.FlightSteps)
            projectile.End(path.StoppedByWorld ? ProjectileDespawnReason.World : ProjectileDespawnReason.Range, tick);
    }

    /// <summary>Resolves the targets the segment touches, in order; true when the projectile reached its maxHits.</summary>
    private bool Sweep(Projectile projectile, FixVec2 start, FixVec2 end, uint tick)
    {
        var resolver = Resolver;
        if (resolver == null)
            return false;
        var def = projectile.Def;
        var owner = projectile.Owner;
        int hitRadiusRaw = FixedPoint.FromCm(def.HitRadiusCm);
        int radiusCm = def.HitRadiusCm + _maxBodyRadiusCm;
        int minX = FixedPoint.ToCmFloor(Math.Min(start.X, end.X)) - radiusCm - 1;
        int maxX = FixedPoint.ToCmFloor(Math.Max(start.X, end.X)) + radiusCm + 1;
        int minY = FixedPoint.ToCmFloor(Math.Min(start.Y, end.Y)) - radiusCm - 1;
        int maxY = FixedPoint.ToCmFloor(Math.Max(start.Y, end.Y)) + radiusCm + 1;

        _candidates.Clear();
        _grid.QueryCells(minX, minY, maxX, maxY, _candidates);
        _hits.Clear();
        foreach (var target in _candidates)
        {
            if (target == owner || target.Combatant is not { IsAlive: true } combatant || projectile.HasHit(target.Id))
                continue;
            if (SweptCircle.Hits(start, end, target.Movement.Position, hitRadiusRaw + combatant.BodyRadiusRaw, out int entry))
                InsertInOrder(new Candidate(entry, target));
        }

        for (int i = 0; i < _hits.Count; i++)
        {
            var target = _hits[i].Target;
            if (!resolver.CanHit(owner, target))
                continue;
            var request = new HitRequest(owner, target, projectile.Effect, HitSource.Projectile, tick,
                def.DamagePercentOfHit(projectile.Hits));
            var outcome = resolver.Resolve(request);
            switch (outcome)
            {
                case HitOutcome.Damaged:
                case HitOutcome.Killed:
                case HitOutcome.Parried:
                    projectile.HitTargets.Add(target.Id);
                    projectile.Hits++;
                    _events?.Publish(new ProjectileHit(projectile.Id, owner.Id, target.Id, def.NetId), CharacterIdOf(owner));
                    if (projectile.Hits >= def.MaxHits)
                        return true;
                    break;
                case HitOutcome.Dodged:
                    projectile.HitTargets.Add(target.Id);
                    break;
            }
        }
        return false;
    }

    /// <summary>Keeps <see cref="_hits"/> sorted by entry distance, then entity id (insertion sort: a handful per segment).</summary>
    private void InsertInOrder(Candidate candidate)
    {
        int index = _hits.Count;
        _hits.Add(candidate);
        while (index > 0 && candidate.IsBefore(_hits[index - 1]))
        {
            _hits[index] = _hits[index - 1];
            index--;
        }
        _hits[index] = candidate;
    }

    private int CountLive(Entity owner)
    {
        int count = 0;
        foreach (var projectile in _active)
        {
            if (projectile.IsLive && projectile.Owner == owner)
                count++;
        }
        return count;
    }

    private static long CharacterIdOf(Entity entity) => entity is Player player ? player.CharacterId : 0;

    private readonly struct Candidate
    {
        public Candidate(int entryRaw, Entity target)
        {
            EntryRaw = entryRaw;
            Target = target;
        }

        public int EntryRaw { get; }

        public Entity Target { get; }

        public bool IsBefore(in Candidate other) =>
            EntryRaw < other.EntryRaw || (EntryRaw == other.EntryRaw && Target.Id < other.Target.Id);
    }
}
