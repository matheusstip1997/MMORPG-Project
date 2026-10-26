using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Static item definition (spec 4.1 implementation, catalog 8.5; backlog G minimal fields). Instances live in the realm
    /// database (<c>item_stacks</c>); only <c>InventoryService</c> creates or changes them (architecture rule 4). M1 has
    /// stackable items only (<see cref="ItemCategories.IsStackable"/>); equipment, gems and crafted stat profiles come
    /// with M2 and M3. TODO(M2): sell value by rarity (R-4.2-02, <c>PriceBase</c>), the non-tradable flag (R-4.1-04).
    /// </summary>
    [Table("ItemDef", 1)]
    public sealed class ItemDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Largest <see cref="NetId"/>: it travels as 16 bits in spawn events and inventory messages.</summary>
        public const int MaxNetId = ushort.MaxValue;

        /// <summary>Stable id, for example <c>m1.demonHorn</c>; stored in <c>item_stacks.def_id</c>, never renamed.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Id on the wire (ground drop spawns, inventory contents); unique, never reused.</summary>
        [Range(1, MaxNetId)] public int NetId { get; set; }

        public ItemCategory Category { get; set; }

        /// <summary>Most units one stack (one bag slot) holds.</summary>
        [Range(1, 9_999)] public int MaxStack { get; set; }

        /// <summary>Look of the item lying on the ground (an <see cref="EntityVisualDef"/> row; placeholder art in M1).</summary>
        [Ref(typeof(EntityVisualDef))] public string VisualId { get; set; } = string.Empty;

        /// <summary>Display name (spec 9.1: user-facing text only from tables).</summary>
        [Ref(typeof(UiStrings))] public string NameId { get; set; } = string.Empty;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!Enum.IsDefined(typeof(ItemCategory), Category))
                errors.Add(nameof(Category), $"{(int)Category} is not an item category");
            else if (!ItemCategories.IsStackable(Category))
                errors.Add(nameof(Category), $"{Category} items are instances, which come after M1 (spec 4.1)");
        }

        public void Validate(ConfigSet set, RowErrors errors) =>
            NetIds.CheckUnique<ItemDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);

        public static ItemDef? Find(ConfigSet set, string id) => NetIds.Find<ItemDef>(set, id, row => row.Id);
    }
}
