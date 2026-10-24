using GameServer.Combat;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Combat;

namespace GameServer.AI;

/// <summary>
/// One attack of a monster (<see cref="MonsterDef.Attacks"/>) as the AI judges it: would it hit a target standing here if
/// it started now, aimed at that target? Strikes are tested with their own hit shapes, placed where the caster will be
/// after the dashes that come before them (spec 3.1); a projectile reaches its <c>rangeCm</c> plus its hit radius. The
/// reach comes from the same rows the hit resolution uses, so it is never a second number to tune. Immutable.
/// </summary>
public sealed class MonsterAttack
{
    private readonly StrikePart[] _strikes;
    private readonly long _projectileReachRaw;

    private MonsterAttack(ActionTiming action, StrikePart[] strikes, long projectileReachRaw, int nominalReachCm)
    {
        Action = action;
        _strikes = strikes;
        _projectileReachRaw = projectileReachRaw;
        NominalReachCm = nominalReachCm;
    }

    public ActionTiming Action { get; }

    /// <summary>Catalog index the AI requests.</summary>
    public int Index => Action.Index;

    /// <summary>A bound on how far it reaches (dashes plus shape sizes, or the projectile's range): orders attacks by reach.</summary>
    public int NominalReachCm { get; }

    /// <summary>
    /// Builds the attack from its action's effects: Strike effects with their shapes (missing shapes: the strike is
    /// ignored), Projectile effects with their <see cref="ProjectileDef"/> (no projectile tables: ignored), and the Dash
    /// distances that fire before each strike.
    /// </summary>
    public static MonsterAttack From(ActionTiming action, StrikeShapes shapes, ProjectileRules? projectiles)
    {
        var strikes = new List<StrikePart>();
        long projectileReachRaw = 0;
        long nominal = 0;
        foreach (var effect in action.Effects)
        {
            switch (effect.Kind)
            {
                case EffectKind.Strike when shapes.TryGet(effect.Def.Id, out var effectShapes):
                {
                    int dashRaw = DashBefore(action, effect.FireTick);
                    strikes.Add(new StrikePart(effectShapes.Shapes.ToArray(), dashRaw));
                    nominal = Math.Max(nominal, (long)FixedPoint.ToCmFloor(dashRaw) + effectShapes.ReachCm);
                    break;
                }
                case EffectKind.Projectile when projectiles != null && projectiles.TryGet(effect.Def.ProjectileId, out var def):
                {
                    long reachCm = (long)def.RangeCm + def.HitRadiusCm;
                    projectileReachRaw = Math.Max(projectileReachRaw, reachCm * FixedPoint.RawPerCm);
                    nominal = Math.Max(nominal, reachCm);
                    break;
                }
            }
        }
        return new MonsterAttack(action, strikes.ToArray(), projectileReachRaw, (int)Math.Min(int.MaxValue, nominal));
    }

    /// <summary>
    /// Whether the attack, started now by a caster at <paramref name="caster"/> facing <paramref name="aim"/>, would hit a
    /// body circle of <paramref name="targetRadiusRaw"/> at <paramref name="target"/> if nothing moved. Allocation-free.
    /// </summary>
    public bool WouldHit(FixVec2 caster, ushort aim, FixVec2 target, int targetRadiusRaw)
    {
        foreach (var strike in _strikes)
        {
            var origin = caster + FixedTrig.Along(aim, strike.DashRaw);
            foreach (var shape in strike.Shapes)
            {
                if (shape.Overlaps(origin, aim, target, targetRadiusRaw))
                    return true;
            }
        }
        if (_projectileReachRaw > 0)
        {
            long reach = _projectileReachRaw + targetRadiusRaw;
            long dx = (long)target.X - caster.X, dy = (long)target.Y - caster.Y;
            // Beyond this either axis alone is out of reach; it also keeps the squares inside a long.
            if (Math.Abs(dx) > reach || Math.Abs(dy) > reach)
                return false;
            return dx * dx + dy * dy <= reach * reach;
        }
        return false;
    }

    /// <summary>
    /// Q24.8 distance of the dashes that fire before <paramref name="fireTick"/> (spec 3.1 "dash, then strike"), taken as
    /// forward: monster dashes have no direction offset (only the Archer's Back Step has one).
    /// </summary>
    private static int DashBefore(ActionTiming action, int fireTick)
    {
        long raw = 0;
        foreach (var effect in action.Effects)
        {
            if (effect.Kind == EffectKind.Dash && effect.FireTick + effect.DashTicks <= fireTick)
                raw += effect.DashDistanceRaw;
        }
        return (int)Math.Min(int.MaxValue, raw);
    }

    private readonly struct StrikePart
    {
        public StrikePart(HitShape[] shapes, int dashRaw)
        {
            Shapes = shapes;
            DashRaw = dashRaw;
        }

        public HitShape[] Shapes { get; }
        public int DashRaw { get; }
    }
}
