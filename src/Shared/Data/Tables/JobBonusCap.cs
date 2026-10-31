using System;
using System.Collections.Generic;
using Shared.Simulation.Jobs;

namespace Shared.Data.Tables
{
    /// <summary>
    /// The cap of one job bonus kind on the sum of its sources (spec 5.1, architecture rule 2; <c>docs/design/job-trees.md</c>
    /// decision 4; WP-M3-7): <c>IJobBonusQuery</c> never returns more than <see cref="Cap"/> for an activity. The
    /// <see cref="JobBonusKind.EnchantOddsPercent"/> row is the job-skill cap of R-5.3-04. A system with a source of its own
    /// (a tool's yield bonus, a <c>QualityCaps</c> term) keeps its own bound. Every number is a placeholder.
    /// </summary>
    [Table("JobBonusCap", 1)]
    public sealed class JobBonusCap : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        public JobBonusKind Kind { get; set; }

        [Range(0, 10_000)] public int Cap { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Kind == JobBonusKind.None || !Enum.IsDefined(typeof(JobBonusKind), Kind))
                errors.Add(nameof(Kind), $"{(int)Kind} is not a job bonus kind");
            else if (Kind == JobBonusKind.UnlockMoldTiers)
                errors.Add(nameof(Kind), "unlockMoldTiers is a tier set, not a number: it has no cap");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<JobBonusCap>(set);
            if (rows.FindIndex(r => r.Kind == Kind) != rows.IndexOf(this))
                errors.Add(nameof(Kind), $"{Kind} has more than one row");
        }

        public static JobBonusCap? Find(ConfigSet set, JobBonusKind kind)
        {
            foreach (var row in NetIds.Rows<JobBonusCap>(set))
            {
                if (row.Kind == kind)
                    return row;
            }
            return null;
        }

        public static List<JobBonusCap> Rows(ConfigSet set) => NetIds.Rows<JobBonusCap>(set);
    }
}
