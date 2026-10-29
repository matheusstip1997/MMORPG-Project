using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Simulation;

/// <summary>
/// <see cref="ActionMachine.Retune"/> (spec 11.7 dev hot reload): what happens to a machine when the action tables are
/// swapped at a tick boundary, on the server and in prediction alike.
/// </summary>
public class ActionMachineRetuneTests
{
    /// <summary>The kit's tables with the attack's recovery and skill.cut's cooldown changed (same rows, same order).</summary>
    private static ActionCatalog Tuned(int attackRecoveryMs = 600, int cutCooldownMs = 1000)
    {
        var old = Catalog();
        var actions = new List<ActionDef>();
        var effects = new List<ActionEffectDef>();
        for (int i = 0; i < old.Count; i++)
        {
            var def = old[i].Def;
            actions.Add(def.Id switch
            {
                "attack" => Action("attack", 1, ActionKind.Attack, 300, 100, attackRecoveryMs, buffer: 150, cancelableBy: ["dodge"],
                    cancelFrom: ActionPhase.Recovery),
                "skill.cut" => Action("skill.cut", 101, ActionKind.Skill, 250, 150, 350, cooldown: cutCooldownMs, buffer: 150,
                    cancelableBy: ["dodge"], cancelFrom: ActionPhase.Recovery),
                _ => def,
            });
            effects.AddRange(old[i].Effects.Select(e => e.Def));
        }
        var bindings = new ActionBindings
        {
            NormalAttack = "attack",
            Dodge = "dodge",
            Parry = "parry",
            SkillSlots = ["skill.cut", "skill.circle", "skill.charge", "skill.armored", ""],
        };
        return new ActionCatalog(TickRateHz, actions, effects, bindings, DodgePool, ParryPool);
    }

    [Fact]
    public void A_running_action_ends_like_a_cancel_and_the_next_one_runs_the_new_timing()
    {
        var state = new ActionMachineState(Catalog());
        Step(state, Input(InputActions.Attack));
        Step(state, NoInput());
        Assert.Equal(ActionState.Startup, state.State);

        var tuned = Tuned(attackRecoveryMs: 600);
        ActionMachine.Retune(state, tuned, tuned.Loadout(null));

        Assert.Same(tuned, state.Catalog);
        Assert.Equal(ActionState.Idle, state.State);
        Assert.Equal(-1, state.ActionIndex);
        Assert.Equal(0, state.FiredEffects);
        Step(state, Input()); // release, so the next attack is a new press
        var result = Step(state, Input(InputActions.Attack));
        Assert.True(result.StartedIndex >= 0);
        Assert.Equal(6 + 2 + 12, state.Action!.TotalTicks); // 300/100/600 ms at 20 Hz
    }

    [Fact]
    public void Cooldowns_keep_running_clamped_to_the_new_cooldown()
    {
        var catalog = Catalog();
        var state = new ActionMachineState(catalog);
        Step(state, Input(slot: 1)); // skill.cut: 3 s cooldown = 60 ticks
        int cut = catalog.Find("skill.cut")!.Index;
        Assert.True(state.CooldownTicks(cut) > 20);

        var tuned = Tuned(cutCooldownMs: 1000); // 20 ticks
        ActionMachine.Retune(state, tuned, tuned.Loadout(null));

        Assert.Equal(20, state.CooldownTicks(cut));
    }

    [Fact]
    public void A_flinch_runs_on_and_a_buffered_press_is_forgotten()
    {
        var state = new ActionMachineState(Catalog());
        Assert.True(ActionMachine.Interrupt(state, InterruptKind.Flinch, 300));
        Step(state, NoInput());
        Step(state, Input(InputActions.Attack)); // buffered while flinched
        int elapsed = state.Elapsed;

        var tuned = Tuned();
        ActionMachine.Retune(state, tuned, tuned.Loadout(null));

        Assert.Equal(ActionState.Flinched, state.State);
        Assert.Equal(elapsed, state.Elapsed);
        Assert.Equal(-1, state.BufferedIndex);
    }

    [Fact]
    public void Other_actions_or_another_order_are_refused()
    {
        var state = new ActionMachineState(Catalog());
        var fewer = new ActionCatalog(TickRateHz, [Action("attack", 1, ActionKind.Attack, 300, 100, 300)], [], (ActionBindings?)null);

        Assert.Throws<ArgumentException>(() => ActionMachine.Retune(state, fewer, fewer.Loadout(null)));
    }

    [Fact]
    public void Two_machines_retuned_the_same_way_stay_the_same()
    {
        var server = new ActionMachineState(Catalog());
        var predicted = new ActionMachineState(Catalog());
        foreach (var machine in new[] { server, predicted })
        {
            Step(machine, Input(slot: 1));
            Step(machine, NoInput());
        }
        var tuned = Tuned();
        ActionMachine.Retune(server, tuned, tuned.Loadout(null));
        ActionMachine.Retune(predicted, tuned, tuned.Loadout(null));
        for (int i = 0; i < 30; i++)
        {
            var input = i == 3 ? Input(InputActions.Attack) : NoInput();
            Step(server, input);
            Step(predicted, input);
        }

        Assert.True(server.SameAs(predicted));
    }
}
