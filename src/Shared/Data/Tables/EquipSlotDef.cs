namespace Shared.Data.Tables
{
    /// <summary>
    /// One equipment slot (spec 4.1 M2 item model, R-3.5-07; catalog 8.5): <c>weaponMain</c>, <c>weaponSecondary</c>,
    /// <c>head</c>, <c>chest</c>, <c>legs</c>, <c>feet</c>, <c>ring</c>, <c>amulet</c>. A later slot is a row. <see cref="Code"/>
    /// is the slot index inside the Equipped container (<see cref="ContainerKind.Equipped"/>): codes run 0 to n-1 and n is
    /// that container's slot count. The name shown is <c>UiStrings</c> <c>inv.equipSlot.&lt;id&gt;</c>.
    /// </summary>
    [Table("EquipSlotDef", 1)]
    public sealed class EquipSlotDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Most slots: an item's slots travel as a 16-bit mask.</summary>
        public const int MaxSlots = 16;

        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Slot index in the Equipped container, stored in <c>item_instances.slot</c>; never renumbered.</summary>
        [Range(0, MaxSlots - 1)] public int Code { get; set; }

        /// <summary>One of the two weapon slots of R-3.5-07: an item filling it must have a weapon type.</summary>
        public bool IsWeapon { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        /// <summary>Codes are unique and contiguous from 0, and there are as many as the Equipped container has slots.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            int count = 0;
            foreach (var other in NetIds.Rows<EquipSlotDef>(set))
            {
                count++;
                if (!ReferenceEquals(other, this) && other.Code == Code)
                    errors.Add(nameof(Code), $"{Code} is also used by '{other.Id}'");
            }
            if (Code >= count)
                errors.Add(nameof(Code), $"{Code} leaves a gap: codes run from 0 to {count - 1}");
            var equipped = ContainerDef.FindEquipped(set);
            if (equipped != null && equipped.Slots != count)
                errors.Add(nameof(Code), $"there are {count} slots but the Equipped container '{equipped.Id}' has {equipped.Slots}");
        }

        public static EquipSlotDef? Find(ConfigSet set, string id) => NetIds.Find<EquipSlotDef>(set, id, row => row.Id);
    }
}
