using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// The kind of an equipment gem (R-5.3-05; M3 decision 11): each kind has one stat row per tier. Config enum (camelCase
    /// names): append only, never renumber. Equipment gems only, never the Star Gems of 14.1 (R-14.1-04: their own family,
    /// a future <c>StarGemDef</c>, M6).
    /// </summary>
    public enum GemKind : byte
    {
        None = 0,
        Ruby = 1,
        Sapphire = 2,
        Emerald = 3,
    }

    /// <summary>
    /// A cut equipment gem (R-5.3-05, R-5.3-06; M3 decision 11, WP-M3-9): the gem item (a material tagged <c>gem</c>, cut by
    /// the Jeweler's refine recipe), its kind and tier, and the one stat it gives the item it is socketed in, read at stat
    /// time through the stat source <c>Gem</c> (spec 3.4). Tiers 1 to 3, one row per kind and tier. Never a Star Gem
    /// (R-14.1-04). PLACEHOLDERS (balance deferred). Catalog 8.5.
    /// </summary>
    [Table("GemDef", 1)]
    public sealed class GemDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>The gem item's <see cref="ItemDef"/> id.</summary>
        [TableId, Ref(typeof(ItemDef))] public string Id { get; set; } = string.Empty;

        public GemKind Kind { get; set; }

        /// <summary>The gem's tier: its item's <see cref="ItemDef.Tier"/>, with a <see cref="GemSocketOdds"/> row.</summary>
        [Range(1, MaterialTierDef.MaxTier)] public int Tier { get; set; }

        [Ref(typeof(StatDef))] public string StatId { get; set; } = string.Empty;

        [Range(-1_000_000, 1_000_000)] public int Flat { get; set; }

        [Range(-1_000, 1_000)] public int Percent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Kind == GemKind.None || !Enum.IsDefined(typeof(GemKind), Kind))
                errors.Add(nameof(Kind), $"{(int)Kind} is not a gem kind");
            if (Flat == 0 && Percent == 0)
                errors.Add(nameof(Flat), "flat and percent are both 0");
        }

        /// <summary>The item is a <c>gem</c>-tagged material of this tier; one row per (kind, tier); the tier has odds.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            var item = ItemDef.Find(set, Id);
            if (item != null)
            {
                if (item.Category != ItemCategory.Material || Array.IndexOf(item.MaterialTags, GemTag) < 0)
                    errors.Add(nameof(Id), $"'{Id}' is not a material tagged '{GemTag}'");
                else if (item.Tier != Tier)
                    errors.Add(nameof(Tier), $"{Tier} is not the tier of '{Id}' ({item.Tier})");
            }
            if (set.TryGet(typeof(GemSocketOdds), out _) && GemSocketOdds.ForTier(set, Tier) == null)
                errors.Add(nameof(Tier), $"tier {Tier} has no GemSocketOdds row");
            foreach (var other in NetIds.Rows<GemDef>(set))
            {
                if (ReferenceEquals(other, this))
                    break;
                if (other.Kind == Kind && other.Tier == Tier)
                {
                    errors.Add(nameof(Kind), $"{Kind} tier {Tier} is also '{other.Id}'");
                    break;
                }
            }
        }

        /// <summary>The material tag of cut gems (<see cref="MaterialTagDef"/>); rough gems are <c>roughGem</c>.</summary>
        public const string GemTag = "gem";

        public static GemDef? Find(ConfigSet set, string itemId) => NetIds.Find<GemDef>(set, itemId, row => row.Id);
    }

    /// <summary>
    /// Socketing and removal per gem tier (R-5.3-05; M3 decision 11, WP-M3-9): the socket success chance (a failure
    /// consumes the gem and leaves the item untouched), the Jeweler level the tier needs, and the removal cost (user answer
    /// 2026-10-31: rough gems of the gem's tier, lowered by the job tree's <c>removalCostPercent</c>). The job tree's
    /// <c>socketOddsPercent</c> adds to the chance, capped by <c>JobBonusCap</c>. PLACEHOLDERS. Catalog 8.5.
    /// </summary>
    [Table("GemSocketOdds", 1)]
    public sealed class GemSocketOdds : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Range(1, MaterialTierDef.MaxTier)] public int Tier { get; set; }

        /// <summary>The job whose service socketing and removal are (R-5.3-05: the Jeweler).</summary>
        [Ref(typeof(JobDef))] public string JobId { get; set; } = string.Empty;

        [Range(1, 100)] public int SuccessPercent { get; set; }

        /// <summary>The Jeweler level that may socket and remove gems of this tier (<c>JobLevelTooLow</c> below).</summary>
        [Range(1, 1_000)] public int MinJobLevel { get; set; }

        /// <summary>A stackable material consumed by a removal.</summary>
        [Ref(typeof(ItemDef))] public string RemovalCostItemId { get; set; } = string.Empty;

        [Range(1, 99)] public int RemovalCostCount { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        /// <summary>One row per tier, tiers on the ladder and contiguous from 1, a material cost within one stack.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (set.TryGet(typeof(MaterialTierDef), out _) && MaterialTierDef.ByTier(set, Tier) == null)
                errors.Add(nameof(Tier), $"{Tier} is not a MaterialTierDef tier");
            var cost = ItemDef.Find(set, RemovalCostItemId);
            if (cost != null && cost.Category != ItemCategory.Material)
                errors.Add(nameof(RemovalCostItemId), $"'{RemovalCostItemId}' is {cost.Category}; the removal cost is a material");
            else if (cost != null && RemovalCostCount > cost.MaxStack)
                errors.Add(nameof(RemovalCostCount), $"{RemovalCostCount} is above one stack of '{RemovalCostItemId}' ({cost.MaxStack})");
            bool below = Tier == 1;
            foreach (var other in NetIds.Rows<GemSocketOdds>(set))
            {
                if (ReferenceEquals(other, this))
                    continue;
                if (other.Tier == Tier)
                    errors.Add(nameof(Tier), $"{Tier} is also the tier of '{other.Id}'");
                if (other.Tier == Tier - 1)
                    below = true;
            }
            if (!below)
                errors.Add(nameof(Tier), $"tier {Tier} has no row for tier {Tier - 1} (tiers contiguous from 1)");
        }

        public static GemSocketOdds? ForTier(ConfigSet set, int tier)
        {
            foreach (var row in NetIds.Rows<GemSocketOdds>(set))
            {
                if (row.Tier == tier)
                    return row;
            }
            return null;
        }
    }

    /// <summary>
    /// DataValidator rule across the gem tables (WP-M3-9): every item tagged <c>gem</c> has exactly one
    /// <see cref="GemDef"/> row (a cut gem without one could never be socketed). Skipped quietly when a table is not loaded.
    /// </summary>
    public static class GemCoverage
    {
        public static System.Collections.Generic.List<ValidationError> Check(ConfigSet set)
        {
            var errors = new System.Collections.Generic.List<ValidationError>();
            if (!set.TryGet(typeof(GemDef), out _) || !set.TryGet(typeof(ItemDef), out _))
                return errors;
            foreach (var item in NetIds.Rows<ItemDef>(set))
            {
                if (Array.IndexOf(item.MaterialTags, GemDef.GemTag) >= 0 && GemDef.Find(set, item.Id) == null)
                    errors.Add(new ValidationError(ValidationCodes.RuleBroken, TableInfo.For(typeof(GemDef)).Name, null,
                        nameof(GemDef.Id), $"no row for gem item '{item.Id}'"));
            }
            return errors;
        }
    }
}
