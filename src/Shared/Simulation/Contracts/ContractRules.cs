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
