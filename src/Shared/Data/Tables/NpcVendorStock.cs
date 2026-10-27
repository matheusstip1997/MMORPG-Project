namespace Shared.Data.Tables
{
    /// <summary>
    /// What a vendor NPC sells, at a fixed price (R-4.2-01: a gold sink; the vendor never buys). M2 item model (spec 4.1):
    /// potions and food only, so every item is a consumable. Stock is unlimited (no rule names a limit); one purchase is at
    /// most one stack (<see cref="ItemDef.MaxStack"/>). TODO(open, spec 10.4 [M2]): the real stock and prices; the rows in
    /// data/tables/NpcVendorStock.json are placeholders.
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
                if (item.Category != ItemCategory.Consumable)
                    errors.Add(nameof(ItemId), $"'{ItemId}' is {item.Category}; vendors sell potions and food (consumables) only in M2");
                var bag = ContainerDef.Find(set, ContainerDef.NormalBag);
                if (bag != null && !bag.Accept(item.Category))
                    errors.Add(nameof(ItemId), $"the Normal bag does not take {item.Category} items");
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
