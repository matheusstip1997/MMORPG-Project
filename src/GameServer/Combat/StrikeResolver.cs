using GameServer.Entities;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Simulation;

namespace GameServer.Combat;

/// <summary>
/// Tick phase 5 for strikes (spec 1.6, 3.1 <c>HitResolver</c>, backlog D3): resolves the strikes phase 3 queued, in the
/// order they fired (attacker id). For each one:
/// <list type="number">
/// <item>the grid cells around the caster's position when it fired, wide enough for the effect's shapes, the largest body
/// radius and how far a target can move in the rewind window;</item>
/// <item>the candidates in entity-id order, filtered by <see cref="IHitTargetResolver.CanHit"/>;</item>
/// <item>lag compensation (R-3.1-03): when a player hits a monster, the monster's position history is rewound to what the
/// player saw (<see cref="Player.ViewLagTicks"/>, at most <c>lagCompensationCapMs</c>); everything else is tested where it
/// stands now, and a monster's hit on a player waits as a pending hit (<see cref="HitResolver"/>, R-3.1-03).
/// TODO(WP6-PvP): PvP rewinds the attacker by <c>pvpAttackerRewindMs</c> (PvP is off in M1);</item>
/// <item>a target is hit when its body circle touches any of the effect's shapes, once per effect (spec 3.1), and goes
/// through <see cref="IHitTargetResolver.Resolve"/>.</item>
/// </list>
/// A strike whose effect has no shapes (the validator forbids it) hits nothing and is counted. Allocation-free once its
/// candidate list has grown.
/// </summary>
internal sealed class StrikeResolver
{
    // A cached Comparison: List.Sort(IComparer) would allocate a delegate on every call.
    private static readonly Comparison<Entity> ById = (a, b) => a.Id.CompareTo(b.Id);

    private readonly SpatialGrid _grid;
    private CombatRules _rules;
    private readonly IHitTargetResolver _resolver;
    private readonly List<Entity> _candidates = new(32);

    public StrikeResolver(SpatialGrid grid, CombatRules rules, IHitTargetResolver resolver)
    {
        _grid = grid;
        _rules = rules;
        _resolver = resolver;
    }

    /// <summary>Dev only (spec 11.7): told every strike and every target it is tested against; null reports nothing.</summary>
    public IStrikeTraceSink? Trace { get; set; }

    /// <summary>Strikes whose effect has no hit shapes (a bug in data the validator should have caught).</summary>
    public long StrikesWithoutShapes { get; private set; }

    /// <summary>Strikes that hit nothing because their attacker was blinded (WP-M2-9).</summary>
    public long BlindedStrikes { get; private set; }

    /// <summary>Dev hot reload (spec 11.7): the shapes, radii and lag compensation of the reloaded tables from the next strike.</summary>
    public void Retune(CombatRules rules) => _rules = rules;

    /// <summary>Resolves and clears the queue.</summary>
    public void Resolve(StrikeQueue queue)
    {
        for (int i = 0; i < queue.Count; i++)
            ResolveOne(queue[i]);
        queue.Clear();
    }

    private void ResolveOne(in PendingStrike strike)
    {
        if (!_rules.Shapes.TryGet(strike.Effect.Id, out var shapes))
        {
            StrikesWithoutShapes++;
            return;
        }
        int x = FixedPoint.ToCmRound(strike.Origin.X), y = FixedPoint.ToCmRound(strike.Origin.Y);
        int half = shapes.ReachCm + _rules.MaxBodyRadiusCm + _rules.RewindMarginCm;
        _candidates.Clear();
        _grid.QueryCells(x - half, y - half, x + half, y + half, _candidates);
        _candidates.Sort(ById);

        var attacker = strike.Attacker;
        Trace?.StrikePlaced(strike.Tick, attacker.Id, strike.ActionNetId, strike.EffectIndex, strike.Origin, strike.Facing);
        // Spec 3.7 M2 status list (WP-M2-9): a blinded attacker's strikes hit nothing (an explosion's blind was decided
        // when its projectile was fired).
        if (!strike.Impact && attacker.Combatant is { } source && source.Statuses.Has(StatusControl.Blind))
        {
            BlindedStrikes++;
            return;
        }
        for (int c = 0; c < _candidates.Count; c++)
        {
            var target = _candidates[c];
            if (!_resolver.CanHit(attacker, target) || strike.Skip != null && strike.Skip.Contains(target.Id))
                continue;
            var position = strike.Impact ? target.Movement.Position : PositionSeenBy(attacker, target, strike.Tick);
            int radius = target.Combatant!.BodyRadiusRaw;
            Trace?.TargetTested(strike.Tick, attacker.Id, target.Id, position, radius);
            if (!Overlaps(shapes, strike, position, radius))
                continue;
            _resolver.Resolve(new HitRequest(attacker, target, strike.Effect, HitSource.Strike, strike.Tick, DamagePercent: 100));
        }
    }

    /// <summary>R-3.1-03: where <paramref name="attacker"/> saw <paramref name="target"/> (capped rewind) or where it stands.</summary>
    internal FixVec2 PositionSeenBy(Entity attacker, Entity target, uint tick)
    {
        if (attacker is Player player && target is Monster)
        {
            int lag = Math.Min(player.ViewLagTicks, _rules.LagCompensationCapTicks);
            if (lag > 0 && target.Combatant!.History.TryGetAt(tick - (uint)lag, out var past))
                return past;
        }
        return target.Movement.Position;
    }

    private static bool Overlaps(EffectShapes shapes, in PendingStrike strike, FixVec2 position, int radiusRaw)
    {
        var list = shapes.Shapes;
        for (int s = 0; s < list.Count; s++)
        {
            if (list[s].Overlaps(strike.Origin, strike.Facing, position, radiusRaw))
                return true;
        }
        return false;
    }
}
