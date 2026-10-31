namespace Shared.Data.Tables
{
    /// <summary>
    /// Harvest yield rules (spec 5.2 "yield is a server roll using node tier, job level, skill-tree bonus, and randomness";
    /// anti-bot "diminishing returns on repetitive harvesting"; WP-M3-5). The yield's count grows with the job level, the
    /// tool's <see cref="ToolDef.YieldBonusPercent"/> and the job tree bonus, capped by <see cref="MaxBonusPercent"/>, and
    /// shrinks for each harvest of the same node type among the job's last <see cref="YieldRepeatWindow"/> activities (the
    /// job's saved recent window, so a relogin does not reset it). Every number in data is a placeholder.
    /// </summary>
    [Table("HarvestSettings", 1)]
    [Singleton]
    public sealed class HarvestSettings : ISetValidatableRow
    {
        /// <summary>How many of the job's latest activities count for yield repeats; at most the saved window (JobXpWeights).</summary>
        [Range(1, JobXpWeights.MaxRepeatWindow)] public int YieldRepeatWindow { get; set; }

        /// <summary>Percent of the yield lost per earlier harvest of the same node type in the window.</summary>
        [Range(0, 100)] public int YieldRepeatStepPercent { get; set; }

        /// <summary>The yield never drops below this percent of the undiminished count (and never below one unit).</summary>
        [Range(0, 100)] public int YieldRepeatFloorPercent { get; set; }

        /// <summary>Percent added to the count per job level above the first.</summary>
        [Range(0, 1_000)] public int YieldPercentPerJobLevel { get; set; }

        /// <summary>The job level, tool and job tree bonuses together never add more than this percent (rule 2: capped).</summary>
        [Range(0, 10_000)] public int MaxBonusPercent { get; set; }

        /// <summary>The job's saved recent window is as long as the largest <see cref="JobXpWeights.RepeatWindow"/>.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (!set.TryGet(typeof(JobXpWeights), out var weights))
                return;
            int window = 0;
            foreach (var row in weights.Rows)
                window = System.Math.Max(window, ((JobXpWeights)row).RepeatWindow);
            if (YieldRepeatWindow > window)
                errors.Add(nameof(YieldRepeatWindow), $"{YieldRepeatWindow} is longer than the job's saved window ({window}, JobXpWeights.repeatWindow)");
        }

        public static HarvestSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(HarvestSettings), out var table) && table.Rows.Count == 1 ? (HarvestSettings)table.Rows[0] : null;
    }
}
