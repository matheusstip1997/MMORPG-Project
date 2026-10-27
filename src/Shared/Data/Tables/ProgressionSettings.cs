namespace Shared.Data.Tables
{
    /// <summary>
    /// Level and skill point rules (spec 3.6, R-3.5-04; WP-M2-3). <see cref="MaxLevel"/> is a placeholder cap (user decision
    /// 2026-10-27): TODO(open) the real level cap; at the cap further exp is dropped. One skill point per level from level 1
    /// (R-3.5-04). <see cref="ExpCurve"/> has a row for every level below the cap.
    /// </summary>
    [Table("ProgressionSettings", 1)]
    [Singleton]
    public sealed class ProgressionSettings
    {
        /// <summary>Highest level a character reaches (placeholder 130, the top of the SS band of R-3.6-02).</summary>
        [Range(2, 10_000)] public int MaxLevel { get; set; }

        /// <summary>Skill points per level (R-3.5-04: one per level, from level 1).</summary>
        [Range(0, 100)] public int SkillPointsPerLevel { get; set; }

        public static ProgressionSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(ProgressionSettings), out var table) && table.Rows.Count == 1 ? (ProgressionSettings)table.Rows[0] : null;
    }
}
