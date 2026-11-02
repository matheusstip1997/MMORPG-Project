namespace Shared.Data.Tables
{
    /// <summary>
    /// One stat a material gives to the item forged from it (R-4.1-01 "give stats to crafts", R-5.3-02 "item stats =
    /// materials used + a quality bonus"; spec 4.1 M3 item model): a range per unit (version 2, WP-M3-14, spec 5.3 M3
    /// decision 13). The forge places the roll inside the range by the craft's quality (<c>CraftedStatsCalc.Position</c>)
    /// and sums the lines into the crafted item's stats snapshot. Flat rows like <see cref="ItemStatBonusDef"/>; only forge
    /// inputs (<see cref="MaterialTagDef.IsForgeInput"/>) have rows, and every forge input has at least one
    /// (<see cref="ForgeCoverage"/>). A percent is whole percents of the stat. PLACEHOLDERS (balance deferred). Catalog 8.5.
    /// </summary>
    [Table("MaterialStatDef", 2)]
    public sealed class MaterialStatDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(ItemDef))] public string ItemId { get; set; } = string.Empty;

        /// <summary>A <see cref="StatDef"/> row: a primary attribute of R-3.4-01 or a derived stat.</summary>
        [Ref(typeof(StatDef))] public string StatId { get; set; } = string.Empty;

        /// <summary>The flat value per unit at quality 0.</summary>
        [Range(-1_000_000, 1_000_000)] public int FlatMin { get; set; }

        /// <summary>The flat value per unit at <c>ForgeSettings.statRangeQuality</c> and above.</summary>
        [Range(-1_000_000, 1_000_000)] public int FlatMax { get; set; }

        [Range(-1_000, 1_000)] public int PercentMin { get; set; }

        [Range(-1_000, 1_000)] public int PercentMax { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (FlatMin > FlatMax)
                errors.Add(nameof(FlatMax), $"{FlatMax} is below flatMin {FlatMin}");
            if (PercentMin > PercentMax)
                errors.Add(nameof(PercentMax), $"{PercentMax} is below percentMin {PercentMin}");
            if (FlatMin == 0 && FlatMax == 0 && PercentMin == 0 && PercentMax == 0)
                errors.Add(nameof(FlatMax), "the flat and percent ranges are both 0");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var item = ItemDef.Find(set, ItemId);
            if (item != null && (item.Category != ItemCategory.Material || item.Tier == 0))
                errors.Add(nameof(ItemId), $"'{ItemId}' is not a craft material (a material with a tier)");
            else if (item != null && set.TryGet(typeof(MaterialTagDef), out _) && !MaterialTagDef.IsForgeInput(set, item))
                errors.Add(nameof(ItemId), $"'{ItemId}' has a tag that is no forge input (MaterialTagDef.forgeInput false)");
            foreach (var other in NetIds.Rows<MaterialStatDef>(set))
            {
                if (ReferenceEquals(other, this))
                    break;
                if (other.ItemId == ItemId && other.StatId == StatId)
                {
                    errors.Add(nameof(StatId), $"'{ItemId}' already has a '{StatId}' row ('{other.Id}')");
                    break;
                }
            }
        }
    }
}
