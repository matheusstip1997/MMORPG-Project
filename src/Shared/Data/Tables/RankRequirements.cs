using System;
using System.Collections.Generic;
using Shared.Simulation.Stats;

namespace Shared.Data.Tables
{
    /// <summary>
    /// What a rank-up needs (R-3.6-04): every row of the character's current rank must be met by its achievement counter.
    /// Each rank from F to S has rows from at least two achievement categories; SS is the top and has none. Checked at an
    /// Adventurer Guild outpost by <c>RankService.TryRankUp</c>. Placeholders (user decision 2026-10-27): F to E, E to D and D
    /// to C are meant to be reachable by level 30; the later ranks are filler rows.
    /// </summary>
    [Table("RankRequirements", 1)]
    public sealed class RankRequirements : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Categories each rank-up needs at least (R-3.6-04 "minimum number and categories"; user decision 2026-10-27).</summary>
        public const int MinCategoriesPerRank = 2;

        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>The rank this row is a requirement to leave (F: the F to E rank-up).</summary>
        public Rank FromRank { get; set; }

        [Ref(typeof(AchievementDef))] public string AchievementId { get; set; } = string.Empty;

        [Range(1, 1_000_000_000)] public int Minimum { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!Enum.IsDefined(typeof(Rank), FromRank))
                errors.Add(nameof(FromRank), $"{(int)FromRank} is not a rank");
            else if (FromRank == Rank.SS)
                errors.Add(nameof(FromRank), "SS is the top rank: there is no rank-up from it");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<RankRequirements>(set);
            int index = rows.IndexOf(this);
            for (int i = 0; i < index; i++)
            {
                if (rows[i].FromRank == FromRank && rows[i].AchievementId == AchievementId)
                    errors.Add(nameof(AchievementId), $"'{AchievementId}' is required twice from rank {FromRank}");
            }
            if (index != 0 || !set.TryGet(typeof(AchievementDef), out _))
                return;
            // Once per table: every rank below SS has requirements from at least two categories.
            for (int r = 0; r < Ranks.Count - 1; r++)
            {
                var categories = new HashSet<AchievementCategory>();
                foreach (var row in rows)
                {
                    if ((int)row.FromRank == r && AchievementDef.Find(set, row.AchievementId) is { } achievement)
                        categories.Add(achievement.Category);
                }
                if (categories.Count < MinCategoriesPerRank)
                    errors.Add(nameof(FromRank), $"the rank-up from {(Rank)r} needs rows from at least {MinCategoriesPerRank} achievement categories (R-3.6-04), has {categories.Count}");
            }
        }
    }
}
