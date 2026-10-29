using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Static item definition (spec 4.1 implementation, catalog 8.5). Stackables live in the realm database as
    /// <c>item_stacks</c>, instances (<see cref="ItemCategories.IsInstance"/>: equipment, molds and tools) as
    /// <c>item_instances</c>; only <c>InventoryService</c> creates or changes them (architecture rule 4).
    /// <para>
    /// Equipment (spec 4.1 M2 item model, R-3.5-07): <see cref="EquipSlots"/> are the slots it fills (a dual weapon both
    /// weapon slots), <see cref="WeaponType"/> its weapon type when it fills a weapon slot, <see cref="MinLevel"/> the level
    /// it needs. Its stat bonuses are <see cref="ItemStatBonusDef"/> rows. Crafted stat profiles, gems and enchants come with
    /// M3. TODO(M2): sell value by rarity (R-4.2-02, <c>PriceBase</c>, WP-M2-4).
    /// </para>
    /// </summary>
    [Table("ItemDef", 2)]
    public sealed class ItemDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Largest <see cref="NetId"/>: it travels as 16 bits in spawn events and inventory messages.</summary>
        public const int MaxNetId = ushort.MaxValue;

        /// <summary>Stable id, for example <c>m1.demonHorn</c>; stored in <c>item_stacks.def_id</c>, never renamed.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Id on the wire (ground drop spawns, inventory contents); unique, never reused.</summary>
        [Range(1, MaxNetId)] public int NetId { get; set; }

        public ItemCategory Category { get; set; }

        /// <summary>Most units one stack (one bag slot) holds; 1 for instances.</summary>
        [Range(1, 9_999)] public int MaxStack { get; set; }

        /// <summary>Look of the item lying on the ground (an <see cref="EntityVisualDef"/> row; placeholder art).</summary>
        [Ref(typeof(EntityVisualDef))] public string VisualId { get; set; } = string.Empty;

        /// <summary>Display name (spec 9.1: user-facing text only from tables).</summary>
        [Ref(typeof(UiStrings))] public string NameId { get; set; } = string.Empty;

        /// <summary>Equipment only: the <see cref="EquipSlotDef"/> rows it fills when equipped.</summary>
        [Optional, Ref(typeof(EquipSlotDef))] public string[] EquipSlots { get; set; } = Array.Empty<string>();

        /// <summary>Equipment filling a weapon slot: its type (R-3.5-07); <see cref="Tables.WeaponType.None"/> otherwise.</summary>
        [Optional] public WeaponType WeaponType { get; set; }

        /// <summary>Equipment only: the character level needed to equip it; 0 or 1 for none.</summary>
        [Optional, Range(0, 1_000)] public int MinLevel { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!Enum.IsDefined(typeof(ItemCategory), Category))
            {
                errors.Add(nameof(Category), $"{(int)Category} is not an item category");
                return;
            }
            if (ItemCategories.IsInstance(Category) && MaxStack != 1)
                errors.Add(nameof(MaxStack), $"{Category} items are instances: maxStack must be 1");
            if (!Enum.IsDefined(typeof(WeaponType), WeaponType))
                errors.Add(nameof(WeaponType), $"{(int)WeaponType} is not a weapon type");
            if (Category != ItemCategory.Equipment)
            {
                if (EquipSlots.Length > 0)
                    errors.Add(nameof(EquipSlots), "only equipment fills equipment slots");
                if (WeaponType != WeaponType.None)
                    errors.Add(nameof(WeaponType), "only equipment has a weapon type");
                if (MinLevel > 1)
                    errors.Add(nameof(MinLevel), "only equipment has a minimum level");
                return;
            }
            if (EquipSlots.Length == 0)
                errors.Add(nameof(EquipSlots), "equipment must fill at least one slot");
            for (int i = 0; i < EquipSlots.Length; i++)
            {
                if (Array.IndexOf(EquipSlots, EquipSlots[i]) != i)
                    errors.Add(nameof(EquipSlots), $"lists '{EquipSlots[i]}' twice");
            }
        }

        /// <summary>
        /// The net id is unique; a weapon slot needs a weapon type and a weapon type a weapon slot; a dual weapon fills both
        /// weapon slots and nothing else, any other weapon one slot (R-3.5-07).
        /// </summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            NetIds.CheckUnique<ItemDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);
            if (Category != ItemCategory.Equipment || EquipSlots.Length == 0)
                return;
            int weaponSlots = 0, weaponSlotsInTable = 0;
            foreach (var slotId in EquipSlots)
            {
                var slot = EquipSlotDef.Find(set, slotId);
                if (slot == null)
                    return; // the reference check reports it
                if (slot.IsWeapon)
                    weaponSlots++;
            }
            foreach (var slot in NetIds.Rows<EquipSlotDef>(set))
            {
                if (slot.IsWeapon)
                    weaponSlotsInTable++;
            }
            if (weaponSlots > 0 && WeaponType == WeaponType.None)
                errors.Add(nameof(WeaponType), "fills a weapon slot, so it must have a weapon type");
            else if (weaponSlots == 0 && WeaponType != WeaponType.None)
                errors.Add(nameof(EquipSlots), $"a {WeaponType} must fill a weapon slot");
            else if (WeaponTypes.IsDual(WeaponType) && (weaponSlots != weaponSlotsInTable || EquipSlots.Length != weaponSlots))
                errors.Add(nameof(EquipSlots), $"a {WeaponType} is one item that fills both weapon slots and nothing else (R-3.5-07)");
            else if (WeaponType != WeaponType.None && !WeaponTypes.IsDual(WeaponType) && EquipSlots.Length != 1)
                errors.Add(nameof(EquipSlots), $"a {WeaponType} fills one weapon slot (only dual types fill both, R-3.5-07)");
        }

        public static ItemDef? Find(ConfigSet set, string id) => NetIds.Find<ItemDef>(set, id, row => row.Id);
    }
}
