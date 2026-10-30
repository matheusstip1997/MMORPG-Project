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
    /// it needs. Its stat bonuses are <see cref="ItemStatBonusDef"/> rows (a crafted instance carries its own snapshot in its
    /// properties instead, M3).
    /// </para>
    /// <para>
    /// v3 (spec 4.1 M3 item model, WP-M3-2; every field optional because other branches add rows): a material's
    /// <see cref="Tier"/> on the <see cref="MaterialTierDef"/> ladder and its <see cref="MaterialTags"/> (the mold slot
    /// filters); 0 and no tags is a material that is no craft input yet (the monster drops until decision 6, WP-M3-6). An
    /// equipment row's <see cref="GemSlots"/> and <see cref="JobId"/> (the item's job for enchanting, WP-M3-8). Tools are
    /// <see cref="ToolDef"/> rows of <c>moldOrTool</c> items.
    /// </para>
    /// </summary>
    [Table("ItemDef", 2)]
    public sealed class ItemDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Largest <see cref="NetId"/>: it travels as 16 bits in spawn events and inventory messages.</summary>
        public const int MaxNetId = ushort.MaxValue;

        /// <summary>Most gem slots an equipment row may have (the gem list of an instance travels with this bound).</summary>
        public const int MaxGemSlots = 8;

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

        /// <summary>Materials only: the <see cref="MaterialTierDef"/> tier; 0 for a material that is no craft input yet.</summary>
        [Optional, Range(0, MaterialTierDef.MaxTier)] public int Tier { get; set; }

        /// <summary>Materials with a tier: what kind of input they are (<see cref="MaterialTagDef"/> rows, mold slot filters).</summary>
        [Optional, Ref(typeof(MaterialTagDef))] public string[] MaterialTags { get; set; } = Array.Empty<string>();

        /// <summary>Equipment only: how many gems an instance can hold (R-4.1-01; dropped gear included, decision 4).</summary>
        [Optional, Range(0, MaxGemSlots)] public int GemSlots { get; set; }

        /// <summary>
        /// Equipment only: the job that enchants the item (R-5.3-04 "the item's job"; read by WP-M3-8), empty when none is set.
        /// </summary>
        [Optional, Ref(typeof(JobDef), Optional = true)] public string JobId { get; set; } = string.Empty;

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
            if (Category == ItemCategory.Material)
            {
                if (Tier > 0 && MaterialTags.Length == 0)
                    errors.Add(nameof(MaterialTags), "a material with a tier needs at least one tag (the mold slot filters)");
                else if (Tier == 0 && MaterialTags.Length > 0)
                    errors.Add(nameof(Tier), "a tagged material needs a tier");
                for (int i = 0; i < MaterialTags.Length; i++)
                {
                    if (Array.IndexOf(MaterialTags, MaterialTags[i]) != i)
                        errors.Add(nameof(MaterialTags), $"lists '{MaterialTags[i]}' twice");
                }
            }
            else
            {
                if (Tier != 0)
                    errors.Add(nameof(Tier), "only materials have a tier (tools: ToolDef)");
                if (MaterialTags.Length > 0)
                    errors.Add(nameof(MaterialTags), "only materials have material tags");
            }
            if (Category != ItemCategory.Equipment)
            {
                if (EquipSlots.Length > 0)
                    errors.Add(nameof(EquipSlots), "only equipment fills equipment slots");
                if (WeaponType != WeaponType.None)
                    errors.Add(nameof(WeaponType), "only equipment has a weapon type");
                if (MinLevel > 1)
                    errors.Add(nameof(MinLevel), "only equipment has a minimum level");
                if (GemSlots != 0)
                    errors.Add(nameof(GemSlots), "only equipment has gem slots");
                if (JobId.Length > 0)
                    errors.Add(nameof(JobId), "only equipment has an enchanting job");
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
            if (Tier > 0 && MaterialTierDef.ByTier(set, Tier) == null)
                errors.Add(nameof(Tier), $"{Tier} is not a MaterialTierDef tier");
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
