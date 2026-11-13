using Shared.Data.Tables;

namespace Shared.Simulation.Items
{
    /// <summary>
    /// Armor per tier (R-4.1-05, WP-M4-13): an item of an armor slot (<see cref="EquipSlotDef.IsArmor"/>) belongs to one
    /// tier (<see cref="ItemDef.ArmorTier"/>), and a character wears only the items of its tier. The character's tier has one
    /// source, <c>SkillTreeRules.EffectiveArmor</c> (the class row and the learned armor passives, R-3.5-08), the one the parry
    /// rules read. One rule for the server's equip check and tier change, the client's equip slot and the bots.
    /// </summary>
    public static class ArmorTiers
    {
        /// <summary>Whether the item is armor (it carries a tier; the validator ties the tier to the armor slots).</summary>
        public static bool IsArmor(ItemDef item) => item.ArmorTier != 0;

        /// <summary>Whether a character of <paramref name="tier"/> may wear the item: anything that is not armor, or armor of that tier.</summary>
        public static bool CanWear(ItemDef item, ArmorClass tier) => item.ArmorTier == 0 || item.ArmorTier == tier;
    }
}
