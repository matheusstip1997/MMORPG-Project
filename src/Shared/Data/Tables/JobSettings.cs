namespace Shared.Data.Tables
{
    /// <summary>
    /// Job progress rules (spec 5.1, R-5.1-04; WP-M3-1). <see cref="MaxLevel"/> is a placeholder job level cap (at the cap
    /// further job XP is dropped); <see cref="JobLevelCurve"/> has a row for every level below it. Job points are spent by
    /// the job skill trees (WP-M3-7). Every number is a placeholder (balance is deferred).
    /// </summary>
    [Table("JobSettings", 1)]
    [Singleton]
    public sealed class JobSettings
    {
        [Range(2, 1_000)] public int MaxLevel { get; set; }

        /// <summary>Job points per job level gained (spent by the job skill tree, R-5.1-04).</summary>
        [Range(0, 100)] public int PointsPerLevel { get; set; }

        /// <summary>
        /// Operation keys of activity events remembered per character, so a repeated event adds nothing (architecture rule 3:
        /// consumers are idempotent). Kept in memory while the character is on the world.
        /// </summary>
        [Range(1, 4_096)] public int RecentOpKeys { get; set; }

        public static JobSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(JobSettings), out var table) && table.Rows.Count == 1 ? (JobSettings)table.Rows[0] : null;
    }
}
