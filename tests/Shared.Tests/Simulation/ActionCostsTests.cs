using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Simulation;

/// <summary>
/// Backlog D4, first part: stamina costs (R-3.1-02, R-3.1-04), dodge and parry charges (R-3.3-02, R-3.3-03) and the
/// i-frame window in the shared action machine, with their reason codes (spec 9.7).
/// </summary>
public class ActionCostsTests
{
    private static readonly ActionCatalog Catalog = ActionTestKit.Catalog();
    private static int Index(string id) => Catalog.Find(id)!.Index;

    /// <summary>Presses <paramref name="held"/> on a fresh press (releasing first) and returns that step's result.</summary>
    private static ActionStepResult Press(ActionMachineState state, InputActions held, IActionResources? resources = null,
        ushort sequence = 0)
    {
        Step(state, Input(), resources);
        return Step(state, Input(held, sequence: sequence), resources);
    }

    [Fact]
    public void The_fourth_dodge_in_a_row_is_refused_for_lack_of_a_charge()
    {
        var state = new ActionMachineState(Catalog);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(Index("dodge"), Press(state, InputActions.Dodge).StartedIndex);
            Assert.Equal(2 - i, state.Charges(ChargePool.Dodge));
            RunToIdle(state);
        }
        var refused = Press(state, InputActions.Dodge, sequence: 77);
        Assert.Equal((-1, ActionReason.DodgeNoCharge, Index("dodge"), (ushort)77),
            (refused.StartedIndex, refused.Refused, refused.RefusedIndex, refused.RefusedSequence));
        Assert.False(state.IsBusy);
        Assert.Equal(3, state.Charges(ChargePool.Parry)); // its own pool (R-3.3-02)
    }

    [Fact]
    public void A_dodge_charge_comes_back_after_the_recharge_time()
    {
        // Spec 12.7: one dodge charge every 4 s, 80 ticks at 20 Hz, counted from the first spend.
        var state = new ActionMachineState(Catalog);
        Assert.Equal(80, state.DodgeCharges.RechargeTicks);
        uint firstSpend = 0;
        for (int i = 0; i < 3; i++)
        {
            Press(state, InputActions.Dodge);
            if (i == 0)
                firstSpend = state.Tick;
            RunToIdle(state);
        }
        while (state.Tick < firstSpend + 78)
            Step(state, Input());
        Assert.Equal(ActionReason.DodgeNoCharge, Step(state, Input(InputActions.Dodge)).Refused); // tick firstSpend + 79
        Assert.Equal(Index("dodge"), Press(state, InputActions.Dodge).StartedIndex); // tick firstSpend + 81
        Assert.Equal(0, state.Charges(ChargePool.Dodge));
    }

    [Fact]
    public void A_parry_spends_its_charge_when_it_starts()
    {
        // R-3.3-03: spent on the start, even if the parry later fails; nothing gives it back.
        var state = new ActionMachineState(Catalog);
        Assert.Equal(Index("parry"), Press(state, InputActions.Parry).StartedIndex);
        Assert.Equal(ActionState.Startup, state.State);
        Assert.Equal(2, state.Charges(ChargePool.Parry));
        RunToIdle(state);
        Assert.Equal(2, state.Charges(ChargePool.Parry));
        Assert.Equal(3, state.Charges(ChargePool.Dodge));

        Press(state, InputActions.Parry);
        RunToIdle(state);
        Press(state, InputActions.Parry);
        RunToIdle(state);
        Assert.Equal(ActionReason.ParryNoCharge, Press(state, InputActions.Parry).Refused);
    }

    [Fact]
    public void A_buffered_dodge_with_no_charge_left_is_refused_when_it_would_start()
    {
        var state = new ActionMachineState(Catalog);
        Press(state, InputActions.Dodge);
        RunToIdle(state);
        Press(state, InputActions.Dodge);
        RunToIdle(state);
        Press(state, InputActions.Dodge); // the last charge
        var dodge = Catalog.Find("dodge")!;
        while (dodge.TotalTicks - state.Elapsed > dodge.BufferWindowTicks)
            Step(state, Input());
        Step(state, Input(InputActions.Dodge, sequence: 9)); // buffered
        Assert.Equal(Index("dodge"), state.BufferedIndex);

        ActionStepResult result;
        do
            result = Step(state, Input(InputActions.Dodge));
        while (result.EndedIndex < 0);
        Assert.Equal((-1, ActionReason.DodgeNoCharge, (ushort)9), (result.StartedIndex, result.Refused, result.RefusedSequence));
        Assert.False(state.IsBusy);
    }

    [Fact]
    public void A_cancel_without_a_charge_is_refused_and_the_action_goes_on()
    {
        var state = new ActionMachineState(Catalog);
        for (int i = 0; i < 3; i++)
        {
            Press(state, InputActions.Dodge);
            RunToIdle(state);
        }
        Press(state, InputActions.Attack);
        while (state.State != ActionState.Recovery)
            Step(state, Input());
        var result = Step(state, Input(InputActions.Dodge)); // dodge cancels the attack's recovery, but has no charge
        Assert.Equal((ActionReason.DodgeNoCharge, -1, -1), (result.Refused, result.StartedIndex, result.CancelledIndex));
        Assert.Equal(Index("attack"), state.ActionIndex);
    }

    [Fact]
    public void Starting_spends_the_stamina_cost_and_too_little_stamina_is_refused()
    {
        // R-3.1-04: no heavy attacks; the normal attack costs stamina like skills do.
        var stamina = new FakeResources(15);
        var state = new ActionMachineState(Catalog);
        Assert.Equal(Index("attack"), Press(state, InputActions.Attack, stamina).StartedIndex);
        Assert.Equal(5, stamina.Stamina);
        RunToIdle(state);

        var refused = Press(state, InputActions.Attack, stamina, sequence: 4);
        Assert.Equal((ActionReason.NoStamina, Index("attack"), (ushort)4), (refused.Refused, refused.RefusedIndex, refused.RefusedSequence));
        Assert.Equal(5, stamina.Stamina);
        Assert.Equal(new[] { 10, 10 }, stamina.Requests);
        Assert.False(state.IsBusy);
    }

    [Fact]
    public void A_refusal_for_stamina_spends_no_charge_and_starts_no_cooldown()
    {
        var stamina = new FakeResources(0);
        var state = new ActionMachineState(Catalog);
        Assert.Equal(ActionReason.NoStamina, Press(state, InputActions.Dodge, stamina).Refused);
        Assert.Equal(3, state.Charges(ChargePool.Dodge));

        var slot = Step(state, Input(slot: 1), stamina); // skill.cut, 3 s cooldown
        Assert.Equal(ActionReason.NoStamina, slot.Refused);
        Assert.Equal(0, state.CooldownTicks(Index("skill.cut")));
    }

    [Fact]
    public void Cooldown_is_checked_first_then_the_charge_then_the_stamina()
    {
        var stamina = new FakeResources(1_000);
        var state = new ActionMachineState(Catalog);
        Step(state, Input(slot: 1), stamina);
        RunToIdle(state);
        Step(state, Input(), stamina);
        stamina.Requests.Clear();
        Assert.Equal(ActionReason.OnCooldown, Step(state, Input(slot: 1), stamina).Refused);
        Assert.Empty(stamina.Requests); // never asked for stamina

        for (int i = 0; i < 3; i++)
        {
            Press(state, InputActions.Dodge, stamina);
            RunToIdle(state);
        }
        stamina.Requests.Clear();
        stamina.Stamina = 0;
        Assert.Equal(ActionReason.DodgeNoCharge, Press(state, InputActions.Dodge, stamina).Refused);
        Assert.Empty(stamina.Requests);
    }

    [Fact]
    public void A_skill_short_of_mp_is_refused_with_NoMp_and_spends_neither_stamina_nor_mp()
    {
        // R-3.1-04: skills also cost MP. The kit's skill.cut costs 10 stamina and 2 MP; 1 MP is not enough.
        var resources = new FakeResources(stamina: 100, mp: 1);
        var state = new ActionMachineState(Catalog);
        var refused = Step(state, Input(slot: 1, sequence: 6), resources);
        Assert.Equal((ActionReason.NoMp, Index("skill.cut"), (ushort)6, (byte)1),
            (refused.Refused, refused.RefusedIndex, refused.RefusedSequence, refused.RefusedSlot));
        Assert.Equal((100, 1), (resources.Stamina, resources.Mp)); // a refusal changes nothing
        Assert.Equal(0, state.CooldownTicks(Index("skill.cut")));
        Assert.False(state.IsBusy);

        resources.Mp = 2;
        Step(state, Input(), resources);
        Assert.Equal(Index("skill.cut"), Step(state, Input(slot: 1), resources).StartedIndex);
        Assert.Equal((90, 0), (resources.Stamina, resources.Mp)); // both spent together
    }

    [Fact]
    public void Stamina_is_checked_before_mp()
    {
        // The start order (spec 3.1, D4 costs): cooldown, charge, stamina, then MP.
        var resources = new FakeResources(stamina: 0, mp: 0);
        var state = new ActionMachineState(Catalog);
        Assert.Equal(ActionReason.NoStamina, Step(state, Input(slot: 1), resources).Refused);
        Assert.Equal([10], resources.Requests);
        Assert.Equal([2], resources.MpRequests);
    }

    [Fact]
    public void A_normal_attack_and_the_dodge_ask_for_no_mp()
    {
        // R-3.1-04: normal attacks, the dodge and the parry cost no MP.
        var resources = new FakeResources(stamina: 100, mp: 0);
        var state = new ActionMachineState(Catalog);
        Assert.Equal(Index("attack"), Press(state, InputActions.Attack, resources).StartedIndex);
        RunToIdle(state);
        Assert.Equal(Index("dodge"), Press(state, InputActions.Dodge, resources).StartedIndex);
        Assert.Equal([0, 0], resources.MpRequests);
    }

    [Fact]
    public void An_action_without_a_cost_never_asks_for_stamina()
    {
        var stamina = new FakeResources(0);
        var state = new ActionMachineState(Catalog);
        Assert.Equal(Index("parry"), Press(state, InputActions.Parry, stamina).StartedIndex); // the kit's parry costs 0
        Assert.Empty(stamina.Requests);
    }

    [Fact]
    public void The_dodge_has_i_frames_over_its_window_only()
    {
        // R-3.3-02. The kit's dodge: 50/250/200 ms = 1/5/4 ticks, i-frames over Active (50 ms for 250 ms).
        var dodge = Catalog.Find("dodge")!;
        Assert.Equal((1, 6), (dodge.IFramesStart, dodge.IFramesEnd));
        var state = new ActionMachineState(Catalog);
        Assert.False(state.InIFrames);
        Press(state, InputActions.Dodge);
        var covered = new List<int>();
        while (state.IsBusy)
        {
            if (state.InIFrames)
                covered.Add(state.Elapsed);
            Step(state, Input());
        }
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, covered);

        Press(state, InputActions.Attack);
        while (state.IsBusy)
        {
            Assert.False(state.InIFrames);
            Step(state, Input());
        }
    }

    [Fact]
    public void I_frame_ticks_round_up_and_stay_inside_the_action()
    {
        var odd = Action("odd", 1, ActionKind.Dodge, 30, 100, 20);
        odd.IFramesStartMs = 30;
        odd.IFramesMs = 120; // ends at 150 ms = 3 ticks; the action is 1 + 2 + 1 = 4 ticks
        var timing = new ActionCatalog(TickRateHz, [odd], [], (ActionBindings?)null, DodgePool, ParryPool)[0];
        Assert.Equal((1, 4), (timing.IFramesStart, timing.IFramesEnd));
        odd.IFramesStartMs = 0;
        odd.IFramesMs = 150;
        timing = new ActionCatalog(TickRateHz, [odd], [], (ActionBindings?)null, DodgePool, ParryPool)[0];
        Assert.Equal((0, 3), (timing.IFramesStart, timing.IFramesEnd));
    }

    [Fact]
    public void A_catalog_needs_charges_for_every_pool_its_actions_use()
    {
        var dodge = Action("dodge", 2, ActionKind.Dodge, 50, 250, 200);
        Assert.Throws<ArgumentException>(() => new ActionCatalog(TickRateHz, [dodge], [], (ActionBindings?)null));
        Assert.Throws<ArgumentException>(() => new ActionCatalog(TickRateHz, [dodge], [], (ActionBindings?)null, default, ParryPool));
        Assert.Equal(80, new ActionCatalog(TickRateHz, [dodge], [], (ActionBindings?)null, DodgePool).NewMeter(ChargePool.Dodge).RechargeTicks);
    }

    [Fact]
    public void Copies_carry_the_charges_and_the_tick()
    {
        var state = new ActionMachineState(Catalog);
        Press(state, InputActions.Dodge);
        var copy = new ActionMachineState(Catalog);
        copy.CopyFrom(state);
        Assert.True(copy.SameAs(state));
        Assert.Equal((state.Tick, 2), (copy.Tick, copy.Charges(ChargePool.Dodge)));

        // Two idle machines at the same tick that differ only in a spent parry charge behave differently.
        var parried = new ActionMachineState(Catalog);
        var waited = new ActionMachineState(Catalog);
        Press(parried, InputActions.Parry);
        int steps = RunToIdle(parried);
        Press(waited, InputActions.None);
        for (int i = 0; i < steps; i++)
            Step(waited, Input());
        Assert.Equal((parried.Tick, parried.State), (waited.Tick, waited.State));
        Assert.False(parried.SameAs(waited));

        state.Reset();
        Assert.Equal((0u, 3, 3), (state.Tick, state.Charges(ChargePool.Dodge), state.Charges(ChargePool.Parry)));
    }
}
