using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Simulation;

/// <summary>R-3.1-10: the normal attack as a chain of hits in the shared action machine (server and prediction alike).</summary>
public class ActionChainTests
{
    private static readonly ActionCatalog Catalog = ChainCatalog();
    private static int Index(string id) => Catalog.Find(id)!.Index;

    /// <summary>Steps with nothing held until the running action has run <paramref name="elapsed"/> ticks.</summary>
    private static void StepTo(ActionMachineState state, int elapsed, IActionResources? resources = null)
    {
        while (state.Elapsed < elapsed)
            Step(state, Input(), resources);
        Assert.Equal(elapsed, state.Elapsed);
    }

    /// <summary>A machine that just started hit 1 (left click from idle).</summary>
    private static ActionMachineState Hit1(IActionResources? resources = null)
    {
        var state = new ActionMachineState(Catalog);
        Assert.Equal(Index("hit1"), Step(state, Input(InputActions.Attack), resources).StartedIndex);
        Step(state, Input(), resources); // release
        return state;
    }

    /// <summary>From a running hit, presses attack at <paramref name="pressAt"/> and steps until the next hit starts.</summary>
    private static void ChainOn(ActionMachineState state, int pressAt, IActionResources? resources = null)
    {
        int from = state.ActionIndex;
        StepTo(state, pressAt - 1, resources);
        Step(state, Input(InputActions.Attack), resources);
        for (int i = 0; i < 30 && state.ActionIndex == from; i++)
            Step(state, Input(), resources);
        Assert.Equal(Catalog[from].ChainNextIndex, state.ActionIndex);
    }

    [Fact]
    public void The_chain_window_rounds_up_to_ticks_and_the_last_hit_has_none()
    {
        var hit1 = Catalog.Find("hit1")!;
        Assert.Equal((Index("hit2"), 6, 10), (hit1.ChainNextIndex, hit1.ChainFromTick, hit1.ChainLinkTick));
        var hit2 = Catalog.Find("hit2")!;
        Assert.Equal((Index("hit3"), 5, 9), (hit2.ChainNextIndex, hit2.ChainFromTick, hit2.ChainLinkTick));
        Assert.Equal(-1, Catalog.Find("hit4")!.ChainNextIndex);
        Assert.Equal(-1, Catalog.Find("skill.cut")!.ChainNextIndex);
    }

    [Fact]
    public void After_rounding_the_link_still_comes_after_the_hits_strike()
    {
        // A link on the strike's own tick (the validator refuses it in ms, the catalog keeps it so after rounding).
        var catalog = new ActionCatalog(TickRateHz,
            [Action("a", 1, ActionKind.Attack, 300, 100, 300, chainNext: "b", chainFrom: 300, chainLink: 300),
             Action("b", 2, ActionKind.Attack, 300, 100, 300)],
            [Effect("a.strike", "a", EffectKind.Strike, ActionPhase.Active, 0)], (ActionBindings?)null);
        Assert.Equal((7, 6), (catalog[0].ChainLinkTick, catalog[0].ChainFromTick)); // the strike fires on tick 6
    }

    [Fact]
    public void A_press_in_the_window_is_kept_and_starts_the_next_hit_on_the_link_tick()
    {
        var state = Hit1();
        StepTo(state, 6);
        var pressed = Step(state, Input(InputActions.Attack)); // tick 7: Active, inside the window
        Assert.Equal((-1, Index("hit2")), (pressed.StartedIndex, state.BufferedIndex));
        Assert.Equal(Index("hit1"), state.ActionIndex);
        Step(state, Input(InputActions.Attack)); // held: no new press
        Step(state, Input());
        var linked = Step(state, Input()); // tick 10: the link
        Assert.Equal((Index("hit2"), Index("hit1"), -1), (linked.StartedIndex, linked.CancelledIndex, linked.EndedIndex));
        Assert.Equal((Index("hit2"), 0, ActionState.Startup), (state.ActionIndex, state.Elapsed, state.State));
        Assert.Equal(-1, state.BufferedIndex);
    }

    [Fact]
    public void A_press_after_the_link_tick_starts_the_next_hit_at_once()
    {
        var state = Hit1();
        StepTo(state, 11); // Recovery, past the link
        var pressed = Step(state, Input(InputActions.Attack));
        Assert.Equal((Index("hit2"), Index("hit1")), (pressed.StartedIndex, pressed.CancelledIndex));
        Assert.Equal(0, state.Elapsed);
    }

    [Fact]
    public void A_press_before_the_window_is_dropped_and_the_hit_ends_back_at_hit_1()
    {
        var state = Hit1();
        StepTo(state, 3); // Startup: before the window
        Step(state, Input(InputActions.Attack));
        Assert.Equal(-1, state.BufferedIndex);
        RunToIdle(state);
        Assert.Equal(Index("hit1"), Step(state, Input(InputActions.Attack)).StartedIndex);
    }

    [Fact]
    public void Without_a_press_in_the_window_the_chain_returns_to_hit_1()
    {
        var state = Hit1();
        ChainOn(state, 7);
        Step(state, Input()); // release; no press during hit 2
        RunToIdle(state);
        Assert.Equal(Index("hit1"), Step(state, Input(InputActions.Attack)).StartedIndex);
    }

    [Fact]
    public void Four_presses_run_the_whole_chain_each_hit_paying_its_stamina_and_the_fourth_ends_it()
    {
        var resources = new FakeResources(stamina: 100);
        var state = Hit1(resources);
        ChainOn(state, 7, resources);
        ChainOn(state, 6, resources);
        ChainOn(state, 6, resources);
        Assert.Equal(Index("hit4"), state.ActionIndex);

        // Hit 4 has no next hit: an attack press in its Active phase is dropped, one in its buffer window is kept for
        // hit 1, which starts when hit 4 ends (the chain starts over).
        StepTo(state, 9, resources);
        Step(state, Input(InputActions.Attack), resources);
        Assert.Equal(-1, state.BufferedIndex);
        Step(state, Input(), resources);
        StepTo(state, 18, resources); // buffer window: the last 3 of 20 ticks
        Step(state, Input(InputActions.Attack), resources);
        Assert.Equal(Index("hit1"), state.BufferedIndex);
        var ended = Step(state, Input(), resources);
        Assert.Equal((Index("hit4"), Index("hit1")), (ended.EndedIndex, ended.StartedIndex));
        Assert.Equal([10, 10, 10, 10, 10], resources.Requests);
        Assert.Equal(50, resources.Stamina);
    }

    [Fact]
    public void Every_hit_fires_its_strike_before_the_next_one_starts()
    {
        var state = new ActionMachineState(Catalog);
        var buffer = new FiredEffect[ActionMachine.MaxFiredPerStep];
        var strikes = new List<string>();
        bool held = false;
        for (int tick = 0; tick < 80; tick++)
        {
            held = !held; // mash: a press every other tick
            var result = ActionMachine.Step(state, Input(held ? InputActions.Attack : InputActions.None), UnlimitedActionResources.Instance, buffer);
            for (int i = 0; i < result.FiredCount; i++)
                strikes.Add(buffer[i].Effect.Def.Id);
        }
        Assert.Equal(["hit1.strike", "hit2.strike", "hit3.strike", "hit4.strike", "hit1.strike"], strikes.Take(5));
    }

    [Fact]
    public void A_dodge_cancel_returns_the_chain_to_hit_1()
    {
        var state = Hit1();
        ChainOn(state, 7);
        StepTo(state, 8); // hit 2's Recovery
        Assert.Equal(Index("dodge"), Step(state, Input(InputActions.Dodge)).StartedIndex);
        RunToIdle(state);
        Assert.Equal(Index("hit1"), Step(state, Input(InputActions.Attack)).StartedIndex);
    }

    [Fact]
    public void A_flinch_or_a_stagger_returns_the_chain_to_hit_1_and_forgets_the_kept_press()
    {
        var state = Hit1();
        ChainOn(state, 7);
        ChainOn(state, 6);
        StepTo(state, 2); // hit 3's Startup: a flinch cancels it
        Assert.True(ActionMachine.Interrupt(state, InterruptKind.Flinch, 300));
        RunToIdle(state);
        Assert.Equal(Index("hit1"), Step(state, Input(InputActions.Attack)).StartedIndex);

        Step(state, Input());
        StepTo(state, 7);
        Step(state, Input(InputActions.Attack)); // kept for hit 2
        Assert.True(ActionMachine.Interrupt(state, InterruptKind.PoiseBreak, 300));
        Assert.Equal((ActionState.Staggered, -1), (state.State, state.BufferedIndex));
        RunToIdle(state);
        Assert.Equal(Index("hit1"), Step(state, Input(InputActions.Attack)).StartedIndex);
    }

    [Fact]
    public void A_refused_hit_leaves_the_running_hit_and_the_chain_where_they_were()
    {
        var resources = new FakeResources(stamina: 20);
        var state = Hit1(resources);
        ChainOn(state, 7, resources); // hit 2: stamina 0 now
        StepTo(state, 9, resources); // the press lands on tick 10, past hit 2's link (9)
        var refused = Step(state, Input(InputActions.Attack, sequence: 9), resources);
        Assert.Equal((-1, ActionReason.NoStamina, Index("hit3"), (ushort)9),
            (refused.StartedIndex, refused.Refused, refused.RefusedIndex, refused.RefusedSequence));
        Assert.Equal((Index("hit2"), 10), (state.ActionIndex, state.Elapsed));

        // Stamina came back while hit 2 still runs: a new press in the window still reaches hit 3.
        resources.Stamina = 10;
        Step(state, Input(), resources);
        Assert.Equal(Index("hit3"), Step(state, Input(InputActions.Attack), resources).StartedIndex);
    }

    [Fact]
    public void A_kept_press_refused_on_the_link_tick_lets_the_hit_run_to_its_end()
    {
        var resources = new FakeResources(stamina: 10);
        var state = Hit1(resources);
        StepTo(state, 6, resources);
        Step(state, Input(InputActions.Attack), resources); // kept for hit 2
        Step(state, Input(), resources);
        StepTo(state, 9, resources);
        var link = Step(state, Input(), resources); // tick 10
        Assert.Equal((-1, ActionReason.NoStamina, Index("hit2")), (link.StartedIndex, link.Refused, link.RefusedIndex));
        Assert.Equal((Index("hit1"), -1), (state.ActionIndex, state.BufferedIndex));
        Assert.Equal(Index("hit1"), RunUntilEnded(state, resources));
    }

    [Fact]
    public void A_retune_returns_the_chain_to_hit_1()
    {
        var state = Hit1();
        StepTo(state, 7);
        Step(state, Input(InputActions.Attack));
        ActionMachine.Retune(state, ChainCatalog(), state.Loadout);
        Assert.Equal((ActionState.Idle, -1), (state.State, state.BufferedIndex));
        Step(state, Input());
        Assert.Equal(Index("hit1"), Step(state, Input(InputActions.Attack)).StartedIndex);
    }

    [Fact]
    public void An_ai_request_never_chains()
    {
        var state = new ActionMachineState(Catalog, ActionLoadout.None);
        Assert.Equal(Index("hit1"), Step(state, ActionInput.Request(Index("hit1"), 0)).StartedIndex);
        for (int i = 0; i < 13; i++)
            Assert.Equal(-1, Step(state, ActionInput.Request(Index("hit2"), 0)).StartedIndex);
        Assert.Equal((Index("hit1"), 13), (state.ActionIndex, state.Elapsed)); // the AI starts its next attack from Idle only
    }

    private static int RunUntilEnded(ActionMachineState state, IActionResources resources)
    {
        for (int i = 0; i < 40; i++)
        {
            var result = Step(state, Input(), resources);
            if (result.EndedIndex >= 0)
                return result.EndedIndex;
        }
        return -1;
    }
}
