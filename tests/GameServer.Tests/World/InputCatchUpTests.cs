using GameServer.Entities;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Util;
using static GameServer.Tests.Support.TestWorlds;
using ProtoKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// Spec 1.6 input catch-up (fix/parry-feel; bug fix approved by the orchestrator 2026-10-26). One frame in and one out
/// per tick never drains a burst, so before the catch-up every jitter or client hitch left frames queued for good: that
/// player's inputs ran later, its view lag grew past the clock-sync limit (R-3.1-03) and parries were judged late.
/// Measured on main: +1 frame on LAN with a 60 fps client, +2 to +4 at rtt100; in-window parry presses that succeeded
/// fell from 25/28 to 2/28.
/// </summary>
public class InputCatchUpTests
{
    private const int StepCm = 20; // 400 cm/s at 20 Hz

    [Fact]
    public void A_burst_drains_back_to_one_frame_per_tick_within_the_backlog_ticks()
    {
        var settings = Settings();
        var harness = new WorldHarness(settings);
        var player = harness.Join(1);
        ushort sequence = 0;
        Steady(harness, ref sequence, 5);

        Burst(harness, ref sequence, extra: 2); // two frames more than the tick takes
        Assert.Equal(2, player.Inputs.Count);

        int ticks = 0;
        while (player.Inputs.Count > 0 && ticks < 50)
        {
            Steady(harness, ref sequence, 1);
            ticks++;
        }
        Assert.Equal(0, player.Inputs.Count); // a frame arrives each tick and is applied that tick again
        Assert.InRange(ticks, settings.InputBacklogTicks, settings.InputBacklogTicks + 2);
        Assert.Equal(2, harness.World.InputCatchUpFrames);
        Assert.Equal(sequence, player.LastInputSequence);
    }

    [Fact]
    public void Without_catch_up_the_burst_stays_queued_the_bug_on_main()
    {
        var harness = new WorldHarness(Settings(maxInputFramesPerTick: 1));
        var player = harness.Join(1);
        ushort sequence = 0;
        Burst(harness, ref sequence, extra: 2);

        Steady(harness, ref sequence, 100);

        Assert.Equal(2, player.Inputs.Count);
        Assert.Equal(sequence - 2, player.LastInputSequence);
    }

    [Fact]
    public void Movement_stays_continuous_through_catch_up_ticks_without_a_speed_violation()
    {
        var harness = new WorldHarness(Settings());
        var player = harness.Join(1);
        int startX = player.XCm;
        ushort sequence = 0;

        Burst(harness, ref sequence, extra: 3, ProtoKeys.D);
        Steady(harness, ref sequence, 30, ProtoKeys.D);

        Assert.Equal(0, player.Inputs.Count);
        Assert.Equal(3, harness.World.InputCatchUpFrames);
        // Every frame moved one step, as the client predicted them, and none counted as a speed violation.
        Assert.Equal(startX + sequence * StepCm, player.XCm);
        Assert.Equal(0, harness.World.SpeedViolations);
    }

    [Fact]
    public void A_press_in_a_caught_up_frame_starts_the_parry_on_that_frames_view_clock()
    {
        var tables = TestCombat.Tables();
        var harness = new WorldHarness(Settings(inputBacklogTicks: 1), map: TestCombat.OpenMap(),
            actions: ActionCatalog.From(tables), combat: TestCombat.Rules(tables));
        var player = harness.Join(1);
        harness.Router.OnRoundTripTime(1, 100); // 100 + 100 + 50 ms: the 2-tick lag is within the limit
        harness.Ticks(3);
        uint tick = harness.World.ServerTick;
        var plain = Frame(1);
        plain.ViewTick = tick - 2;
        var press = Frame(2, actions: InputActions.Parry);
        press.ViewTick = tick - 2;
        press.SubTick = 40;
        var later = Frame(3);
        later.ViewTick = tick - 1;
        harness.SendInputs(1, later, press, plain);

        harness.World.Tick(); // applies the plain frame and, catching up, the press

        Assert.Equal(2, player.LastInputSequence);
        Assert.Equal(ActionKind.Parry, player.Actions.Action?.Kind);
        Assert.Equal(1, player.Defence.Count);
        Assert.Equal((long)(tick - 2) * 64 + 40, player.Defence[0].ViewStart64);
    }

    /// <summary>
    /// The parry timing measurement as a regression (LAN with a 60 fps client, and rtt100 with 5 ms jitter): a client that
    /// runs its 20 Hz ticks on 60 fps frames with a hitch every 61 frames, its frames delayed one way and seeded jitter.
    /// The view lag the server judges a press with must stay within what the link alone gives it (the lag were the frame
    /// applied on the first tick after it arrived, clamped by the clock-sync limit): no queued backlog adds to it once a
    /// burst drained. The same run without catch-up keeps the backlog and judges presses up to ticks late.
    /// </summary>
    [Theory]
    [InlineData(0, 3)] // LAN: loopback and OS timers jitter a few ms
    [InlineData(50, 5)] // rtt100
    public void The_judged_view_lag_stays_at_what_the_link_gives_through_client_hitches(int oneWayMs, int jitterMs)
    {
        var withCatchUp = LinkRun(oneWayMs, jitterMs, Settings());
        var without = LinkRun(oneWayMs, jitterMs, Settings(maxInputFramesPerTick: 1));

        Assert.True(withCatchUp.CatchUpFrames > 0, "the hitches made bursts");
        // After a burst the backlog drains within the backlog ticks; outside those windows no frame is judged late.
        Assert.True(withCatchUp.LateTicksMax <= 0.05, $"judged {withCatchUp.LateTicksMax:0.00} ticks later than the link allows");
        Assert.True(without.LateTicksMax >= 0.9, $"control: {without.LateTicksMax:0.00} ticks late without catch-up");
    }

    private sealed record LinkResult(double LateTicksMax, long CatchUpFrames);

    private static LinkResult LinkRun(int oneWayMs, int jitterMs, WorldSettings settings)
    {
        const double TickMs = 50, FrameMs = 1000.0 / 60;
        const int InterpolationMs = 100, ToleranceMs = 50, CapTicks = 4, Ticks = 1200, WarmupTicks = 40;
        var tables = TestCombat.Tables();
        var harness = new WorldHarness(settings, map: TestCombat.OpenMap(), actions: ActionCatalog.From(tables),
            combat: TestCombat.Rules(tables));
        var player = harness.Join(1);
        int rtt = 2 * oneWayMs;
        harness.Router.OnRoundTripTime(1, rtt);
        double limitTicks = Math.Min(CapTicks, (rtt + InterpolationMs + ToleranceMs) * 20 / 1000.0);
        uint baseTick = harness.World.ServerTick; // world time 0

        // The client: frames on a 60 fps clock with a hitch, ticks paced by TickClock's rule, one frame sent per tick.
        var rng = new Pcg32Rng(7);
        var packets = new List<(double ArriveMs, InputFrame Frame, double IdealLagTicks)>();
        double now = 0, accumulated = 0, lastArrive = 0;
        ushort sequence = 0;
        int frameIndex = 0;
        while (now < (Ticks + 10) * TickMs)
        {
            double delta = ++frameIndex % 61 == 0 ? 70 : FrameMs;
            now += delta;
            accumulated += delta;
            for (int t = 0; t < 3 && accumulated >= TickMs; t++)
            {
                accumulated -= TickMs;
                // What the screen shows: the previous frame (PredictionDevClient advances its interpolation clock after the
                // ticks), the world one way plus the interpolation delay before it.
                double view = Math.Max(1, baseTick + (now - delta - oneWayMs - InterpolationMs) / TickMs);
                var frame = Frame(++sequence, ProtoKeys.D);
                frame.ViewTick = (uint)Math.Floor(view);
                frame.SubTick = (byte)Math.Min(63, Math.Floor((view - frame.ViewTick) * 64));
                double arrive = Math.Max(lastArrive, now + oneWayMs + (jitterMs > 0 ? rng.NextInt(jitterMs + 1) : 0));
                lastArrive = arrive;
                // Applied on the first tick at or after its arrival, if nothing were queued.
                double idealTick = baseTick + Math.Ceiling(arrive / TickMs);
                double clockOfFrame = frame.ViewTick + frame.SubTick / 64.0;
                packets.Add((arrive, frame, idealTick - clockOfFrame));
            }
        }

        double lateMax = 0;
        int next = 0;
        harness.World.PhaseStarted = phase =>
        {
            if (phase != TickPhase.Movement || !player.HasInputThisTick || harness.World.ServerTick < baseTick + WarmupTicks)
                return;
            var applied = packets[player.LastInputSequence - 1];
            double ideal = Math.Min(applied.IdealLagTicks, limitTicks);
            double judged = player.Defence.Lag64 / 64.0;
            // Judged "later" is a smaller lag than the link gives: the press counts as made later than it was.
            double trueLag = harness.World.ServerTick - (applied.Frame.ViewTick + applied.Frame.SubTick / 64.0);
            double late = (trueLag - judged) - (applied.IdealLagTicks - ideal);
            if (!InDrainWindow(player, settings))
                lateMax = Math.Max(lateMax, late);
        };
        for (int k = 1; k <= Ticks; k++)
        {
            while (next < packets.Count && packets[next].ArriveMs <= k * TickMs)
                harness.SendInputs(1, packets[next++].Frame);
            harness.World.Tick();
        }
        return new LinkResult(lateMax, harness.World.InputCatchUpFrames);
    }

    /// <summary>A backlog that has not waited its InputBacklogTicks yet, or is draining.</summary>
    private static bool InDrainWindow(Player player, WorldSettings settings) =>
        settings.MaxInputFramesPerTick > 1 && player.InputBacklogStreak > 0;

    private static void Steady(WorldHarness harness, ref ushort sequence, int ticks, ProtoKeys keys = ProtoKeys.None)
    {
        for (int i = 0; i < ticks; i++)
        {
            harness.SendInputs(1, Frame(++sequence, keys));
            harness.World.Tick();
        }
    }

    /// <summary>One tick that gets <paramref name="extra"/> frames more than it takes (a client hitch catching up).</summary>
    private static void Burst(WorldHarness harness, ref ushort sequence, int extra, ProtoKeys keys = ProtoKeys.None)
    {
        for (int i = 0; i <= extra; i++)
            harness.SendInputs(1, Frame(++sequence, keys));
        harness.World.Tick();
    }
}
