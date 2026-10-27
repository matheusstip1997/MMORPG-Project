using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// What an achievement counter counts (R-3.6-05). The list is code (each category has its own source); the counters
    /// themselves are data. Stored by name in <c>characters.achievement_counters</c>: append only, never rename.
    /// </summary>
    public enum AchievementCategory
    {
        /// <summary>Every monster the character killed (<c>Killed</c>).</summary>
        MonsterKills = 0,
        /// <summary>Monster types killed at least once (the per-type counts of <c>monster_kill_counts</c>, R-14.2-01).</summary>
        MonsterTypesKilled = 1,
        /// <summary>Kills of monsters with <c>MonsterDef.boss</c>.</summary>
        BossKills = 2,
        /// <summary>Outpost runs claimed (<c>RunClaimed</c>, WP-M2-6).</summary>
        RunsClaimed = 3,
        /// <summary>Outposts visited (<c>OutpostVisited</c>, WP-M2-4).</summary>
        OutpostsVisited = 4,
    }

    /// <summary>How a counter grows: every event adds one, or only the first event per key (an outpost, a monster type).</summary>
    public enum AchievementKind
    {
        Count = 0,
        Distinct = 1,
    }

    /// <summary>
    /// Achievement counters v0 (spec 3.6, WP-M2-3): one counter per category, kept per character by
    /// <c>AchievementService</c> and checked by <see cref="RankRequirements"/>. Monster kills and boss kills count every
    /// kill; monster types killed is distinct by its nature; runs and outposts take the kind from the row (placeholder:
    /// outposts visited is distinct, user decision 2026-10-27).
    /// </summary>
    [Table("AchievementDef", 1)]
    public sealed class AchievementDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        public AchievementCategory Category { get; set; }

        public AchievementKind Kind { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!Enum.IsDefined(typeof(AchievementCategory), Category))
                errors.Add(nameof(Category), $"{(int)Category} is not an achievement category");
            if (!Enum.IsDefined(typeof(AchievementKind), Kind))
                errors.Add(nameof(Kind), $"{(int)Kind} is not an achievement kind");
            else if (Category == AchievementCategory.MonsterTypesKilled && Kind != AchievementKind.Distinct)
                errors.Add(nameof(Kind), "monster types killed counts each type once: distinct");
            else if ((Category == AchievementCategory.MonsterKills || Category == AchievementCategory.BossKills) && Kind != AchievementKind.Count)
                errors.Add(nameof(Kind), $"{Category} counts every kill: count");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<AchievementDef>(set);
            if (rows.FindIndex(r => r.Category == Category) != rows.IndexOf(this))
                errors.Add(nameof(Category), $"{Category} has more than one counter (v0: one per category)");
        }

        public static AchievementDef? Find(ConfigSet set, string id) => NetIds.Find<AchievementDef>(set, id, row => row.Id);
    }
}
