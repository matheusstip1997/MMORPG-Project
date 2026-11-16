using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// The tables <see cref="SnapshotPriority"/> and <see cref="SnapshotKindWeight"/> as the snapshot sender reads them (spec 1.7,
/// user decision 2026-11-16): radii squared for integer distance checks, the stale limit in ticks, one weight per entity kind.
/// </summary>
public sealed class SnapshotPriorityRules
{
    private readonly int[] _kindWeightPercent;

    private SnapshotPriorityRules(long telegraphRadiusSq, long nearRadiusSq, int farWeightPercent, uint maxStaleTicks, int[] kindWeights)
    {
        TelegraphRadiusSq = telegraphRadiusSq;
        NearRadiusSq = nearRadiusSq;
        FarWeightPercent = farWeightPercent;
        MaxStaleTicks = maxStaleTicks;
        _kindWeightPercent = kindWeights;
    }

    /// <summary><see cref="SnapshotPriority.TelegraphRadiusCm"/> squared.</summary>
    public long TelegraphRadiusSq { get; }

    /// <summary><see cref="SnapshotPriority.NearRadiusCm"/> squared.</summary>
    public long NearRadiusSq { get; }

    public int FarWeightPercent { get; }

    /// <summary><see cref="SnapshotPriority.MaxStaleMs"/> in server ticks, rounded up, at least 1.</summary>
    public uint MaxStaleTicks { get; }

    /// <summary>The kind's weight in percent; 100 for a kind without a row (only possible in worlds built by hand).</summary>
    public int KindWeightPercent(EntityKind kind) =>
        (int)kind < _kindWeightPercent.Length && _kindWeightPercent[(int)kind] > 0 ? _kindWeightPercent[(int)kind] : 100;

    public static SnapshotPriorityRules Create(SnapshotPriority priority, IEnumerable<SnapshotKindWeight> weights, int tickRateHz)
    {
        if (tickRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(tickRateHz), "must be positive");
        var kindWeights = new int[(int)EntityKind.LootDrop + 1];
        foreach (var row in weights)
        {
            if (SnapshotKindWeight.TryParseKind(row.Id, out var kind))
                kindWeights[(int)kind] = row.WeightPercent;
        }
        long staleTicks = Math.Max(1, ((long)priority.MaxStaleMs * tickRateHz + 999) / 1000);
        return new SnapshotPriorityRules((long)priority.TelegraphRadiusCm * priority.TelegraphRadiusCm,
            (long)priority.NearRadiusCm * priority.NearRadiusCm, priority.FarWeightPercent, (uint)staleTicks, kindWeights);
    }

    /// <summary>The rules from a loaded set, or null when it has no <see cref="SnapshotPriority"/> (the age-only order then).</summary>
    public static SnapshotPriorityRules? From(ConfigSet set, int tickRateHz)
    {
        var priority = SnapshotPriority.Find(set);
        if (priority == null)
            return null;
        var weights = set.TryGet(typeof(SnapshotKindWeight), out var table)
            ? table.Rows.OfType<SnapshotKindWeight>()
            : Enumerable.Empty<SnapshotKindWeight>();
        return Create(priority, weights, tickRateHz);
    }
}
