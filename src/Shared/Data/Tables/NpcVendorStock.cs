namespace Shared.Data.Tables
{
    /// <summary>
    /// What a vendor NPC sells, at a fixed price (R-4.2-01: "low-tier potions, food, starter molds, and basic tools at fixed
    /// prices", a gold sink; the vendor never buys). M2: potions and food (consumables); M3 (WP-M3-2): tools and starter
    /// molds too (<c>moldOrTool</c> instances, routed to the Safe bag equipment area). Stock is unlimited (no rule names a
    /// limit); one purchase is at most one stack (<see cref="ItemDef.MaxStack"/>, so one instance). TODO(open, spec 10.4
    /// [M2]): the real stock and prices; the rows in data/tables/NpcVendorStock.json are placeholders.
    /// </summary>
    [Table("NpcVendorStock", 1)]
    public sealed class NpcVendorStock : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(NpcDef))] public string NpcId { get; set; } = string.Empty;

        [Ref(typeof(ItemDef))] public string ItemId { get; set; } = string.Empty;

        /// <summary>Gold per unit; all of it is destroyed (a sink).</summary>
        [Range(1, 1_000_000_000)] public int PriceGold { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var npc = NpcDef.Find(set, NpcId);
            if (npc != null && !npc.Offers(NpcInteraction.Buy))
                errors.Add(nameof(NpcId), $"NPC '{NpcId}' does not offer buy");
            var item = ItemDef.Find(set, ItemId);
            if (item != null)
            {
                if (item.Category != ItemCategory.Consumable && item.Category != ItemCategory.MoldOrTool)
                    errors.Add(nameof(ItemId), $"'{ItemId}' is {item.Category}; vendors sell potions, food, molds and tools only (R-4.2-01)");
                else if (ContainerDef.FindReceiver(set, item.Category) == null)
                    errors.Add(nameof(ItemId), $"no container receives new {item.Category} items");
            }
            foreach (var other in NetIds.Rows<NpcVendorStock>(set))
            {
                if (!ReferenceEquals(other, this) && other.NpcId == NpcId && other.ItemId == ItemId)
                {
                    errors.Add(nameof(ItemId), $"'{ItemId}' is also sold by '{NpcId}' in row '{other.Id}'");
                    return;
                }
            }
        }
    }
}
