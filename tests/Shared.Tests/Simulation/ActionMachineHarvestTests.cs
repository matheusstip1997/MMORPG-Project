using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using MoveKeys = Shared.Simulation.MoveKeys;
using Shared.Simulation.Actions;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Simulation;

/// <summary>
/// Harvesting is a channel on the action machine (spec 5.2 "harvest is a channeled action, interruptible by
/// damage/movement"; M3 decision 5; WP-M3-5): a request starts it from Idle like an item use; a movement input ends it in
/// Startup or Channel (<c>cancelOnMove</c>) but not once its harvest effect fired; a dodge cancels it; a flinch interrupts
/// it; the server's <see cref="ActionMachine.Break"/> ends it to Idle (damage, the node gone). Server and prediction run
/// this same code.
/// </summary>
public class ActionMachineHarvestTests
{
    private static readonly ActionCatalog Catalog = BuildCatalog();
    private static readonly int Mine = Catalog.Find("harvest.ore")!.Index;

    /// <summary>dodge 50/250/200; harvest 200 startup, 1000 channel, 100 active, 300 recovery (4/20/2/6 ticks), effect at Active.</summary>
    private static ActionCatalog BuildCatalog()
    {
        var harvest = Action("harvest.ore", 239, ActionKind.Harvest, 200, 100, 300, channel: 1000, cancelableBy: ["dodge"], stamina: 0);
        harvest.CancelOnMove = true;
        var actions = new[]
        {
            Action("attack", 1, ActionKind.Attack, 300, 100, 300),
            Action("dodge", 2, ActionKind.Dodge, 50, 250, 200),
            Action("parry", 3, ActionKind.Parry, 50, 200, 250),
            harvest,
        };
        var effects = new[]
        {
            Effect("attack.strike", "attack", EffectKind.Strike, ActionPhase.Active, 0),
            Effect("harvest.ore.harvest", "harvest.ore", EffectKind.Harvest, ActionPhase.Active, 0),
        };
        var bindings = new ActionBindings { NormalAttack = "attack", Dodge = "dodge", Parry = "parry", SkillSlots = [""] };
        return new ActionCatalog(TickRateHz, actions, effects, bindings, DodgePool, ParryPool);
    }

    private static ActionInput Moving(MoveKeys keys = MoveKeys.W) => new(true, InputActions.None, 0, 0, 0, keys);

    private static ActionStepResult StepFiring(ActionMachineState state, in ActionInput input, List<EffectKind> fired)
    {
        var buffer = new FiredEffect[ActionMachine.MaxFiredPerStep];
        var result = ActionMachine.Step(state, input, UnlimitedActionResources.Instance, buffer);
        for (int i = 0; i < result.FiredCount; i++)
            fired.Add(buffer[i].Kind);
        return result;
    }

    [Fact]
    public void A_harvest_runs_its_channel_and_fires_its_harvest_effect_at_the_start_of_active()
    {
        var state = new ActionMachineState(Catalog);
        var fired = new List<EffectKind>();
        Assert.Equal(Mine, StepFiring(state, Input().WithRequest(Mine), fired).StartedIndex);

        bool sawChannel = false;
        int ticks = 0;
        while (fired.Count == 0 && ticks < 100)
        {
            StepFiring(state, Input(), fired);
            sawChannel |= state.State == ActionState.Channel;
            ticks++;
        }
        Assert.True(sawChannel);
        Assert.Equal([EffectKind.Harvest], fired);
        Assert.Equal(Catalog[Mine].ChannelEnd, ticks);
    }

    [Theory]
    [InlineData(1)] // Startup
    [InlineData(10)] // Channel
    public void A_movement_input_ends_the_harvest_in_startup_or_channel_and_its_effect_never_fires(int ticksBefore)
    {
        var state = new ActionMachineState(Catalog);
        var fired = new List<EffectKind>();
        StepFiring(state, Input().WithRequest(Mine), fired);
        for (int i = 0; i < ticksBefore; i++)
            StepFiring(state, Input(), fired);

        var moved = StepFiring(state, Moving(), fired);

        Assert.Equal(Mine, moved.CancelledIndex);
        Assert.Equal(ActionState.Idle, state.State);
        for (int i = 0; i < 60; i++)
            StepFiring(state, Moving(), fired);
        Assert.Empty(fired);
    }

    [Fact]
    public void A_movement_input_after_the_harvest_effect_fired_does_not_cancel_the_rest()
    {
        var state = new ActionMachineState(Catalog);
        var fired = new List<EffectKind>();
        StepFiring(state, Input().WithRequest(Mine), fired);
        while (fired.Count == 0)
            StepFiring(state, Input(), fired);
        Assert.Equal(ActionState.Active, state.State);

        var moved = StepFiring(state, Moving(), fired);
        Assert.Equal(-1, moved.CancelledIndex);
        Assert.Equal(Mine, state.ActionIndex);
    }

    [Fact]
    public void A_movement_input_does_not_end_an_action_without_cancel_on_move()
    {
        var state = new ActionMachineState(Catalog);
        Step(state, Input(InputActions.Attack));
        var moved = Step(state, Moving(MoveKeys.A | MoveKeys.S));
        Assert.Equal(-1, moved.CancelledIndex);
        Assert.Equal(Catalog.Find("attack")!.Index, state.ActionIndex);
    }

    [Fact]
    public void A_dodge_cancels_the_harvest_channel()
    {
        var state = new ActionMachineState(Catalog);
        var fired = new List<EffectKind>();
        StepFiring(state, Input().WithRequest(Mine), fired);
        for (int i = 0; i < 8; i++)
            StepFiring(state, Input(), fired);

        var dodge = StepFiring(state, Input(InputActions.Dodge), fired);
        Assert.Equal(Mine, dodge.CancelledIndex);
        Assert.Equal(Catalog.Find("dodge")!.Index, dodge.StartedIndex);
        RunToIdle(state);
        Assert.DoesNotContain(EffectKind.Harvest, fired);
    }

    [Fact]
    public void A_flinch_in_the_channel_interrupts_the_harvest()
    {
        var state = new ActionMachineState(Catalog);
        Step(state, Input().WithRequest(Mine));
        for (int i = 0; i < 8; i++)
            Step(state, Input());
        Assert.Equal(ActionState.Channel, state.State);

        Assert.True(ActionMachine.Interrupt(state, InterruptKind.Flinch, 300));
        Assert.Equal(ActionState.Flinched, state.State);
        Assert.Equal(-1, state.ActionIndex);
    }

    [Fact]
    public void Break_ends_a_running_action_to_idle_and_leaves_interrupts_and_idle_alone()
    {
        var state = new ActionMachineState(Catalog);
        Assert.False(ActionMachine.Break(state)); // idle

        Step(state, Input().WithRequest(Mine));
        Step(state, Input());
        Assert.True(ActionMachine.Break(state));
        Assert.Equal(ActionState.Idle, state.State);
        Assert.Equal(-1, state.ActionIndex);

        Step(state, Input().WithRequest(Mine));
        ActionMachine.Interrupt(state, InterruptKind.PoiseBreak, 500);
        Assert.False(ActionMachine.Break(state)); // a stagger runs on
        Assert.Equal(ActionState.Staggered, state.State);
    }
}
