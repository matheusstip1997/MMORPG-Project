using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Skills;

namespace Shared.Tests.Simulation.Skills;

/// <summary>The skill-tree checks and changes of spec 3.5 (R-3.5-01/03/04/07/08; WP-M2-5) on <see cref="SkillTestKit"/>'s tree.</summary>
public class SkillTreeRulesTests
{
    private static readonly SkillRules Rules = SkillTestKit.Rules();

    [Fact]
    public void A_new_character_learns_its_first_root_skill_with_its_one_point()
    {
        // R-3.5-04: normal attack and 1 point; the first skill is the player's choice from the root tree.
        var state = SkillTestKit.Root();
        Assert.Equal(new[] { "r.cut", "r.guard" }, SkillTreeRules.Available(Rules, state, level: 1, points: 1).Select(n => n.Id));
        Assert.Equal(SkillTreeReason.None, SkillTreeRules.CheckLearn(Rules, state, "r.cut", 1, 1, out int cost));
        Assert.Equal(1, cost);
        SkillTreeRules.ApplyLearn(Rules, state, "r.cut");
        Assert.Equal(1, state.LevelOf("r.cut"));
        Assert.Equal(1, state.SlotOf("r.cut")); // its kit slot (ActionBindings, the default layout)
    }

    [Theory]
    [InlineData("nope", 30, 9, SkillTreeReason.UnknownNode)]
    [InlineData("d.shot", 30, 9, SkillTreeReason.WrongClassLine)]
    [InlineData("c.charge", 30, 9, SkillTreeReason.WrongClassLine)] // a tier-1 tree before the advance
    [InlineData("r.circle", 4, 9, SkillTreeReason.CharacterLevelTooLow)]
    [InlineData("r.circle", 5, 9, SkillTreeReason.PrerequisiteMissing)]
    [InlineData("r.guard", 1, 0, SkillTreeReason.NotEnoughPoints)]
    public void Learning_is_refused_with_its_reason(string node, int level, int points, SkillTreeReason expected)
    {
        Assert.Equal(expected, SkillTreeRules.CheckLearn(Rules, SkillTestKit.Root(), node, level, points, out _));
    }

    [Fact]
    public void A_prerequisite_needs_its_level_and_a_learned_node_is_not_learned_twice()
    {
        var state = SkillTestKit.Learned("r", ("r.cut", 1));
        Assert.Equal(SkillTreeReason.PrerequisiteMissing, SkillTreeRules.CheckLearn(Rules, state, "r.circle", 5, 9, out _));
        state.SetLevel("r.cut", 2);
        Assert.Equal(SkillTreeReason.None, SkillTreeRules.CheckLearn(Rules, state, "r.circle", 5, 9, out _));
        Assert.Equal(SkillTreeReason.AlreadyLearned, SkillTreeRules.CheckLearn(Rules, state, "r.cut", 5, 9, out _));
    }

    [Fact]
    public void A_node_without_a_skill_def_and_an_evolution_are_not_learned_plainly()
    {
        var state = SkillTestKit.Learned("c", ("r.cut", 1));
        Assert.Equal(SkillTreeReason.SkillUnavailable, SkillTreeRules.CheckLearn(Rules, state, "c.blank", 30, 9, out _));
        Assert.Equal(SkillTreeReason.EvolveRequired, SkillTreeRules.CheckLearn(Rules, state, "c.charge", 30, 9, out _));
    }

    [Fact]
    public void Upgrading_costs_a_point_per_level_up_to_the_max_level()
    {
        var state = SkillTestKit.Learned("r", ("r.cut", 4));
        Assert.Equal(SkillTreeReason.SkillNotLearned, SkillTreeRules.CheckUpgrade(Rules, state, "r.guard", 9, out _));
        Assert.Equal(SkillTreeReason.NotEnoughPoints, SkillTreeRules.CheckUpgrade(Rules, state, "r.cut", 0, out _));
        Assert.Equal(SkillTreeReason.None, SkillTreeRules.CheckUpgrade(Rules, state, "r.cut", 1, out int cost));
        Assert.Equal(1, cost);
        SkillTreeRules.ApplyUpgrade(state, "r.cut");
        Assert.Equal(SkillTreeReason.MaxLevelReached, SkillTreeRules.CheckUpgrade(Rules, state, "r.cut", 9, out _));
    }

    [Fact]
    public void An_evolution_replaces_its_base_at_level_1_takes_its_slot_and_loses_its_points()
    {
        // R-3.5-04: costs points like a new node, starts at level 1, the old node's levels and points are lost.
        var state = SkillTestKit.Learned("c", ("r.cut", 3));
        state.Place(7, "r.cut");
        Assert.Equal(3, SkillTreeRules.PointsSpent(Rules, state));
        Assert.Equal(SkillTreeReason.None, SkillTreeRules.CheckEvolve(Rules, state, "c.charge", 30, 1, blocked: false, out int cost));
        Assert.Equal(1, cost);
        SkillTreeRules.ApplyEvolve(Rules, state, "c.charge");
        Assert.False(state.IsLearned("r.cut"));
        Assert.Equal(1, state.LevelOf("c.charge"));
        Assert.Equal(7, state.SlotOf("c.charge"));
        Assert.Equal(1, SkillTreeRules.PointsSpent(Rules, state)); // the base's 3 are gone
    }

    [Fact]
    public void One_base_with_several_evolutions_on_a_line_is_a_choice()
    {
        var state = SkillTestKit.Learned("c", ("r.cut", 1));
        SkillTreeRules.ApplyEvolve(Rules, state, "c.charge");
        Assert.Equal(SkillTreeReason.EvolutionTaken, SkillTreeRules.CheckEvolve(Rules, state, "c.armored", 30, 9, false, out _));
    }

    [Theory]
    [InlineData("r.cut", false, SkillTreeReason.NotAnEvolution)]
    [InlineData("c.charge", true, SkillTreeReason.EvolveBlockedCombat)]
    public void Evolving_is_refused_for_a_plain_node_and_in_combat(string node, bool blocked, SkillTreeReason expected)
    {
        var state = SkillTestKit.Learned("c", ("r.cut", 1));
        Assert.Equal(expected, SkillTreeRules.CheckEvolve(Rules, state, node, 30, 9, blocked, out _));
    }

    [Fact]
    public void Evolving_needs_the_base_learned()
    {
        Assert.Equal(SkillTreeReason.SkillNotLearned,
            SkillTreeRules.CheckEvolve(Rules, SkillTestKit.Learned("c"), "c.charge", 30, 9, false, out _));
    }

    [Fact]
    public void Respec_is_free_refunds_the_learned_points_and_clears_evolved_nodes()
    {
        var state = SkillTestKit.Learned("c", ("c.charge", 2), ("r.guard", 3), ("c.spear", 1));
        state.Place(1, "c.charge");
        Assert.Equal(SkillTreeReason.RespecBlockedCombat, SkillTreeRules.CheckRespec(Rules, state, blocked: true, out _));
        Assert.Equal(SkillTreeReason.None, SkillTreeRules.CheckRespec(Rules, state, blocked: false, out int refund));
        Assert.Equal(6, refund);
        SkillTreeRules.ApplyRespec(state);
        Assert.Equal(0, state.LearnedCount);
        Assert.Equal(string.Empty, state.NodeAt(1));
        Assert.Equal("c", state.ClassId); // the class is permanent (R-3.5-01)
        Assert.Equal(SkillTreeReason.NothingToRespec, SkillTreeRules.CheckRespec(Rules, state, false, out _));
    }

    [Theory]
    [InlineData("zz", 30, false, SkillTreeReason.UnknownClass)]
    [InlineData("r", 30, false, SkillTreeReason.NotNextClass)]
    [InlineData("c", 29, false, SkillTreeReason.AdvanceLevelTooLow)]
    [InlineData("c", 30, true, SkillTreeReason.AdvanceBlockedCombat)]
    [InlineData("d", 30, false, SkillTreeReason.None)]
    public void Advancing_needs_the_next_class_its_level_and_no_combat(string target, int level, bool blocked, SkillTreeReason expected)
    {
        Assert.Equal(expected, SkillTreeRules.CheckAdvance(Rules, SkillTestKit.Root(), target, level, blocked));
    }

    [Fact]
    public void Advancing_keeps_everything_learned_and_opens_the_new_tree()
    {
        // R-3.5-04: after a class change nothing is reset; a new tree becomes available.
        var state = SkillTestKit.Learned("r", ("r.cut", 2));
        Assert.Equal(SkillTreeReason.WrongClassLine, SkillTreeRules.CheckLearn(Rules, state, "c.training", 30, 9, out _));
        SkillTreeRules.ApplyAdvance(state, "c");
        Assert.Equal(2, state.LevelOf("r.cut"));
        Assert.Equal(SkillTreeReason.None, SkillTreeRules.CheckLearn(Rules, state, "c.training", 30, 9, out _));
        Assert.Equal(SkillTreeReason.NotNextClass, SkillTreeRules.CheckAdvance(Rules, state, "d", 30, false));
    }

    [Fact]
    public void The_hotbar_takes_learned_actives_and_buffs_on_the_player_slots_only()
    {
        var state = SkillTestKit.Learned("r", ("r.cut", 1), ("r.guard", 1));
        Assert.Equal(SkillTreeReason.HotbarSlotInvalid, SkillTreeRules.CheckAssign(Rules, state, 0, "r.cut"));
        Assert.Equal(SkillTreeReason.HotbarSlotInvalid, SkillTreeRules.CheckAssign(Rules, state, 12, "r.cut"));
        Assert.Equal(SkillTreeReason.NotForHotbar, SkillTreeRules.CheckAssign(Rules, state, 2, "r.guard"));
        Assert.Equal(SkillTreeReason.SkillNotLearned, SkillTreeRules.CheckAssign(Rules, state, 2, "r.circle"));
        Assert.Equal(SkillTreeReason.UnknownNode, SkillTreeRules.CheckAssign(Rules, state, 2, "nope"));
        Assert.Equal(SkillTreeReason.None, SkillTreeRules.CheckAssign(Rules, state, 2, "r.cut"));
        state.Place(2, "r.cut");
        Assert.Equal((2, ""), (state.SlotOf("r.cut"), state.NodeAt(1))); // a node is on one slot at most
        Assert.Equal(SkillTreeReason.HotbarSlotInvalid, SkillTreeRules.CheckClear(Rules, 0));
    }

    [Fact]
    public void A_learned_skill_without_a_free_kit_slot_takes_the_first_slot_no_kit_skill_keeps()
    {
        var state = SkillTestKit.Learned("c", ("r.cut", 1));
        state.Place(1, "r.cut");
        // c.lance has no kit slot; slot 7 is kept for r.circle (the default layout), so it goes on 2.
        Assert.Equal(2, SkillTreeRules.DefaultSlot(Rules, state, "c.lance", 0));
        Assert.Equal(5, SkillTreeRules.DefaultSlot(Rules, state, "c.lance", 5));
        Assert.Equal(0, SkillTreeRules.DefaultSlot(Rules, state, "r.guard", 0)); // passives have no slot
    }

    [Fact]
    public void Usable_weapons_are_the_class_line_together_and_an_unlocked_type_needs_its_node()
    {
        // R-3.5-07 (user answer 2026-10-27): the root's sword with the tier-1 row's list; the spear needs c.spear.
        var root = SkillTestKit.Root();
        Assert.Equal(WeaponMasks.Bit(WeaponType.Sword), SkillTreeRules.UsableWeapons(Rules, root));
        var tier1 = SkillTestKit.Learned("c");
        Assert.Equal(WeaponMasks.Of(new[] { WeaponType.Sword, WeaponType.Shield }), SkillTreeRules.UsableWeapons(Rules, tier1));
        tier1.SetLevel("c.spear", 1);
        Assert.Equal(WeaponMasks.Of(new[] { WeaponType.Sword, WeaponType.Shield, WeaponType.Spear }), SkillTreeRules.UsableWeapons(Rules, tier1));
    }

    [Fact]
    public void A_learned_armor_passive_sets_the_tier()
    {
        // R-3.5-08: the class's tier, or the latest armor passive on the class line.
        var state = SkillTestKit.Learned("c");
        Assert.Equal(ArmorClass.Heavy, SkillTreeRules.EffectiveArmor(Rules, state));
        state.SetLevel("c.training", 1);
        Assert.Equal(ArmorClass.Medium, SkillTreeRules.EffectiveArmor(Rules, state));
    }
}
