namespace Shared.Data.Tables
{
    /// <summary>
    /// Base outpost sell price of a sell-only item (R-4.2-02: price = base x rarity multiplier x dynamic multiplier). One
    /// row per sell-only <see cref="ItemDef"/> and only those (<see cref="EconomyCoverage"/>). Placeholders in data.
    /// </summary>
    [Table("PriceBase", 1)]
    public sealed class PriceBase : ISetValidatableRow
    {
        /// <summary>The item id.</summary>
        [TableId, Ref(typeof(ItemDef))] public string ItemId { get; set; } = string.Empty;

        /// <summary>Gold for one unit at the rarity multiplier and the dynamic multiplier of 100%.</summary>
        [Range(1, 1_000_000_000)] public int BaseGold { get; set; }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var item = ItemDef.Find(set, ItemId);
            if (item != null && item.Category != ItemCategory.SellOnly)
                errors.Add(nameof(ItemId), $"'{ItemId}' is {item.Category}; only sell-only items are sold at outposts (R-4.1-01)");
        }
    }
}
