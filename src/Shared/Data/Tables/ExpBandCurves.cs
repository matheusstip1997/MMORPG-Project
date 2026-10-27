namespace Shared.Data.Tables
{
    /// <summary>
    /// The band modifier of R-3.6-03: a character's level against its own rank's level band (<see cref="RankLevelRanges"/>).
    /// Inside the band x1.0; below its minimum a bonus of <see cref="BelowBonusPercentPerLevel"/> per level, capped at
    /// <see cref="BelowBonusCapPercent"/>; above its maximum x<see cref="AboveMultiplierPercentPerLevel"/>/100 once per level,
    /// multiplied, with no floor (the dead zone). Placeholders (user decision 2026-10-27: +2% per level capped at +20%, x0.6
    /// per level). It is also what spec 8.5 called <c>LevelPenalty</c>.
    /// </summary>
    [Table("ExpBandCurves", 1)]
    [Singleton]
    public sealed class ExpBandCurves
    {
        [Range(0, 100)] public int BelowBonusPercentPerLevel { get; set; }

        [Range(0, 1_000)] public int BelowBonusCapPercent { get; set; }

        /// <summary>Kept per level above the band, in percent (60 = x0.6 per level); below 100, so it is a penalty.</summary>
        [Range(0, 99)] public int AboveMultiplierPercentPerLevel { get; set; }
    }
}
