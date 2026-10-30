using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Simulation;

/// <summary>
/// Using an item is an action (spec 3.4 "Consumable use", M3 plan decision 3; WP-M3-3): a request alongside the player's
/// input frame starts the use action like a skill press from Idle, a press of the same tick wins, it never starts during
/// another action, and it is cancelled by a dodge and interrupted by a flinch in Startup before its consume effect fires.
/// </summary>
public class ActionMachineUseItemTests
{
    private static readonly ActionCatalog Catalog = BuildCatalog();
    private static readonly int Drink = Catalog.Find("use.drink")!.Index;

    /// <summary>attack 300/100/300; dodge 50/250/200; drink 600/100/400 ms (12/2/8 ticks) with its consume at the start of Active.</summary>
    private static ActionCatalog BuildCatalog()
    {
        var actions = new[]
        {
            Action("attack", 1, ActionKind.Attack, 300, 100, 300, cancelableBy: ["dodge"], cancelFrom: ActionPhase.Recovery),
            Action("dodge", 2, ActionKind.Dodge, 50, 250, 200),
            Action("parry", 3, ActionKind.Parry, 50, 200, 250),
            Action("use.drink", 237, ActionKind.UseItem, 600, 100, 400, cancelableBy: ["dodge"], stamina: 0),
        };
        var effects = new[]
        {
            Effect("attack.strike", "attack", EffectKind.Strike, ActionPhase.Active, 0),
            Effect("use.drink.consume", "use.drink", EffectKind.Consume, ActionPhase.Active, 0),
        };
        var bindings = new ActionBindings { NormalAttack = "attack", Dodge = "dodge", Parry = "parry", SkillSlots = [""] };
        return new ActionCatalog(TickRateHz, actions, effects, bindings, DodgePool, ParryPool);
    }

    private static ActionStepResult StepFiring(ActionMachineState state, in ActionInput input, List<EffectKind> fired)
    {
        var buffer = new FiredEffect[ActionMachine.MaxFiredPerStep];
        var result = ActionMachine.Step(state, input, UnlimitedActionResources.Instance, buffer);
        for (int i = 0; i < result.FiredCount; i++)
            fired.Add(buffer[i].Kind);
        return result;
    }

    [Fact]
    public void A_request_alongside_an_input_frame_starts_the_use_from_idle_and_consumes_at_the_start_of_active()
    {
        var state = new ActionMachineState(Catalog);
        var fired = new List<EffectKind>();

        var start = StepFiring(state, Input().WithRequest(Drink), fired);
        Assert.Equal(Drink, start.StartedIndex);
        Assert.Equal(ActionState.Startup, state.State);

        int ticks = 0;
        while (fired.Count == 0 && ticks < 100)
        {
            StepFiring(state, Input(), fired); // the input frames keep coming: movement keys are ignored, facing locked
            ticks++;
        }
        Assert.Equal([EffectKind.Consume], fired);
        Assert.Equal(Catalog[Drink].StartupEnd, ticks);
        Assert.Equal(ActionState.Active, state.State);
    }

    [Fact]
    public void A_press_in_the_same_tick_wins_and_the_request_does_not_start()
    {
        var state = new ActionMachineState(Catalog);

        var result = Step(state, Input(InputActions.Attack).WithRequest(Drink));

        Assert.Equal(Catalog.Find("attack")!.Index, result.StartedIndex);
        Assert.Equal(ActionReason.None, result.Refused); // not a refusal: the server answers ActionBusy
    }

    [Fact]
    public void A_request_during_another_action_is_dropped_and_never_buffered()
    {
        var state = new ActionMachineState(Catalog);
        Step(state, Input(InputActions.Attack));

        var during = Step(state, Input(InputActions.Attack).WithRequest(Drink));
        Assert.Equal(-1, during.StartedIndex);
        Assert.Equal(ActionReason.None, during.Refused);
        RunToIdle(state);
        Assert.Equal(-1, state.ActionIndex); // nothing buffered
    }

    [Fact]
    public void A_flinch_in_startup_interrupts_the_use_like_a_skill_and_its_consume_never_fires()
    {
        var state = new ActionMachineState(Catalog);
        var fired = new List<EffectKind>();
        StepFiring(state, Input().WithRequest(Drink), fired);
        StepFiring(state, Input(), fired);

        Assert.True(ActionMachine.Interrupt(state, InterruptKind.Flinch, 300));
        for (int i = 0; i < 40; i++)
            StepFiring(state, Input(), fired);

        Assert.Empty(fired);
        Assert.Equal(-1, state.ActionIndex);
    }

    [Fact]
    public void A_dodge_cancels_the_use_in_startup_and_its_consume_never_fires()
    {
        var state = new ActionMachineState(Catalog);
        var fired = new List<EffectKind>();
        StepFiring(state, Input().WithRequest(Drink), fired);

        var dodge = StepFiring(state, Input(InputActions.Dodge), fired);
        Assert.Equal(Drink, dodge.CancelledIndex);
        Assert.Equal(Catalog.Find("dodge")!.Index, dodge.StartedIndex);
        RunToIdle(state);

        Assert.DoesNotContain(EffectKind.Consume, fired);
    }

    [Fact]
    public void A_request_without_input_still_starts_from_idle_as_a_monster_request()
    {
        var state = new ActionMachineState(Catalog);
        Assert.Equal(Drink, Step(state, ActionInput.Request(Drink, aim: 0)).StartedIndex);
    }
}
