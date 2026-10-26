using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// A character container (R-4.1-02; catalog 8.5): its slot count and which item categories it accepts. M1 (backlog G)
    /// has the Normal bag only (<see cref="NormalBag"/>). TODO(M2): the Safe bag areas, the Quest bag, Equipped.
    /// </summary>
    [Table("ContainerDef", 1)]
    public sealed class ContainerDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Row id of the Normal bag, the only container in M1.</summary>
        public const string NormalBag = "normalBag";

        /// <summary>Largest slot count; a slot index travels as one byte in inventory messages.</summary>
        public const int MaxSlots = 200;

        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Stored in <c>item_stacks.container</c>; unique, never renumbered.</summary>
        [Range(0, 255)] public int Code { get; set; }

        [Range(1, MaxSlots)] public int Slots { get; set; }

        /// <summary>Item categories it takes (R-4.1-02: the Normal bag takes materials, sell-only items, consumables, gems).</summary>
        public ItemCategory[] Accepts { get; set; } = Array.Empty<ItemCategory>();

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
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            foreach (var other in NetIds.Rows<ContainerDef>(set))
            {
                if (!ReferenceEquals(other, this) && other.Code == Code)
                {
                    errors.Add(nameof(Code), $"{Code} is also used by '{other.Id}'");
                    return;
                }
            }
        }

        public static ContainerDef? Find(ConfigSet set, string id) => NetIds.Find<ContainerDef>(set, id, row => row.Id);
    }
}
