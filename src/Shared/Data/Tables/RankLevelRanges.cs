using System;
using Shared.Simulation.Stats;

namespace Shared.Data.Tables
{
    /// <summary>
    /// The overlapping level band of each rank (R-3.6-02: F 0-25, E 5-40, D 15-50, C 25-70, B 35-85, A 45-100, S 55-125,
    /// SS 70-130). The band modifier (<see cref="ExpBandCurves"/>) compares a character's level with its own rank's band.
    /// Exactly one row per rank.
    /// </summary>
    [Table("RankLevelRanges", 1)]
    public sealed class RankLevelRanges : IValidatableRow, ISetValidatableRow
    {
        public Rank Rank { get; set; }

        [Range(0, 10_000)] public int MinLevel { get; set; }

        [Range(0, 10_000)] public int MaxLevel { get; set; }

        public void Validate(RowErrors errors)
        {
            if (!Enum.IsDefined(typeof(Rank), Rank))
                errors.Add(nameof(Rank), $"{(int)Rank} is not a rank");
            if (MinLevel > MaxLevel)
                errors.Add(nameof(MinLevel), "must not be greater than MaxLevel");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<RankLevelRanges>(set);
            int index = rows.IndexOf(this);
            if (rows.FindIndex(r => r.Rank == Rank) != index)
                errors.Add(nameof(Rank), $"{Rank} has more than one row");
            if (index != 0)
                return;
            for (int r = 0; r < Ranks.Count; r++)
            {
                if (rows.TrueForAll(row => (int)row.Rank != r))
                    errors.Add(nameof(Rank), $"rank {(Rank)r} has no row");
            }
        }
    }
}
