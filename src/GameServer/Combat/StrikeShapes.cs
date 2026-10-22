using Shared.Data.Tables;
using Shared.Simulation.Combat;

namespace GameServer.Combat;

/// <summary>The shapes of one effect (1 to 4, spec 3.1) and how far from the caster any of them reaches.</summary>
public sealed class EffectShapes
{
    internal EffectShapes(HitShape[] shapes, int reachCm)
    {
        Shapes = shapes;
        ReachCm = reachCm;
    }

    public IReadOnlyList<HitShape> Shapes { get; }

    /// <summary>
    /// A bound on the distance from the caster to any point of any shape, along either axis (offsets plus every size, so
    /// it over-counts): the half-size of the grid query before the exact shape tests.
    /// </summary>
    public int ReachCm { get; }
}

/// <summary>
/// The hit shapes of every Strike and GroundArea effect (R-3.1-03, spec 3.1 <c>HitShapeDef</c>), built once from the
/// table with <see cref="HitShape.From"/> and looked up by effect id. Immutable.
/// </summary>
public sealed class StrikeShapes
{
    private readonly Dictionary<string, EffectShapes> _byEffect;

    private StrikeShapes(Dictionary<string, EffectShapes> byEffect)
    {
        _byEffect = byEffect;
    }

    public static StrikeShapes Empty { get; } = new(new Dictionary<string, EffectShapes>(StringComparer.Ordinal));

    public int EffectCount => _byEffect.Count;

    /// <summary>Rows in table order; the validator already checked kinds, sizes and at most four per effect.</summary>
    public static StrikeShapes From(IEnumerable<HitShapeDef> rows)
    {
        var grouped = new Dictionary<string, List<HitShapeDef>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!grouped.TryGetValue(row.EffectId, out var list))
                grouped.Add(row.EffectId, list = new List<HitShapeDef>());
            list.Add(row);
        }
        var byEffect = new Dictionary<string, EffectShapes>(StringComparer.Ordinal);
        foreach (var (effectId, list) in grouped)
        {
            var shapes = new HitShape[list.Count];
            long reach = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var row = list[i];
                shapes[i] = HitShape.From(row);
                reach = Math.Max(reach, (long)Math.Abs(row.ForwardOffsetCm) + Math.Abs(row.SideOffsetCm) + row.RangeCm
                    + row.LengthCm + row.WidthCm / 2 + row.RadiusCm);
            }
            byEffect.Add(effectId, new EffectShapes(shapes, (int)Math.Min(int.MaxValue / 4, reach)));
        }
        return new StrikeShapes(byEffect);
    }

    public bool TryGet(string effectId, out EffectShapes shapes) => _byEffect.TryGetValue(effectId, out shapes!);
}
