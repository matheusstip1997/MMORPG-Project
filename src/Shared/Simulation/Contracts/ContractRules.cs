using System.Collections.Generic;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;

namespace Shared.Simulation.Contracts
{
    /// <summary>
    /// The terms check of a contract (spec 5.4 R-5.4-01; WP-M4-10), one check for the server, the propose form and the bots: a
    /// craftable forge definition (a mold or a recipe) and one slot per input in order, each with its supplier. A customer's slot
    /// names the item it holds in escrow, which the input must take (<see cref="ForgeInputSpec.Takes"/>); a crafter's slot names
    /// the item the terms require, 0 for any item the input takes. The units of every slot are the input's count.
    /// </summary>
    public static class ContractRules
    {
        /// <summary>The terms are well formed for <paramref name="definition"/>; <paramref name="itemOf"/> finds an item by net id.</summary>
        public static ContractReason CheckTerms(ForgeDefinition? definition, IReadOnlyList<ContractSlot> slots,
            System.Func<int, ItemDef?> itemOf)
        {
            if (definition == null || definition.Output == null || slots.Count != definition.Inputs.Count)
                return ContractReason.ContractTermsInvalid;
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                var input = definition.Inputs[i];
                if (slot.Count != input.Count)
                    return ContractReason.ContractTermsInvalid;
                if (slot.Supplier == ContractSupplier.Crafter && slot.ItemNetId == 0)
                    continue;
                var item = itemOf(slot.ItemNetId);
                if (item == null || !input.Takes(item))
                    return ContractReason.ContractTermsInvalid;
            }
            return ContractReason.None;
        }

        /// <summary>True when the crafter's <paramref name="item"/> may fill <paramref name="slot"/> of <paramref name="input"/>.</summary>
        public static bool CrafterSlotTakes(ContractSlot slot, ForgeInputSpec input, ItemDef item) =>
            slot.Supplier == ContractSupplier.Crafter && input.Takes(item) && (slot.ItemNetId == 0 || slot.ItemNetId == item.NetId);

        /// <summary>The slot of an enchant contract's item (the customer's equipment instance).</summary>
        public const int EnchantItemSlot = 0;

        /// <summary>The slot of an enchant contract's stones (either side supplies them, user answer 2026-11-16).</summary>
        public const int EnchantStonesSlot = 1;

        /// <summary>
        /// The terms check of an enchant contract (spec 5.4, WP-M4-10 part C; user answers 2026-11-16: one attempt, the stones'
        /// supplier chosen in the terms), one check for the server, the propose form and the bots. Exactly two slots:
        /// <list type="number">
        /// <item>the customer's <paramref name="item"/>: one unit of an instance (<c>InstanceId</c>) in the Safe bag's equipment
        /// area, equipment the enchant rules take (it has a job), below <see cref="EnchantLimits.MaxLevel"/>
        /// (<paramref name="level"/>, its current level);</item>
        /// <item>the stones: either supplier, the item's tier's stone (<see cref="EnchantRules.TryMaterial"/>) and its count.</item>
        /// </list>
        /// </summary>
        public static ContractReason CheckEnchantTerms(EnchantRules rules, ItemDef? item, int level, IReadOnlyList<ContractSlot> slots)
        {
            if (item == null || slots.Count != 2)
                return ContractReason.ContractTermsInvalid;
            var target = slots[EnchantItemSlot];
            var stones = slots[EnchantStonesSlot];
            if (target.Supplier != ContractSupplier.Customer || target.Count != 1 || target.InstanceId <= 0
                || target.Container != rules.SafeCode || target.ItemNetId != item.NetId)
                return ContractReason.ContractTermsInvalid;
            if (item.Category != ItemCategory.Equipment || rules.JobOf(item).Length == 0 || level < 0 || level >= EnchantLimits.MaxLevel)
                return ContractReason.ContractTermsInvalid;
            if (!rules.TryMaterial(item, out var stone, out int count) || stones.ItemNetId != stone.NetId || stones.Count != count)
                return ContractReason.ContractTermsInvalid;
            return ContractReason.None;
        }

        /// <summary>The number of slots the customer supplies.</summary>
        public static int CustomerSlots(IReadOnlyList<ContractSlot> slots)
        {
            int count = 0;
            foreach (var slot in slots)
            {
                if (slot.Supplier == ContractSupplier.Customer)
                    count++;
            }
            return count;
        }
    }
}
