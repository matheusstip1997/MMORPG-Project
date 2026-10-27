using System;

namespace Shared.Data.Tables
{
    /// <summary>What a container is (spec 4.1 M2 item model). Config enum (camelCase names): append only.</summary>
    public enum ContainerKind : byte
    {
        /// <summary>A bag of slots (Normal bag, Safe bag areas, Quest bag).</summary>
        Bag = 0,
        /// <summary>The equipped set: one slot per <see cref="EquipSlotDef"/> row, slot index = its code (R-4.1-02 "Equipped").</summary>
        Equipped = 1,
    }

    /// <summary>
    /// A character container (R-4.1-02; catalog 8.5): its slot count and which item categories it accepts. M2 (spec 4.1 M2
    /// item model): the Normal bag, the Safe bag equipment area and valuables area, the Quest bag, and Equipped. A new item
    /// (a pickup, a grant) goes to the one container with <see cref="ReceivesNewItems"/> that accepts its category; the
    /// others fill only by the player's moves. The tab name shown is <c>UiStrings</c> <c>inv.container.&lt;id&gt;</c>.
    /// TODO(M4): which containers drop on PvE death (R-4.3-03: the Normal bag only).
    /// </summary>
    [Table("ContainerDef", 2)]
    public sealed class ContainerDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Row id of the Normal bag (the M1 bag; the old single-bag API works on it).</summary>
        public const string NormalBag = "normalBag";

        /// <summary>Largest slot count; a slot index travels as one byte in inventory messages.</summary>
        public const int MaxSlots = 200;

        /// <summary>Most containers a character has (inventory messages carry them in one array).</summary>
        public const int MaxContainers = 16;

        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Stored in <c>item_stacks.container</c> and <c>item_instances.container</c>; unique, never renumbered.</summary>
        [Range(1, 255)] public int Code { get; set; }

        [Range(1, MaxSlots)] public int Slots { get; set; }

        /// <summary>Item categories it takes (R-4.1-02: the Normal bag takes materials, sell-only items, consumables, gems).</summary>
        public ItemCategory[] Accepts { get; set; } = Array.Empty<ItemCategory>();

        [Optional] public ContainerKind Kind { get; set; }

        /// <summary>New items of the categories it accepts land here (at most one such container per category).</summary>
        [Optional] public bool ReceivesNewItems { get; set; }

        public bool Accept(ItemCategory category) => Array.IndexOf(Accepts, category) >= 0;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Accepts.Length == 0)
                errors.Add(nameof(Accepts), "must list at least one item category");
            for (int i = 0; i < Accepts.Length; i++)
            {
                if (!Enum.IsDefined(typeof(ItemCategory), Accepts[i]))
                    errors.Add(nameof(Accepts), $"{(int)Accepts[i]} is not an item category");
                else if (Array.IndexOf(Accepts, Accepts[i]) != i)
                    errors.Add(nameof(Accepts), $"lists {Accepts[i]} twice");
            }
            if (!Enum.IsDefined(typeof(ContainerKind), Kind))
                errors.Add(nameof(Kind), $"{(int)Kind} is not a container kind");
            else if (Kind == ContainerKind.Equipped)
            {
                if (Accepts.Length != 1 || Accepts[0] != ItemCategory.Equipment)
                    errors.Add(nameof(Accepts), "the Equipped container takes equipment only");
                if (ReceivesNewItems)
                    errors.Add(nameof(ReceivesNewItems), "new items never go straight to Equipped");
                if (Slots > EquipSlotDef.MaxSlots)
                    errors.Add(nameof(Slots), $"at most {EquipSlotDef.MaxSlots} equipment slots");
            }
        }

        /// <summary>Unique codes, at most one Equipped container, at most one receiving container per category.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            int count = 0;
            foreach (var other in NetIds.Rows<ContainerDef>(set))
            {
                count++;
                if (ReferenceEquals(other, this))
                    continue;
                if (other.Code == Code)
                    errors.Add(nameof(Code), $"{Code} is also used by '{other.Id}'");
                if (Kind == ContainerKind.Equipped && other.Kind == ContainerKind.Equipped)
                    errors.Add(nameof(Kind), $"'{other.Id}' is also an Equipped container");
                if (ReceivesNewItems && other.ReceivesNewItems)
                {
                    foreach (var category in Accepts)
                    {
                        if (other.Accept(category))
                            errors.Add(nameof(ReceivesNewItems), $"new {category} items would go here and to '{other.Id}'");
                    }
                }
            }
            if (count > MaxContainers)
                errors.Add(nameof(Id), $"at most {MaxContainers} containers");
        }

        public static ContainerDef? Find(ConfigSet set, string id) => NetIds.Find<ContainerDef>(set, id, row => row.Id);

        /// <summary>The Equipped container, or null.</summary>
        public static ContainerDef? FindEquipped(ConfigSet set)
        {
            foreach (var row in NetIds.Rows<ContainerDef>(set))
            {
                if (row.Kind == ContainerKind.Equipped)
                    return row;
            }
            return null;
        }

        /// <summary>The container new items of <paramref name="category"/> go to, or null when none takes them.</summary>
        public static ContainerDef? FindReceiver(ConfigSet set, ItemCategory category)
        {
            foreach (var row in NetIds.Rows<ContainerDef>(set))
            {
                if (row.ReceivesNewItems && row.Accept(category))
                    return row;
            }
            return null;
        }
    }
}
