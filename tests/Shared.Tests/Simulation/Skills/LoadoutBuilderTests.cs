using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Skills;

namespace Shared.Tests.Simulation.Skills;

/// <summary>
/// The per-character loadout of spec 3.5 and 9.6 (WP-M2-5): built from the learned skills the player placed, with the
/// weapon types each slot needs (R-3.5-07), and the action machine's <c>WrongWeapon</c> refusal.
/// </summary>
public class LoadoutBuilderTests
{
    private static readonly SkillRules Rules = SkillTestKit.Rules();
    private static readonly ActionCatalog Catalog = SkillTestKit.Catalog;

    private static int Index(string id) => Catalog.Find(id)!.Index;

    [Fact]
    public void The_loadout_binds_the_line_attacks_and_the_learned_skills_on_their_slots()
    {
        var state = SkillTestKit.Learned("c", ("r.cut", 1), ("c.lance", 1));
        state.Place(7, "r.cut");
        state.Place(8, "c.lance");
        state.Place(9, "r.circle"); // not learned: left empty
        var loadout = LoadoutBuilder.Build(Catalog, Rules, state);
        Assert.Equal("c", loadout.ClassId);
        Assert.Equal((Index("attack"), Index("dodge"), Index("parry")), (loadout.AttackIndex, loadout.DodgeIndex, loadout.ParryIndex));
        Assert.Equal(Index("skill.cut"), loadout.SlotIndex(7));
        Assert.Equal(Index("skill.charge"), loadout.SlotIndex(8));
        Assert.Equal(-1, loadout.SlotIndex(9));
        Assert.Equal(-1, loadout.SlotIndex(1));
        Assert.Equal(0u, loadout.SlotWeapons(7));
        Assert.Equal(WeaponMasks.Bit(WeaponType.Spear), loadout.SlotWeapons(8));
    }

    [Fact]
    public void An_evolution_without_its_own_action_presses_its_parent_s()
    {
        // R-3.5-05: inheritance with overrides; c.charge names its own action, a row that does not inherits it.
        var state = SkillTestKit.Learned("c", ("c.charge", 1));
        state.Place(1, "c.charge");
        Assert.Equal(Index("skill.charge"), LoadoutBuilder.Build(Catalog, Rules, state).SlotIndex(1));
    }

    [Fact]
    public void A_skill_whose_weapon_is_not_equipped_is_refused_with_WrongWeapon_and_starts_with_it()
    {
        var state = SkillTestKit.Learned("c", ("c.lance", 1));
        state.Place(2, "c.lance");
        var machine = new ActionMachineState(Catalog, LoadoutBuilder.Build(Catalog, Rules, state))
        {
            EquippedWeapons = WeaponMasks.Bit(WeaponType.Sword),
        };
        var result = ActionMachine.Step(machine, new ActionInput(true, InputActions.None, 2, 0, 9), UnlimitedActionResources.Instance, new FiredEffect[8]);
        Assert.Equal((ActionReason.WrongWeapon, Index("skill.charge"), (byte)2), (result.Refused, result.RefusedIndex, result.RefusedSlot));
        Assert.False(machine.IsBusy);

        machine.EquippedWeapons = WeaponMasks.Of(new[] { WeaponType.Spear, WeaponType.Shield });
        result = ActionMachine.Step(machine, new ActionInput(true, InputActions.None, 0, 0, 10), UnlimitedActionResources.Instance, new FiredEffect[8]);
        result = ActionMachine.Step(machine, new ActionInput(true, InputActions.None, 2, 0, 11), UnlimitedActionResources.Instance, new FiredEffect[8]);
        Assert.Equal(Index("skill.charge"), result.StartedIndex);
    }

    [Fact]
    public void Two_loadouts_of_the_same_state_bind_the_same_and_a_copy_keeps_the_weapons()
    {
        var state = SkillTestKit.Learned("r", ("r.cut", 1));
        state.Place(1, "r.cut");
        var a = LoadoutBuilder.Build(Catalog, Rules, state);
        var b = LoadoutBuilder.Build(Catalog, Rules, state);
        Assert.NotSame(a, b);
        Assert.True(a.SameBindings(b));
        var machine = new ActionMachineState(Catalog, a) { EquippedWeapons = 6 };
        var copy = new ActionMachineState(Catalog, b);
        copy.CopyFrom(machine);
        Assert.True(copy.SameAs(machine));
        Assert.Equal(6u, copy.EquippedWeapons);
    }

    [Fact]
    public void A_class_line_without_bindings_gets_no_loadout()
    {
        Assert.Same(ActionLoadout.None, LoadoutBuilder.Build(Catalog, Rules, new SkillState("unknown")));
    }
}
