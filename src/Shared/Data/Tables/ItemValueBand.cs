namespace Shared.Data.Tables
{
    /// <summary>
    /// The gold value of one unit of an item for the large-transfer flags and the report's trade value (spec 4.5 R-4.5-02, M4
    /// decision 17, user answer 2026-11-15; WP-M4-15): a value per item category and tier, times the item's
    /// <see cref="RarityMult"/> percent. An item takes its category's row with the highest tier not above its own
    /// (<c>ItemDef.tier</c>, 0 when it has none); every tradable category has a tier-0 row (<see cref="EconomyCoverage"/>), so
    /// every tradable item has a value. A sell-only item uses its <see cref="PriceBase"/> instead (it is never traded or listed,
    /// R-4.5-01). Never a price: nothing is bought or sold at this value. Placeholders in data (balance is deferred).
    /// </summary>
    [Table("ItemValueBand", 1)]
    public sealed class ItemValueBand : IValidatableRow, ISetValidatableRow
    {
        /// <summary>The categories a trade or a listing can move (R-4.5-01 refuses sell-only and quest items): each needs a tier-0 row.</summary>
        public static readonly ItemCategory[] Tradable =
        {
            ItemCategory.Material, ItemCategory.Equipment, ItemCategory.Consumable, ItemCategory.MoldOrTool,
        };

        /// <summary>The row's id, by convention <c>&lt;category&gt;.t&lt;tier&gt;</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>The item category the row values (not <c>sellOnly</c>: those use <see cref="PriceBase"/>).</summary>
        public ItemCategory Category { get; set; }

        /// <summary>The lowest item tier the row applies to.</summary>
        [Range(0, MaterialTierDef.MaxTier)] public int Tier { get; set; }

        /// <summary>Gold per unit at the rarity percent of 100.</summary>
        [Range(0, 1_000_000_000)] public int GoldPerUnit { get; set; }

        public void Validate(RowErrors errors)
        {
            if (Category == ItemCategory.SellOnly)
                errors.Add(nameof(Category), "sell-only items take their value from PriceBase");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (!set.TryGet(typeof(ItemValueBand), out var table))
                return;
            foreach (var row in table.Rows)
            {
                var other = (ItemValueBand)row;
                if (!ReferenceEquals(other, this) && other.Category == Category && other.Tier == Tier)
                    errors.Add(nameof(Tier), $"'{other.Id}' values {Category} tier {Tier} too");
            }
        }
    }
}
