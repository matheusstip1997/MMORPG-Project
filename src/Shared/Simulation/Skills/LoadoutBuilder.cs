using Shared.Data.Tables;
using Shared.Simulation.Actions;

namespace Shared.Simulation.Skills
{
    /// <summary>
    /// Builds a character's <see cref="ActionLoadout"/> from its skill state (spec 3.5, 9.6; WP-M2-5): the class line's
    /// normal attack, dodge and parry (its <see cref="ActionBindings"/> row) and, on each hotbar slot, the action of the
    /// learned skill placed there with the weapon types its node needs (R-3.5-07). The server, client prediction, the HUD
    /// and the bots call this one function, so a learned skill predicts like a kit skill.
    /// </summary>
    public static class LoadoutBuilder
    {
        public static ActionLoadout Build(ActionCatalog catalog, SkillRules rules, SkillState state)
        {
            var bindings = rules.Bindings(state.ClassId);
            if (bindings == null)
                return ActionLoadout.None;
            var slotIndex = new int[ActionBindings.MaxSkillSlots + 1];
            var slotWeapons = new uint[ActionBindings.MaxSkillSlots + 1];
            for (int slot = 1; slot <= ActionBindings.MaxSkillSlots; slot++)
            {
                slotIndex[slot] = -1;
                string nodeId = state.NodeAt(slot);
                if (nodeId.Length == 0 || !state.IsLearned(nodeId))
                    continue;
                var node = rules.Node(nodeId);
                var skill = rules.Skill(nodeId);
                if (node == null || skill == null || !node.IsHotbarSkill() || skill.ActionId.Length == 0)
                    continue;
                slotIndex[slot] = IndexOf(catalog, skill.ActionId);
                slotWeapons[slot] = WeaponMasks.Of(node.Weapons);
            }
            return new ActionLoadout(state.ClassId, IndexOf(catalog, bindings.NormalAttack), IndexOf(catalog, bindings.Dodge),
                IndexOf(catalog, bindings.Parry), slotIndex, slotWeapons);
        }

        private static int IndexOf(ActionCatalog catalog, string actionId) => catalog.Find(actionId)?.Index ?? -1;
    }
}
