using GameServer.Entities;
using GameServer.Events;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;

namespace GameServer.Combat;

/// <summary>
/// Ground areas (spec 3.1 <c>GroundArea</c> effects, R-3.1-07 "spawns an area on the ground with a duration and a tick
/// rate", backlog D6): placed in phase 3 when the effect fires, they tick in phase 5, after the strikes and before the due
/// pending hits. The same system for players and monsters.
/// <para>
/// Placing: the effect's hit shapes at the owner's position on whole cm and its locked facing at the wire's resolution (so
/// clients draw the same shapes); the per-tick heal (<c>healPercent</c> of the owner's Matk) is read then. Over
/// <c>GroundAreaSettings.maxPerOwner</c> live areas, or with the pool empty (<c>maxActive</c>), it is refused and counted.
/// </para>
/// <para>
/// Ticks: on the tick it is placed, then every <c>tickIntervalMs</c> (rounded up to ticks) while inside its
/// <c>durationMs</c> (rounded up), so a duration that is not a multiple of the interval has no partial last tick. Each
/// tick, the entities whose body circle touches a shape, where they stand (no rewind: the area persists and clients see
/// it), in entity-id order: an enemy (<c>enemies</c>, <see cref="IHitTargetResolver.CanHit"/>) is hit through the shared
/// pipeline with <see cref="HitSource.GroundArea"/> (damage, poise and build-ups; never a flinch, R-3.3-09; dodge i-frames
/// avoid it, a parry does not, user decision 2026-10-24; a monster's area on a player is a pending hit); an ally
/// (<c>allies</c>: same faction, not the owner) or the owner itself (<c>self</c>, when inside) is healed. Areas run in id order.
/// </para>
/// <para>
/// End (user decision 2026-10-24): <c>Expired</c> in phase 5 of the tick after its span, <c>OwnerDied</c> when its owner is
/// dead at its next phase 5, <c>OwnerGone</c> when its owner leaves the world (<see cref="OwnerRemoved"/>). Ended areas stay
/// <c>eventRepeatTicks</c> ticks so their despawn reaches every player that got their spawn (<see cref="ReleaseEnded"/>).
/// TODO(open): areas that follow their owner (a Flamethrower that moves with the Mage), and location-targeted areas
/// (R-3.1-07, the cursor point). Allocation-free once its lists have grown. World simulation thread only.
/// </para>
/// </summary>
internal sealed class GroundAreaSystem : IActionEffectSink
{
    private static readonly Comparison<Entity> ById = (a, b) => a.Id.CompareTo(b.Id);

    private readonly CombatRules _rules;
    private readonly GroundAreaSettings _settings;
    private readonly SpatialGrid _grid;
    private readonly IHitTargetResolver _resolver;
    private readonly StatusSystem _statuses;
    private readonly EventBus? _events;
    private readonly Stack<GroundArea> _pool;
    private readonly List<GroundArea> _active;
    private readonly List<Entity> _candidates = new(32);
    private uint _nextId = 1;

    public GroundAreaSystem(CombatRules rules, GroundAreaSettings settings, SpatialGrid grid, IHitTargetResolver resolver,
        StatusSystem statuses, EventBus? events)
    {
        _rules = rules;
        _settings = settings;
        _grid = grid;
        _resolver = resolver;
        _statuses = statuses;
        _events = events;
        _pool = new Stack<GroundArea>(settings.MaxActive);
        _active = new List<GroundArea>(settings.MaxActive);
        for (int i = 0; i < settings.MaxActive; i++)
            _pool.Push(new GroundArea());
    }

    public GroundAreaSettings Settings => _settings;

    /// <summary>Live and ending areas in id order (ending ones still owe their despawn events).</summary>
    public IReadOnlyList<GroundArea> Active => _active;

    public int LiveCount
    {
        get
        {
            int count = 0;
            foreach (var area in _active)
            {
                if (area.IsLive)
                    count++;
            }
            return count;
        }
    }

    /// <summary>Areas placed since the world started.</summary>
    public long Spawned { get; private set; }

    /// <summary>Areas refused because the owner had <c>maxPerOwner</c> live areas.</summary>
    public long RefusedOwnerCap { get; private set; }

    /// <summary>Areas refused because the pool was empty (<c>maxActive</c>).</summary>
    public long RefusedPoolFull { get; private set; }

    /// <summary>GroundArea effects without hit shapes (a data bug the validator refuses).</summary>
    public long AreasWithoutShapes { get; private set; }

    /// <summary>Phase 3: a <c>GroundArea</c> effect a running action fired; the world sets this system as <c>EffectRouter.GroundAreas</c>.</summary>
    public void OnEffect(Entity entity, in FiredEffect effect, uint tick)
    {
        if (effect.Kind == EffectKind.GroundArea)
            Spawn(entity, effect.Effect.Def, effect.Action.NetId, effect.EffectIndex, entity.Actions.Facing, tick);
    }

    /// <summary>Places an area where <paramref name="owner"/> stands, along <paramref name="lockedFacing"/>. Null when refused.</summary>
    public GroundArea? Spawn(Entity owner, ActionEffectDef effect, int actionNetId, int effectIndex, ushort lockedFacing, uint tick)
    {
        if (owner.Combatant is not { IsAlive: true } caster)
            return null;
        if (!_rules.Shapes.TryGet(effect.Id, out var shapes))
        {
            AreasWithoutShapes++;
            return null;
        }
        if (CountLive(owner) >= _settings.MaxPerOwner)
        {
            RefusedOwnerCap++;
            return null;
        }
        if (_pool.Count == 0)
        {
            RefusedPoolFull++;
            return null;
        }
        var origin = FixVec2.FromCm(owner.XCm, owner.YCm);
        ushort facing = Quantize.RoundTripAngle(lockedFacing, GroundAreaMessages.FacingBits);
        int heal = effect.HealsPerTick() ? StatusSystem.HealAmount(caster.Stats.Get(_rules.Config.Ids.Matk), effect.HealPercent) : 0;
        var area = _pool.Pop();
        area.Start(_nextId++, owner, effect, shapes, actionNetId, effectIndex, origin, facing, owner.HeightCm, tick,
            Math.Max(1, _rules.TicksFor(effect.DurationMs)), Math.Max(1, _rules.TicksFor(effect.TickIntervalMs)), heal);
        _active.Add(area); // ids only grow, so the list stays in id order
        Spawned++;
        _events?.Publish(new GroundAreaSpawned(area.Id, owner.Id, actionNetId, effectIndex, tick), CharacterOf(owner));
        return area;
    }

    /// <summary>Phase 5, after the strikes: every live area ends or ticks, in id order.</summary>
    public void Step(uint tick)
    {
        for (int i = 0; i < _active.Count; i++)
        {
            var area = _active[i];
            if (!area.IsLive)
                continue;
            if (area.Owner.Combatant is not { IsAlive: true })
                area.End(GroundAreaDespawnReason.OwnerDied, tick);
            else if (area.ExpiredAt(tick))
                area.End(GroundAreaDespawnReason.Expired, tick);
            else if (area.PulsesAt(tick))
                Pulse(area, tick);
        }
    }

    /// <summary>The owner left the world (removed in phase 1 or 8): its live areas end with <c>OwnerGone</c>.</summary>
    public void OwnerRemoved(Entity owner, uint tick)
    {
        foreach (var area in _active)
        {
            if (area.IsLive && area.Owner == owner)
                area.End(GroundAreaDespawnReason.OwnerGone, tick);
        }
    }

    /// <summary>After phase 10: areas that ended at least <c>eventRepeatTicks</c> ticks ago go back to the pool.</summary>
    public void ReleaseEnded(uint tick)
    {
        int repeats = _settings.EventRepeatTicks;
        for (int i = 0; i < _active.Count; i++)
        {
            var area = _active[i];
            if (area.IsLive || tick - area.EndTick + 1 < (uint)repeats)
                continue;
            _active.RemoveAt(i--); // keeps the id order
            area.Release();
            _pool.Push(area);
        }
    }

    private void Pulse(GroundArea area, uint tick)
    {
        var owner = area.Owner;
        var effect = area.Effect;
        bool enemies = effect.HasTarget(EffectTarget.Enemies);
        bool allies = effect.HasTarget(EffectTarget.Allies);
        bool self = effect.HasTarget(EffectTarget.Self);
        var faction = owner.Combatant!.Faction;
        int x = FixedPoint.ToCmRound(area.Origin.X), y = FixedPoint.ToCmRound(area.Origin.Y);
        int half = area.Shapes.ReachCm + _rules.MaxBodyRadiusCm;
        _candidates.Clear();
        _grid.QueryCells(x - half, y - half, x + half, y + half, _candidates);
        _candidates.Sort(ById);
        area.Pulses++;
        for (int c = 0; c < _candidates.Count; c++)
        {
            var target = _candidates[c];
            if (target.Combatant is not { IsAlive: true } other)
                continue;
            bool enemy = enemies && _resolver.CanHit(owner, target);
            bool healed = !enemy && area.HealAmount > 0
                && (target == owner ? self : allies && other.Faction == faction);
            if ((!enemy && !healed) || !Overlaps(area, target.Movement.Position, other.BodyRadiusRaw))
                continue;
            if (enemy)
                _resolver.Resolve(new HitRequest(owner, target, effect, HitSource.GroundArea, tick, DamagePercent: 100));
            else
                _statuses.Heal(owner.Id, CharacterOf(owner), target, other, area.HealAmount, area.ActionNetId, statusId: 0, tick);
        }
    }

    private static bool Overlaps(GroundArea area, FixVec2 position, int radiusRaw)
    {
        var shapes = area.Shapes.Shapes;
        for (int s = 0; s < shapes.Count; s++)
        {
            if (shapes[s].Overlaps(area.Origin, area.Facing, position, radiusRaw))
                return true;
        }
        return false;
    }

    private int CountLive(Entity owner)
    {
        int count = 0;
        foreach (var area in _active)
        {
            if (area.IsLive && area.Owner == owner)
                count++;
        }
        return count;
    }

    private static long CharacterOf(Entity entity) => entity is Player player ? player.CharacterId : 0;
}
