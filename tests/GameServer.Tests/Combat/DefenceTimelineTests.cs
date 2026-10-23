using GameServer.Combat;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace GameServer.Tests.Combat;

/// <summary>
/// The defender's timeline (R-3.1-03, R-3.3-02): the clock-sync limits on the view clock a client claims, and dodge and
/// parry windows judged on it with sub-tick precision. Repository tables: 20 Hz, cap 200 ms (4 ticks), interpolation
/// 100 ms, tolerance 50 ms; the dodge is 1 tick of startup then i-frames on ticks 1-5 of 10; the parry window is ticks 1-4.
/// </summary>
public class DefenceTimelineTests
{
    private const int Sub = DefenceTimeline.SubTicks;
    private const int NoRtt = -1;
    private const uint Start = 100;

    private readonly ActionCatalog _catalog = ActionCatalog.From(TestCombat.Tables());
    private readonly ClockSyncLimits _limits = TestCombat.Rules().ClockSync;

    [Fact]
    public void The_repository_limits_are_the_cap_and_round_trip_plus_interpolation_plus_tolerance()
    {
        Assert.Equal(4 * Sub, _limits.MaxLag64(NoRtt)); // no sample: only the cap
        Assert.Equal(192, _limits.MaxLag64(0)); // 150 ms = 3 ticks
        Assert.Equal(224, _limits.MaxLag64(25)); // 175 ms = 3.5 ticks
        Assert.Equal(4 * Sub, _limits.MaxLag64(1_000)); // never above the cap
    }

    [Fact]
    public void A_frame_sets_the_view_lag_with_its_sub_tick()
    {
        var timeline = new DefenceTimeline();
        timeline.OnFrame(Start, Start - 3, 16, NoRtt, _limits);
        Assert.Equal(3 * Sub - 16, timeline.Lag64);
        Assert.Equal(2, timeline.LagTicks);
        Assert.Equal(3, timeline.LagTicksCeil);
    }

    [Theory]
    [InlineData(Start, 0)] // the tick being applied has not been sent yet
    [InlineData(Start + 5, 0)]
    [InlineData(Start - 1, 63)] // 1/64 of a tick before the applied tick is fine
    public void No_view_or_a_view_of_the_future_gives_no_lag(uint viewTick, byte subTick)
    {
        var timeline = new DefenceTimeline();
        timeline.OnFrame(Start, viewTick, subTick, NoRtt, _limits);
        if (viewTick == Start - 1)
        {
            Assert.Equal(1, timeline.Lag64);
            return;
        }
        Assert.Equal(0, timeline.Lag64);
        Assert.Equal(1, timeline.FutureClaims);

        timeline.OnFrame(Start, 0, 0, NoRtt, _limits); // no view yet
        Assert.Equal(0, timeline.Lag64);
    }

    [Fact]
    public void The_view_clock_never_goes_back()
    {
        var timeline = new DefenceTimeline();
        timeline.OnFrame(Start, Start - 2, 0, NoRtt, _limits);
        timeline.OnFrame(Start + 1, Start - 3, 0, NoRtt, _limits); // claims an older view than the last frame
        Assert.Equal(1, timeline.BackwardClaims);
        Assert.Equal(3 * Sub, timeline.Lag64); // raised to the last view: Start - 2
    }

    [Fact]
    public void A_claimed_lag_beyond_round_trip_interpolation_and_tolerance_is_clamped()
    {
        var timeline = new DefenceTimeline();
        timeline.OnFrame(Start, Start - 4, 0, roundTripMs: 0, _limits);
        Assert.Equal(192, timeline.Lag64); // 150 ms
        Assert.Equal(1, timeline.TooOldClaims);

        var unknown = new DefenceTimeline();
        unknown.OnFrame(Start, Start - 4, 0, NoRtt, _limits);
        Assert.Equal(4 * Sub, unknown.Lag64); // only the cap until a round trip is measured
        Assert.Equal(0, unknown.TooOldClaims);
    }

    [Fact]
    public void Whatever_the_client_claims_and_its_round_trip_the_lag_stays_within_200_ms()
    {
        var timeline = new DefenceTimeline();
        timeline.OnFrame(Start, Start - 50, 0, roundTripMs: 5_000, _limits);
        Assert.Equal(4 * Sub, timeline.Lag64);
        Assert.Equal(4, timeline.LagTicksCeil);
    }

    [Fact]
    public void Reset_forgets_the_view_and_the_starts()
    {
        var (timeline, _) = Dodge(Start - 3, 0);
        timeline.Reset();
        Assert.Equal(0, timeline.Lag64);
        Assert.Equal(0, timeline.Count);
        timeline.OnFrame(Start + 1, Start - 10, 0, NoRtt, _limits); // older than the view before the reset: allowed
        Assert.Equal(0, timeline.BackwardClaims);
    }

    [Theory]
    // The dodge started on view tick Start - 3 (+ sub/64); hit ticks H with e = (H - Start + 3) * 64 - sub.
    [InlineData(0, Start - 2, true)] // e = 64: the first i-frame tick, exactly
    [InlineData(1, Start - 2, false)] // e = 63: 1/64 earlier, still in startup
    [InlineData(0, Start + 2, true)] // e = 320: tick 5, the last i-frame tick
    [InlineData(1, Start + 3, true)] // e = 383: 1/64 before the window closes
    [InlineData(0, Start + 3, false)] // e = 384: tick 6, just after the i-frames
    [InlineData(0, Start - 4, false)] // before the dodge started
    public void A_dodge_covers_a_hit_exactly_inside_its_i_frames_on_the_view_clock(byte subTick, uint hitTick, bool dodged)
    {
        var (timeline, dodge) = Dodge(Start - 3, subTick);
        bool covered = timeline.TryJudge(hitTick, _catalog, out var start, out int elapsed);
        Assert.Equal(dodged, covered && dodge.HasIFramesAt(elapsed));
        if (covered)
            Assert.Equal(dodge.Index, start.ActionIndex);
    }

    [Theory]
    [InlineData(Start - 2, ActionState.Active)] // tick 1: the window opens
    [InlineData(Start + 1, ActionState.Active)] // tick 4: its last tick
    [InlineData(Start + 2, ActionState.Recovery)] // too early a press
    [InlineData(Start - 3, ActionState.Startup)] // too late a press
    public void A_parry_window_is_its_active_phase_on_the_view_clock(uint hitTick, ActionState phase)
    {
        var timeline = new DefenceTimeline();
        var machine = new ActionMachineState(_catalog);
        timeline.OnFrame(Start, Start - 3, 0, NoRtt, _limits);
        Press(machine, timeline, InputActions.Parry, Start);

        Assert.True(timeline.TryJudge(hitTick, _catalog, out var start, out int elapsed));
        Assert.Equal(phase, _catalog[start.ActionIndex].PhaseAt(elapsed));
    }

    [Fact]
    public void A_dodge_cut_short_by_a_stagger_covers_only_the_ticks_it_ran()
    {
        var timeline = new DefenceTimeline();
        var machine = new ActionMachineState(_catalog);
        Press(machine, timeline, InputActions.Dodge, Start); // lag 0: the view clock is the server's
        Press(machine, timeline, InputActions.Dodge, Start + 1);
        Press(machine, timeline, InputActions.Dodge, Start + 2);
        ActionMachine.Interrupt(machine, InterruptKind.PoiseBreak, 1_000); // phase 5 of Start + 2
        Press(machine, timeline, InputActions.None, Start + 3);

        Assert.True(timeline[0].Ended);
        Assert.Equal(3, timeline[0].RanTicks);
        Assert.True(timeline.TryJudge(Start + 2, _catalog, out _, out int elapsed));
        Assert.Equal(2, elapsed);
        Assert.False(timeline.TryJudge(Start + 3, _catalog, out _, out _));
    }

    [Fact]
    public void A_buffered_start_is_placed_with_the_lag_of_the_tick_it_starts_on()
    {
        var timeline = new DefenceTimeline();
        var machine = new ActionMachineState(_catalog);
        var attack = _catalog[_catalog.AttackIndex];
        timeline.OnFrame(Start, Start - 1, 0, NoRtt, _limits);
        Press(machine, timeline, InputActions.Attack, Start);
        uint tick = Start;
        while (attack.TotalTicks - machine.Elapsed > attack.BufferWindowTicks)
            Press(machine, timeline, InputActions.None, ++tick);
        Press(machine, timeline, InputActions.Parry, ++tick); // within the buffer window: kept
        Assert.Equal(_catalog.ParryIndex, machine.BufferedIndex);
        while (machine.ActionIndex == attack.Index && attack.TotalTicks - machine.Elapsed > 1)
            Press(machine, timeline, InputActions.Parry, ++tick);
        Assert.Equal(0, timeline.Count);
        timeline.OnFrame(tick + 1, tick - 2, 0, NoRtt, _limits); // the lag grew to 3 ticks
        Press(machine, timeline, InputActions.Parry, tick + 1); // the attack ends and the parry starts

        Assert.Equal(1, timeline.Count);
        Assert.Equal(_catalog.ParryIndex, timeline[0].ActionIndex);
        Assert.Equal((long)(tick + 1) * Sub - 3 * Sub, timeline[0].ViewStart64);
    }

    [Fact]
    public void A_parry_refused_for_want_of_a_charge_still_marks_its_would_be_window()
    {
        var timeline = new DefenceTimeline();
        var parry = _catalog[_catalog.ParryIndex];
        timeline.RefusedNoCharge(parry, Start, sequence: 9);

        Assert.True(timeline.TryJudge(Start + 1, _catalog, out var start, out int elapsed));
        Assert.True(start.NoCharge);
        Assert.Equal(9, start.Sequence);
        Assert.Equal(ActionState.Active, parry.PhaseAt(elapsed));
    }

    [Fact]
    public void The_ring_keeps_the_newest_starts()
    {
        var timeline = new DefenceTimeline();
        var parry = _catalog[_catalog.ParryIndex];
        for (uint i = 0; i < DefenceTimeline.Capacity + 2; i++)
            timeline.RefusedNoCharge(parry, Start + i * 20, (ushort)i);
        Assert.Equal(DefenceTimeline.Capacity, timeline.Count);
        Assert.Equal(DefenceTimeline.Capacity + 1, timeline[0].Sequence);
        Assert.Equal(2, timeline[DefenceTimeline.Capacity - 1].Sequence);
    }

    /// <summary>A dodge started at <see cref="Start"/> by a frame that saw <paramref name="viewTick"/> + sub/64.</summary>
    private (DefenceTimeline Timeline, ActionTiming Dodge) Dodge(uint viewTick, byte subTick)
    {
        var timeline = new DefenceTimeline();
        var machine = new ActionMachineState(_catalog);
        timeline.OnFrame(Start, viewTick, subTick, NoRtt, _limits);
        Press(machine, timeline, InputActions.Dodge, Start);
        Assert.Equal(1, timeline.Count);
        return (timeline, _catalog[_catalog.DodgeIndex]);
    }

    private static void Press(ActionMachineState machine, DefenceTimeline timeline, InputActions held, uint tick)
    {
        var fired = new FiredEffect[ActionMachine.MaxFiredPerStep];
        var result = ActionMachine.Step(machine, new ActionInput(true, held, 0, 0, (ushort)tick), UnlimitedActionResources.Instance, fired);
        timeline.Step(machine, tick, result.StartedIndex, (ushort)tick);
    }
}
