namespace Shared.Data.Tables
{
    /// <summary>
    /// The job XP a job needs to go from <see cref="Level"/> to the next job level (spec 5.1 R-5.1-04; WP-M3-1; the table
    /// name is the WP-M3-1 plan's, spec 8.5 named none). One row per level from 1 to <c>JobSettings.maxLevel - 1</c>, in
    /// order, never decreasing. Every number is a placeholder.
    /// </summary>
    [Table("JobLevelCurve", 1)]
    public sealed class JobLevelCurve : ISetValidatableRow
    {
        [Range(1, 1_000)] public int Level { get; set; }

        [Range(1, 1_000_000_000)] public int XpToNext { get; set; }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<JobLevelCurve>(set);
            int index = rows.IndexOf(this);
            if (Level != index + 1)
                errors.Add(nameof(Level), $"row {index} must be level {index + 1}: one row per level from 1, in order");
            if (index > 0 && rows[index - 1].XpToNext > XpToNext)
                errors.Add(nameof(XpToNext), $"{XpToNext} is below the previous level's {rows[index - 1].XpToNext}");
            if (index == rows.Count - 1 && JobSettings.Find(set) is { } settings && Level != settings.MaxLevel - 1)
                errors.Add(nameof(Level), $"the last row must be level {settings.MaxLevel - 1} (JobSettings.maxLevel - 1), not {Level}");
        }
    }
}
