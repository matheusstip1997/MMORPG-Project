using System;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Book slots unlocked at each rank (R-14.2-04; user decision 2026-10-27, placeholders: F 1, E 2, D 3, C 4, B 5, A 6,
    /// S 7, SS 8). Exactly one row per rank; a higher rank never has fewer slots (ranks only go up, so a placed card never
    /// loses its slot). At most <see cref="BookLimits.MaxSlots"/>.
    /// </summary>
    [Table("BystialSlots", 1)]
    public sealed class BystialSlots : IValidatableRow, ISetValidatableRow
    {
        public Rank Rank { get; set; }

        [Range(0, BookLimits.MaxSlots)] public int Slots { get; set; }

        public void Validate(RowErrors errors)
        {
            if (!Enum.IsDefined(typeof(Rank), Rank))
                errors.Add(nameof(Rank), $"{(int)Rank} is not a rank");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<BystialSlots>(set);
            int index = rows.IndexOf(this);
            if (rows.FindIndex(r => r.Rank == Rank) != index)
                errors.Add(nameof(Rank), $"{Rank} has more than one row");
            foreach (var other in rows)
            {
                if (other.Rank < Rank && other.Slots > Slots)
                    errors.Add(nameof(Slots), $"must not be fewer than rank {other.Rank}'s {other.Slots}");
            }
            if (index != 0)
                return;
            for (int r = 0; r < Ranks.Count; r++)
            {
                if (rows.TrueForAll(row => (int)row.Rank != r))
                    errors.Add(nameof(Rank), $"rank {(Rank)r} has no row");
            }
        }

        /// <summary>The most slots any rank has (0 when the table is not loaded).</summary>
        public static int MaxOf(ConfigSet set)
        {
            int max = 0;
            foreach (var row in NetIds.Rows<BystialSlots>(set))
                max = Math.Max(max, row.Slots);
            return max;
        }
    }
}
