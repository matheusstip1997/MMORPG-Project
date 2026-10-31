namespace Shared.Data.Tables
{
    /// <summary>
    /// The material tier ladder (spec 4.1 M3 item model, user decision 2026-10-30): three tiers in M3, placed over the field
    /// rings (T1 rings 1-2, T2 ring 3, T3 ring 4). <see cref="ItemDef.Tier"/> on materials and <see cref="ToolDef.Tier"/> name
    /// a row; resource nodes (WP-M3-4) may too. Tiers run from 1 without gaps. Catalog 8.5.
    /// </summary>
    [Table("MaterialTierDef", 1)]
    public sealed class MaterialTierDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Most tiers the ladder may have.</summary>
        public const int MaxTier = 99;

        [TableId] public string Id { get; set; } = string.Empty;

        [Range(1, MaxTier)] public int Tier { get; set; }

        /// <summary>Display name, for example "Tier 1" (spec 9.1: user-facing text only from tables).</summary>
        [Ref(typeof(UiStrings))] public string NameId { get; set; } = string.Empty;

        /// <summary>The quality number each unit of a material of this tier adds to a forge craft (R-5.3-02 "material numbers"; WP-M3-6).</summary>
        [Optional, Range(0, 10_000)] public int ForgeQuality { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        /// <summary>Tiers are unique and contiguous from 1.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            int count = 0;
            foreach (var other in NetIds.Rows<MaterialTierDef>(set))
            {
                count++;
                if (!ReferenceEquals(other, this) && other.Tier == Tier)
                    errors.Add(nameof(Tier), $"{Tier} is also used by '{other.Id}'");
            }
            if (Tier > count)
                errors.Add(nameof(Tier), $"{Tier} leaves a gap: tiers run from 1 to {count}");
        }

        /// <summary>The row of a tier, or null.</summary>
        public static MaterialTierDef? ByTier(ConfigSet set, int tier)
        {
            foreach (var row in NetIds.Rows<MaterialTierDef>(set))
            {
                if (row.Tier == tier)
                    return row;
            }
            return null;
        }
    }
}
