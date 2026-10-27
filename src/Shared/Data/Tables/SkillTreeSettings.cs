namespace Shared.Data.Tables
{
    /// <summary>
    /// The skill-tree numbers (spec 3.5 "M2 wave 1 decisions", R-3.5-04; user decision 2026-10-27, every number a
    /// placeholder until the balance phase). <c>SkillTreeGen</c> builds <see cref="SkillTree"/> from them: max level
    /// <see cref="DefaultMaxLevel"/> per node (weapon unlocks <see cref="WeaponUnlockMaxLevel"/>), <see cref="PointCostPerLevel"/>
    /// points per node level, and a node's minimum level is its class's unlock level plus a step per design row
    /// (<see cref="RootMinLevelPerRow"/> on a root tree, <see cref="TierMinLevelPerRow"/> on later trees) unless the design
    /// file sets one.
    /// </summary>
    [Table("SkillTreeSettings", 1)]
    [Singleton]
    public sealed class SkillTreeSettings
    {
        [Range(1, SkillTree.MaxLevelCap)] public int DefaultMaxLevel { get; set; }

        [Range(1, SkillTree.MaxLevelCap)] public int WeaponUnlockMaxLevel { get; set; }

        [Range(1, 1_000)] public int PointCostPerLevel { get; set; }

        /// <summary>Levels per design row on a root tree (placeholder 4: rows 0-4 at levels 1, 5, 9, 13, 17).</summary>
        [Range(0, 100)] public int RootMinLevelPerRow { get; set; }

        /// <summary>Levels per design row on a tier-1 or later tree (placeholder 5: rows 0-4 at levels 30 to 50).</summary>
        [Range(0, 100)] public int TierMinLevelPerRow { get; set; }

        public static SkillTreeSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(SkillTreeSettings), out var table) && table.Rows.Count == 1 ? (SkillTreeSettings)table.Rows[0] : null;

        /// <summary>The minimum level of a node on <paramref name="row"/> of a tree whose class unlocks at <paramref name="unlockLevel"/>.</summary>
        public int MinLevel(int tier, int unlockLevel, int row) =>
            unlockLevel + (tier == 0 ? RootMinLevelPerRow : TierMinLevelPerRow) * row;
    }
}
