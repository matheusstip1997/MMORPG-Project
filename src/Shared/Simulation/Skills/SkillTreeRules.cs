using System;
using System.Collections.Generic;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Shared.Simulation.Skills
{
    /// <summary>
    /// The checks and state changes of the skill tree (spec 3.5, R-3.5-01/03/04/07/08; WP-M2-5). Pure: the server's
    /// <c>SkillTreeService</c> runs a check, then applies it to the character's <see cref="SkillState"/>; the skill tree
    /// window and the bots run the same checks to show what can be learned. <c>blocked</c> means in combat (instances
    /// and boss fights count as combat; TODO(M4) instances) or during an action.
    /// </summary>
    public static class SkillTreeRules
    {
        /// <summary>R-3.5-04: learn a node at level 1 (an evolution is learned with <see cref="CheckEvolve"/>).</summary>
        public static SkillTreeReason CheckLearn(SkillRules rules, SkillState state, string nodeId, int level, int points, out int cost)
        {
            cost = 0;
            var reason = CheckNode(rules, state, nodeId, out var node);
            if (reason != SkillTreeReason.None)
                return reason;
            if (node!.EvolvesFrom.Length != 0)
                return SkillTreeReason.EvolveRequired;
            if (state.IsLearned(nodeId))
                return SkillTreeReason.AlreadyLearned;
            return CheckGates(state, node, level, points, out cost);
        }

        /// <summary>One more level of a learned node.</summary>
        public static SkillTreeReason CheckUpgrade(SkillRules rules, SkillState state, string nodeId, int points, out int cost)
        {
            cost = 0;
            var reason = CheckNode(rules, state, nodeId, out var node);
            if (reason != SkillTreeReason.None)
                return reason;
            int current = state.LevelOf(nodeId);
            if (current == 0)
                return SkillTreeReason.SkillNotLearned;
            if (current >= node!.MaxLevel)
                return SkillTreeReason.MaxLevelReached;
            cost = node.PointCost;
            return points < cost ? SkillTreeReason.NotEnoughPoints : SkillTreeReason.None;
        }

        /// <summary>
        /// R-3.5-04: an evolution replaces the node it names, which must be learned (at any level); it costs points like a
        /// new node, starts at level 1, and the base's points are lost. Blocked in combat (spec 3.5 evolve flow).
        /// </summary>
        public static SkillTreeReason CheckEvolve(SkillRules rules, SkillState state, string nodeId, int level, int points, bool blocked,
            out int cost)
        {
            cost = 0;
            var reason = CheckNode(rules, state, nodeId, out var node);
            if (reason != SkillTreeReason.None)
                return reason;
            if (node!.EvolvesFrom.Length == 0)
                return SkillTreeReason.NotAnEvolution;
            if (state.IsLearned(nodeId))
                return SkillTreeReason.AlreadyLearned;
            if (!state.IsLearned(node.EvolvesFrom))
            {
                // One base, several evolutions on a line: the player takes one of them (R-3.5-04).
                foreach (var pair in state.Learned)
                {
                    var other = rules.Node(pair.Key);
                    if (other != null && other.Family == node.Family && other.EvolvesFrom.Length != 0)
                        return SkillTreeReason.EvolutionTaken;
                }
                return SkillTreeReason.SkillNotLearned;
            }
            if (blocked)
                return SkillTreeReason.EvolveBlockedCombat;
            return CheckGates(state, node, level, points, out cost);
        }

        /// <summary>R-3.5-04: free, any time out of combat; clears evolved nodes too. <paramref name="refund"/> is the points back.</summary>
        public static SkillTreeReason CheckRespec(SkillRules rules, SkillState state, bool blocked, out int refund)
        {
            refund = PointsSpent(rules, state);
            if (state.LearnedCount == 0)
                return SkillTreeReason.NothingToRespec;
            return blocked ? SkillTreeReason.RespecBlockedCombat : SkillTreeReason.None;
        }

        /// <summary>R-3.5-01: advance into a class whose parent is the character's class, at its unlock level, out of combat.</summary>
        public static SkillTreeReason CheckAdvance(SkillRules rules, SkillState state, string classId, int level, bool blocked)
        {
            var target = rules.Class(classId);
            if (target == null)
                return SkillTreeReason.UnknownClass;
            if (target.Parent != state.ClassId)
                return SkillTreeReason.NotNextClass;
            if (level < target.UnlockLevel)
                return SkillTreeReason.AdvanceLevelTooLow;
            return blocked ? SkillTreeReason.AdvanceBlockedCombat : SkillTreeReason.None;
        }

        /// <summary>Spec 9.6: a learned active or buff on a slot the player can use.</summary>
        public static SkillTreeReason CheckAssign(SkillRules rules, SkillState state, int slot, string nodeId)
        {
            if (slot < 1 || slot > rules.Settings.HotbarSlots)
                return SkillTreeReason.HotbarSlotInvalid;
            var node = rules.Node(nodeId);
            if (node == null)
                return SkillTreeReason.UnknownNode;
            if (!state.IsLearned(nodeId))
                return SkillTreeReason.SkillNotLearned;
            return node.IsHotbarSkill() ? SkillTreeReason.None : SkillTreeReason.NotForHotbar;
        }

        public static SkillTreeReason CheckClear(SkillRules rules, int slot) =>
            slot < 1 || slot > rules.Settings.HotbarSlots ? SkillTreeReason.HotbarSlotInvalid : SkillTreeReason.None;

        /// <summary>Learns the node at level 1 and places it on its default slot (<see cref="DefaultSlot"/>).</summary>
        public static void ApplyLearn(SkillRules rules, SkillState state, string nodeId)
        {
            state.SetLevel(nodeId, 1);
            PlaceDefault(rules, state, nodeId, 0);
        }

        public static void ApplyUpgrade(SkillState state, string nodeId) => state.SetLevel(nodeId, state.LevelOf(nodeId) + 1);

        /// <summary>The evolution replaces its base at level 1 and takes the base's hotbar slot (spec 3.5 evolve flow).</summary>
        public static void ApplyEvolve(SkillRules rules, SkillState state, string nodeId)
        {
            var node = rules.Node(nodeId)!;
            int baseSlot = state.SlotOf(node.EvolvesFrom);
            state.Forget(node.EvolvesFrom);
            state.SetLevel(nodeId, 1);
            PlaceDefault(rules, state, nodeId, baseSlot);
        }

        public static void ApplyRespec(SkillState state) => state.ClearAll();

        /// <summary>R-3.5-04: nothing is reset by a class change; the new tree becomes available.</summary>
        public static void ApplyAdvance(SkillState state, string classId) => state.ClassId = classId;

        /// <summary>Points spent on the learned nodes (R-3.5-04: derived from the levels; lost evolution points not included).</summary>
        public static int PointsSpent(SkillRules rules, SkillState state)
        {
            int spent = 0;
            foreach (var pair in state.Learned)
            {
                var node = rules.Node(pair.Key);
                if (node != null)
                    spent += pair.Value * node.PointCost;
            }
            return spent;
        }

        /// <summary>
        /// R-3.5-07 (user answer 2026-10-27): the weapon types the character may equip, the class rows of its line together;
        /// a type a weapon-unlock node of the line lists needs that node learned.
        /// </summary>
        public static uint UsableWeapons(SkillRules rules, SkillState state)
        {
            uint usable = 0;
            uint gated = 0;
            uint unlocked = 0;
            foreach (var cls in rules.Line(state.ClassId))
            {
                usable |= rules.ClassWeapons(cls.Id);
                foreach (var node in rules.Tree(cls.Id))
                {
                    if (node.Kind != SkillNodeKind.WeaponUnlock)
                        continue;
                    uint mask = WeaponMasks.Of(node.Weapons);
                    gated |= mask;
                    if (state.IsLearned(node.Id))
                        unlocked |= mask;
                }
            }
            return (usable & ~gated) | (usable & unlocked);
        }

        /// <summary>
        /// R-3.5-08: the class's armor tier (its row, which repeats the earlier tier's unless it sets one), or the tier of the
        /// learned armor passive latest on the class line (the highest tier; the lowest node id breaks a tie).
        /// </summary>
        public static ArmorClass EffectiveArmor(SkillRules rules, SkillState state)
        {
            var armor = rules.Class(state.ClassId)?.ArmorClass ?? ArmorClass.Light;
            int bestTier = -1;
            foreach (var pair in state.Learned)
            {
                var skill = rules.Skill(pair.Key);
                var node = rules.Node(pair.Key);
                if (skill == null || node == null || skill.ArmorClass == 0 || node.Kind != SkillNodeKind.Passive)
                    continue;
                int tier = rules.TierOf(node);
                if (tier > bestTier)
                {
                    bestTier = tier;
                    armor = skill.ArmorClass;
                }
            }
            return armor;
        }

        /// <summary>
        /// The slot a newly learned skill goes on: <paramref name="preferred"/> when free, else its kit slot of the line's
        /// <see cref="ActionBindings"/> row (the default layout, spec 9.6) when free, else the first free slot; 0 when the bar
        /// is full or the node is not a hotbar skill (the player places it).
        /// </summary>
        public static int DefaultSlot(SkillRules rules, SkillState state, string nodeId, int preferred)
        {
            var node = rules.Node(nodeId);
            var skill = rules.Skill(nodeId);
            if (node == null || skill == null || !node.IsHotbarSkill())
                return 0;
            int slots = rules.Settings.HotbarSlots;
            if (preferred >= 1 && preferred <= slots && state.NodeAt(preferred).Length == 0)
                return preferred;
            var bindings = rules.Bindings(state.ClassId);
            if (bindings != null)
            {
                int kit = Array.IndexOf(bindings.SkillSlots, skill.ActionId) + 1;
                if (kit >= 1 && kit <= slots && state.NodeAt(kit).Length == 0)
                    return kit;
            }
            for (int slot = 1; slot <= slots; slot++)
            {
                if (state.NodeAt(slot).Length == 0 && !KitSlot(bindings, slot))
                    return slot;
            }
            for (int slot = 1; slot <= slots; slot++)
            {
                if (state.NodeAt(slot).Length == 0)
                    return slot;
            }
            return 0;
        }

        /// <summary>Nodes the character can learn or evolve now, in table order (the window's highlight and the bots).</summary>
        public static List<SkillTree> Available(SkillRules rules, SkillState state, int level, int points)
        {
            var list = new List<SkillTree>();
            foreach (var cls in rules.Line(state.ClassId))
            {
                foreach (var node in rules.Tree(cls.Id))
                {
                    var reason = node.EvolvesFrom.Length == 0
                        ? CheckLearn(rules, state, node.Id, level, points, out _)
                        : CheckEvolve(rules, state, node.Id, level, points, false, out _);
                    if (reason == SkillTreeReason.None)
                        list.Add(node);
                }
            }
            return list;
        }

        private static void PlaceDefault(SkillRules rules, SkillState state, string nodeId, int preferred)
        {
            int slot = DefaultSlot(rules, state, nodeId, preferred);
            if (slot != 0)
                state.Place(slot, nodeId);
        }

        /// <summary>Slots the default layout keeps for kit skills not learned yet, so a new skill does not take one.</summary>
        private static bool KitSlot(ActionBindings? bindings, int slot) =>
            bindings != null && slot <= bindings.SkillSlots.Length && bindings.SkillSlots[slot - 1].Length != 0;

        private static SkillTreeReason CheckNode(SkillRules rules, SkillState state, string nodeId, out SkillTree? node)
        {
            node = rules.Node(nodeId);
            if (node == null)
                return SkillTreeReason.UnknownNode;
            if (!rules.OnLine(state.ClassId, node.ClassId))
                return SkillTreeReason.WrongClassLine;
            return rules.Skill(nodeId) == null ? SkillTreeReason.SkillUnavailable : SkillTreeReason.None;
        }

        private static SkillTreeReason CheckGates(SkillState state, SkillTree node, int level, int points, out int cost)
        {
            cost = node.PointCost;
            if (level < node.MinCharLevel)
                return SkillTreeReason.CharacterLevelTooLow;
            for (int i = 0; i < node.Requires.Length && i < node.RequiresLevels.Length; i++)
            {
                if (state.LevelOf(node.Requires[i]) < node.RequiresLevels[i])
                    return SkillTreeReason.PrerequisiteMissing;
            }
            return points < cost ? SkillTreeReason.NotEnoughPoints : SkillTreeReason.None;
        }
    }
}
