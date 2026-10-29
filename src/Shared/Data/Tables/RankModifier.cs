namespace Shared.Data.Tables
{
    /// <summary>
    /// The rank modifier of R-3.6-03: exp percent by <see cref="RankDiff"/> = monster rank minus player rank (higher-rank
    /// monsters give more). A difference beyond the rows is clamped to the nearest row. The rows are contiguous, ascending,
    /// and diff 0 is 100. Placeholders (user decision 2026-10-27): -2 50, -1 75, 0 100, +1 125, +2 150.
    /// </summary>
    [Table("RankModifier", 1)]
    public sealed class RankModifier : ISetValidatableRow
    {
        [Range(-7, 7)] public int RankDiff { get; set; }

        [Range(0, 1_000)] public int Percent { get; set; }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<RankModifier>(set);
            int index = rows.IndexOf(this);
            if (index > 0 && RankDiff != rows[index - 1].RankDiff + 1)
                errors.Add(nameof(RankDiff), $"{RankDiff} must follow {rows[index - 1].RankDiff}: contiguous rank differences in ascending order");
            if (RankDiff == 0 && Percent != 100)
                errors.Add(nameof(Percent), "the same rank gives x1.0 (R-3.6-03): diff 0 must be 100");
            if (index == 0 && rows.TrueForAll(r => r.RankDiff != 0))
                errors.Add(nameof(RankDiff), "needs a row for diff 0 (same rank)");
        }
    }
}
