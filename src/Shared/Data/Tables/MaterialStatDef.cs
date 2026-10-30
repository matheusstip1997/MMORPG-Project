namespace Shared.Data.Tables
{
    /// <summary>
    /// One stat a material gives to the item crafted from it (R-4.1-01 "give stats to crafts", R-5.3-02 "item stats =
    /// materials used + a quality bonus"; spec 4.1 M3 item model). Flat rows like <see cref="ItemStatBonusDef"/>; the forge
    /// (WP-M3-6) sums them into the crafted item's stats snapshot. Only materials with a tier are craft inputs. A percent is
    /// whole percents of the stat. PLACEHOLDERS (balance deferred). Catalog 8.5.
    /// </summary>
    [Table("MaterialStatDef", 1)]
    public sealed class MaterialStatDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(ItemDef))] public string ItemId { get; set; } = string.Empty;

        /// <summary>A <see cref="StatDef"/> row: a primary attribute of R-3.4-01 or a derived stat.</summary>
        [Ref(typeof(StatDef))] public string StatId { get; set; } = string.Empty;

        [Range(-1_000_000, 1_000_000)] public int Flat { get; set; }

        [Range(-1_000, 1_000)] public int Percent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Flat == 0 && Percent == 0)
                errors.Add(nameof(Flat), "flat and percent are both 0");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var item = ItemDef.Find(set, ItemId);
            if (item != null && (item.Category != ItemCategory.Material || item.Tier == 0))
                errors.Add(nameof(ItemId), $"'{ItemId}' is not a craft material (a material with a tier)");
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
