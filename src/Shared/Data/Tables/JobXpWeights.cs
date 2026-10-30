using System;
using Shared.Simulation.Jobs;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Job XP per activity (spec 5.1 R-5.1-04; WP-M3-1): <see cref="BaseXp"/> times the difficulty factor of the event's tier
    /// times the diminishing return for repeating the same node or recipe among the job's last <see cref="RepeatWindow"/>
    /// activities (<c>JobMath</c>). An activity without a row pays no job XP. TODO(open): the XP split between gathering and
    /// crafting (R-5.1-04, spec 10.4); every number is a placeholder (M3 plan decision 8).
    /// </summary>
    [Table("JobXpWeights", 1)]
    public sealed class JobXpWeights : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Largest repeat window: the size of the saved recent ring per job.</summary>
        public const int MaxRepeatWindow = 64;

        [TableId] public string Id { get; set; } = string.Empty;

        public JobActivity Activity { get; set; }

        [Range(0, 1_000_000)] public int BaseXp { get; set; }

        /// <summary>Difficulty factor in percent by tier (index tier - 1); a tier past the end uses the last entry.</summary>
        public int[] TierPercent { get; set; } = Array.Empty<int>();

        /// <summary>How many of the job's latest activities count for repeats.</summary>
        [Range(1, MaxRepeatWindow)] public int RepeatWindow { get; set; }

        /// <summary>Percent of the XP lost per earlier repeat in the window.</summary>
        [Range(0, 100)] public int RepeatStepPercent { get; set; }

        /// <summary>The XP never drops below this percent of the undiminished value.</summary>
        [Range(0, 100)] public int RepeatFloorPercent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Activity == JobActivity.None || !Enum.IsDefined(typeof(JobActivity), Activity))
                errors.Add(nameof(Activity), $"{(int)Activity} is not a job activity");
            if (TierPercent.Length == 0)
                errors.Add(nameof(TierPercent), "must list at least one tier");
            foreach (int percent in TierPercent)
            {
                if (percent < 0 || percent > 10_000)
                    errors.Add(nameof(TierPercent), $"{percent} is outside 0..10000");
            }
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<JobXpWeights>(set);
            if (rows.FindIndex(r => r.Activity == Activity) != rows.IndexOf(this))
                errors.Add(nameof(Activity), $"{Activity} has more than one row");
        }
    }
}
