using Shared.Data.Tables;
using Shared.Simulation;
using Shared.World;

namespace BotClient.Brain;

/// <summary>A monster spawn point as a back-off leg sees it: the monster's home, leash and aggro range (MonsterDef).</summary>
public readonly record struct SpawnCircle(int MonsterNetId, FixVec2 Home, int LeashRangeCm, int AggroRangeCm);

/// <summary>
/// The spawn points of the world for planned back-off legs (<see cref="BotBehaviour.PlanBackOffLegs"/>, WP-M2-12): every
/// <c>SpawnPoints</c> marker with its monster's leash and aggro ranges, in marker id order.
/// </summary>
public sealed class BackOffMap
{
    public BackOffMap(IReadOnlyList<SpawnCircle> spawns)
    {
        Spawns = spawns;
    }

    public IReadOnlyList<SpawnCircle> Spawns { get; }

    /// <summary>From the spawn point rows, the world's markers and the monster rows (points without a marker are skipped).</summary>
    public static BackOffMap From(IEnumerable<SpawnPoints> points, IReadOnlyDictionary<string, FixVec2> markers, IEnumerable<MonsterDef> monsters)
    {
        var defs = monsters.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var spawns = new List<SpawnCircle>();
        foreach (var point in points.OrderBy(p => p.MarkerId, StringComparer.Ordinal))
        {
            if (markers.TryGetValue(point.MarkerId, out var at) && defs.TryGetValue(point.MonsterId, out var monster))
                spawns.Add(new SpawnCircle(monster.NetId, at, monster.LeashRangeCm, monster.AggroRangeCm));
        }
        return new BackOffMap(spawns);
    }

    /// <summary>The spawn point of a monster type nearest to where the monster stands (its home for the leash); null for none.</summary>
    public SpawnCircle? HomeOf(int monsterNetId, FixVec2 at)
    {
        SpawnCircle? best = null;
        double bestCm = double.MaxValue;
        foreach (var spawn in Spawns)
        {
            if (spawn.MonsterNetId != monsterNetId)
                continue;
            double cm = BackOffLeg.DistanceCm(spawn.Home, at);
            if (cm < bestCm)
            {
                best = spawn;
                bestCm = cm;
            }
        }
        return best;
    }
}

/// <summary>How a planned leg was found.</summary>
public enum BackOffLegKind : byte
{
    /// <summary>Inside the threat's leash and outside every other spawn point's aggro.</summary>
    Planned,
    /// <summary>No direction kept clear of the other spawns' aggro: inside the leash only.</summary>
    LeashOnly,
    /// <summary>Nothing fitted: straight away from the threat, as an unplanned leg.</summary>
    Straight,
}

/// <summary>
/// Where a bot's back-off leg ends (WP-M2-12, the open follow-up of the bot back-off fix), ported from the QA kill step's rule
/// (<c>QaNavigation.TryBackOffTarget</c>, QA wave 5 H2 and smoke fix5c-4): <c>backOffDistanceCm</c> from the bot, as directly
/// away from the threat as the world allows, turning in <see cref="TurnDegrees"/> steps to both sides. The leg ends within the
/// threat's <c>leashRangeCm</c> minus <c>backOffLeashMarginCm</c> of its home (so the monster does not leash, walk home and
/// heal), the straight walk there stays on walkable cells and enters no region the bot's level gate closes (R-2.3-06,
/// <c>GridPath.Clear</c> with the bot's <see cref="MoverGate"/>), and its end lies outside every other spawn point's
/// <c>aggroRangeCm</c> plus <c>avoidAggroMarginCm</c> (so the bot does not pull a second monster). When no
/// direction fits it tries again inside the leash only, then falls back to the straight leg. QA's no-entry check (the city)
/// is not ported: bots back off in the field. Pure; the same inputs give the same leg.
/// </summary>
public static class BackOffLeg
{
    /// <summary>The step between the directions a leg tries (QA: <c>QaNavigation.BackOffTurnDegrees</c>).</summary>
    public const int TurnDegrees = 15;

    public static (FixVec2 To, BackOffLegKind Kind) Plan(WorldGrid grid, BackOffMap map, BotBehaviour behaviour, FixVec2 from,
        FixVec2 threat, int threatNetId, in MoverGate gate = default)
    {
        int distanceCm = behaviour.BackOffDistanceCm;
        if (map.HomeOf(threatNetId, threat) is { } home)
        {
            int maxFromHomeCm = Math.Max(0, home.LeashRangeCm - behaviour.BackOffLeashMarginCm);
            var avoid = map.Spawns.Where(s => s.Home != home.Home)
                .Select(s => (s.Home, s.AggroRangeCm + behaviour.AvoidAggroMarginCm)).ToList();
            if (TryTarget(grid, from, threat, home.Home, distanceCm, maxFromHomeCm, avoid, out var planned, gate))
                return (planned, BackOffLegKind.Planned);
            if (TryTarget(grid, from, threat, home.Home, distanceCm, maxFromHomeCm, null, out var leashOnly, gate))
                return (leashOnly, BackOffLegKind.LeashOnly);
        }
        return (Straight(from, threat, distanceCm), BackOffLegKind.Straight);
    }

    /// <summary>
    /// The QA rule (<c>QaNavigation.TryBackOffTarget</c> without the no-entry regions): the first direction from straight away
    /// from <paramref name="threat"/>, turning both ways, whose end is within <paramref name="maxFromHomeCm"/> of
    /// <paramref name="home"/>, reached by a clear straight walk (walkable, through no closed level gate), and outside every
/// <paramref name="avoid"/> circle.
    /// </summary>
    public static bool TryTarget(WorldGrid grid, FixVec2 from, FixVec2 threat, FixVec2 home, int distanceCm, int maxFromHomeCm,
        IReadOnlyList<(FixVec2 At, int RadiusCm)>? avoid, out FixVec2 target, in MoverGate gate = default)
    {
        target = from;
        double fromX = Cm(from.X), fromY = Cm(from.Y);
        double away = Math.Atan2(fromX - Cm(threat.X), fromY - Cm(threat.Y)); // clockwise from +Y
        for (int turn = 0; turn <= 180; turn += TurnDegrees)
        {
            for (int side = 1; side >= -1; side -= 2)
            {
                if ((turn == 0 || turn == 180) && side < 0)
                    continue;
                double angle = away + side * turn * Math.PI / 180.0;
                var to = FixVec2.FromCm((int)Math.Round(fromX + Math.Sin(angle) * distanceCm), (int)Math.Round(fromY + Math.Cos(angle) * distanceCm));
                if (DistanceCm(to, home) > maxFromHomeCm || !GridPath.Clear(grid, from, to, gate) || InsideAny(to, avoid))
                    continue;
                target = to;
                return true;
            }
        }
        return false;
    }

    /// <summary>The unplanned leg: <paramref name="distanceCm"/> straight away from <paramref name="threat"/>.</summary>
    public static FixVec2 Straight(FixVec2 from, FixVec2 threat, int distanceCm)
    {
        double length = Math.Max(1.0, DistanceCm(from, threat));
        double awayX = (Cm(from.X) - Cm(threat.X)) / length, awayY = (Cm(from.Y) - Cm(threat.Y)) / length;
        return from + FixVec2.FromCm((int)(awayX * distanceCm), (int)(awayY * distanceCm));
    }

    public static double DistanceCm(FixVec2 a, FixVec2 b)
    {
        double dx = Cm(a.X) - Cm(b.X), dy = Cm(a.Y) - Cm(b.Y);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool InsideAny(FixVec2 point, IReadOnlyList<(FixVec2 At, int RadiusCm)>? circles)
    {
        if (circles == null)
            return false;
        foreach (var (at, radius) in circles)
        {
            if (DistanceCm(point, at) < radius)
                return true;
        }
        return false;
    }

    private static double Cm(int raw) => GridPath.Cm(raw);
}
