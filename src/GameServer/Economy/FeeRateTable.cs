using Shared.Data.Tables;
using Shared.Simulation.Economy;

namespace GameServer.Economy;

/// <summary>The <see cref="FeeRates"/> rows by <see cref="FeeType"/> (spec 4.2 R-4.2-05, WP-M4-1). Built once from validated tables.</summary>
public sealed class FeeRateTable
{
    private readonly FeeRates[] _byType;

    private FeeRateTable(FeeRates[] byType) => _byType = byType;

    /// <summary>The row of <paramref name="type"/>.</summary>
    public FeeRates this[FeeType type] => _byType[(int)type];

    /// <summary>One row per fee type; null when a type has no row (the table is not loaded: worlds built without fees).</summary>
    public static FeeRateTable? From(IEnumerable<FeeRates> rows)
    {
        var byType = new FeeRates[FeeTypes.All.Length];
        foreach (var row in rows)
        {
            if (FeeTypes.TryParse(row.Id, out var type))
                byType[(int)type] = row;
        }
        return Array.TrueForAll(byType, r => r != null) ? new FeeRateTable(byType) : null;
    }
}
