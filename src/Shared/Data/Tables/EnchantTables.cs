using System;
using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>
    /// The odds of one enchant attempt by the item's current level (R-5.3-04 "chances per level are config"; spec 5.3 M3
    /// decision 10; WP-M3-8): the weights of +1, no change and -1 in whole percents, summing to 100. One row per level 0 to
    /// 8 (an item at +9 cannot be enchanted further, <c>EnchantMaxed</c>); every level can rise (<see cref="PlusPercent"/>
    /// above 0) and level 0 has no -1 weight (the level never goes below +0). The job tree's <c>enchantOddsPercent</c> moves
    /// percentage points to +1, from -1 first, then from no change (user answer, WP-M3-8 plan). Every number is a
    /// placeholder. Catalog 8.5.
    /// </summary>
    [Table("EnchantOdds", 1)]
    public sealed class EnchantOdds : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Range(0, EnchantLimits.MaxLevel - 1)] public int Level { get; set; }

        [Range(1, 100)] public int PlusPercent { get; set; }

        [Range(0, 100)] public int SamePercent { get; set; }

        [Range(0, 100)] public int MinusPercent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (PlusPercent + SamePercent + MinusPercent != 100)
                errors.Add(nameof(PlusPercent), $"plus, same and minus sum to {PlusPercent + SamePercent + MinusPercent}, not 100");
            if (Level == 0 && MinusPercent != 0)
                errors.Add(nameof(MinusPercent), "level 0 cannot go down (never below +0, R-5.3-04)");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<EnchantOdds>(set);
            if (rows.FindIndex(r => r.Level == Level) != rows.IndexOf(this))
                errors.Add(nameof(Level), $"level {Level} has more than one row");
        }

        public static EnchantOdds? ForLevel(ConfigSet set, int level) =>
            NetIds.Rows<EnchantOdds>(set).Find(r => r.Level == level);
    }

    /// <summary>
    /// The enchant bonus by level (spec 3.4 "enchant bonus is computed from a config table by level (not baked into items)";
    /// spec 5.3 M3 decision 10; WP-M3-8): an item at <see cref="Level"/> adds <see cref="StatPercent"/> percent of each of its
    /// stat lines (the crafted snapshot, or its <c>ItemStatBonusDef</c> rows at its rarity), read at stat time through the
    /// stat source <c>enchant</c>. The percent is the total at that level, not a step; one row per level 1 to 9, never
    /// falling as the level rises. Every number is a placeholder. Catalog 8.5.
    /// </summary>
    [Table("EnchantBonus", 1)]
    public sealed class EnchantBonus : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Range(1, EnchantLimits.MaxLevel)] public int Level { get; set; }

        [Range(0, 1_000)] public int StatPercent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<EnchantBonus>(set);
            foreach (var other in rows)
            {
                if (ReferenceEquals(other, this))
                    continue;
                if (other.Level == Level && rows.IndexOf(other) < rows.IndexOf(this))
                    errors.Add(nameof(Level), $"level {Level} has more than one row");
                else if (other.Level < Level && other.StatPercent > StatPercent)
                    errors.Add(nameof(StatPercent), $"{StatPercent} is below level {other.Level}'s {other.StatPercent}");
            }
        }
    }

    /// <summary>
    /// The material of one enchant attempt by the item's tier (R-5.3-04 "materials are always consumed"; spec 5.3 M3
    /// decision 10; WP-M3-8): <see cref="Count"/> units of <see cref="ItemId"/>, an item tagged <c>enchantStone</c>. The
    /// item's tier is the tier of the <c>MoldDef</c> that makes it, dropped gear tier 1 (user answer, WP-M3-8 plan). One row
    /// per <c>MaterialTierDef</c> tier. Every number is a placeholder. Catalog 8.5.
    /// </summary>
    [Table("EnchantMaterial", 1)]
    public sealed class EnchantMaterial : IValidatableRow, ISetValidatableRow
    {
        /// <summary>The material tag an enchant material carries (<c>MaterialTagDef</c>).</summary>
        public const string StoneTag = "enchantStone";

        [TableId] public string Id { get; set; } = string.Empty;

        [Range(1, MaterialTierDef.MaxTier)] public int Tier { get; set; }

        [Ref(typeof(ItemDef))] public string ItemId { get; set; } = string.Empty;

        [Range(1, 99)] public int Count { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<EnchantMaterial>(set);
            if (rows.FindIndex(r => r.Tier == Tier) != rows.IndexOf(this))
                errors.Add(nameof(Tier), $"tier {Tier} has more than one row");
            var item = NetIds.Find<ItemDef>(set, ItemId, r => r.Id);
            if (item != null && (item.Category != ItemCategory.Material || Array.IndexOf(item.MaterialTags, StoneTag) < 0))
                errors.Add(nameof(ItemId), $"'{ItemId}' is not a material tagged {StoneTag}");
            else if (item != null && Count > item.MaxStack)
                errors.Add(nameof(Count), $"{Count} is above the item's maxStack {item.MaxStack} (one stack pays an attempt)");
        }

        public static EnchantMaterial? ForTier(ConfigSet set, int tier) =>
            NetIds.Rows<EnchantMaterial>(set).Find(r => r.Tier == tier);
    }

    /// <summary>The bounds of R-5.3-04: an item is enchanted from +0 to +9. A rule of the spec, not a tuning number.</summary>
    public static class EnchantLimits
    {
        public const int MaxLevel = 9;
    }

    /// <summary>
    /// DataValidator rules across the enchant tables (spec 5.3 M3 decision 10, WP-M3-8): <see cref="EnchantOdds"/> has every
    /// level 0 to 8 and <see cref="EnchantBonus"/> every level 1 to 9; <see cref="EnchantMaterial"/> has every
    /// <see cref="MaterialTierDef"/> tier; every equipment item has a job (<c>ItemDef.jobId</c>, else the job of the
    /// <see cref="MoldDef"/> that makes it; both agree when both are set), and every such job has its molds at one station
    /// (the station that enchants its items). Skipped quietly per rule when a table is not loaded.
    /// </summary>
    public static class EnchantCoverage
    {
        public static List<ValidationError> Check(ConfigSet set)
        {
            var errors = new List<ValidationError>();
            if (set.TryGet(typeof(EnchantOdds), out _))
            {
                for (int level = 0; level < EnchantLimits.MaxLevel; level++)
                {
                    if (EnchantOdds.ForLevel(set, level) == null)
                        errors.Add(Error(typeof(EnchantOdds), nameof(EnchantOdds.Level), $"no row for level {level}"));
                }
            }
            if (set.TryGet(typeof(EnchantBonus), out _))
            {
                var bonuses = NetIds.Rows<EnchantBonus>(set);
                for (int level = 1; level <= EnchantLimits.MaxLevel; level++)
                {
                    if (bonuses.Find(r => r.Level == level) == null)
                        errors.Add(Error(typeof(EnchantBonus), nameof(EnchantBonus.Level), $"no row for level {level}"));
                }
            }
            if (set.TryGet(typeof(EnchantMaterial), out _))
            {
                foreach (var tier in NetIds.Rows<MaterialTierDef>(set))
                {
                    if (EnchantMaterial.ForTier(set, tier.Tier) == null)
                        errors.Add(Error(typeof(EnchantMaterial), nameof(EnchantMaterial.Tier), $"no row for tier {tier.Tier}"));
                }
            }
            if (set.TryGet(typeof(MoldDef), out var molds) && set.TryGet(typeof(EnchantOdds), out _))
            {
                var stations = new Dictionary<string, NpcInteraction>(StringComparer.Ordinal);
                foreach (var row in molds.Rows)
                {
                    var mold = (MoldDef)row;
                    if (stations.TryGetValue(mold.JobId, out var station) && station != mold.Station)
                        errors.Add(Error(typeof(MoldDef), nameof(MoldDef.Station),
                            $"job '{mold.JobId}' has molds at {station} and {mold.Station}: its items enchant at one station"));
                    stations[mold.JobId] = mold.Station;
                }
                foreach (var item in NetIds.Rows<ItemDef>(set))
                {
                    if (item.Category != ItemCategory.Equipment)
                        continue;
                    string moldJob = string.Empty;
                    foreach (var row in molds.Rows)
                    {
                        var mold = (MoldDef)row;
                        if (mold.OutputItemId == item.Id)
                            moldJob = mold.JobId;
                    }
                    if (item.JobId.Length > 0 && moldJob.Length > 0 && item.JobId != moldJob)
                        errors.Add(Error(typeof(ItemDef), nameof(ItemDef.JobId),
                            $"'{item.Id}' has job '{item.JobId}' but its mold's job is '{moldJob}'"));
                    string job = item.JobId.Length > 0 ? item.JobId : moldJob;
                    if (job.Length == 0)
                        errors.Add(Error(typeof(ItemDef), nameof(ItemDef.JobId),
                            $"equipment '{item.Id}' has no job (jobId, or a mold that makes it): nobody could enchant it"));
                    else if (!stations.ContainsKey(job))
                        errors.Add(Error(typeof(ItemDef), nameof(ItemDef.JobId),
                            $"'{item.Id}' belongs to job '{job}', which has no mold and so no station to enchant at"));
                }
            }
            return errors;
        }

        private static ValidationError Error(Type table, string field, string message) =>
            new ValidationError(ValidationCodes.RuleBroken, TableInfo.For(table).Name, null, field, message);
    }
}
