namespace Shared.Data.Tables
{
    /// <summary>
    /// One stat bonus of an equipment <see cref="ItemDef"/> row (spec 4.1 M2 item model: "a fixed list of stat bonuses (stat
    /// id, flat or percent)"; catalog 8.5). Flat rows because config tables hold no object arrays. The values are those of a
    /// common instance; an instance of another rarity gets them times <see cref="RarityDef.StatPercent"/> / 100
    /// (<c>Shared.Simulation.Items.ItemStatBonus.Scale</c>). A percent bonus is whole percents of the stat. PLACEHOLDERS.
    /// </summary>
    [Table("ItemStatBonusDef", 1)]
    public sealed class ItemStatBonusDef : IValidatableRow, ISetValidatableRow
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
            if (item != null && item.Category != ItemCategory.Equipment)
                errors.Add(nameof(ItemId), $"'{ItemId}' is {item.Category}; only equipment has stat bonuses");
            foreach (var other in NetIds.Rows<ItemStatBonusDef>(set))
            {
                if (ReferenceEquals(other, this))
                    break;
                if (other.ItemId == ItemId && other.StatId == StatId)
                {
                    errors.Add(nameof(StatId), $"'{ItemId}' already has a '{StatId}' bonus ('{other.Id}')");
                    break;
                }
            }
        }
    }
}
