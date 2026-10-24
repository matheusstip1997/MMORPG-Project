using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Tests.World;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Simulation;

/// <summary>
/// The shared pieces the monster AI (backlog F) and player respawn use: <see cref="FixedTrig.AngleOf"/>,
/// <see cref="MovementSim.StepAlong"/>, an action requested without buttons (<see cref="ActionInput.Request"/>), and
/// <see cref="ActionMachine.Revive"/>.
/// </summary>
public class MonsterSimulationTests
{
    private const ushort Step = 64; // one facing step: 1024 per turn

    [Theory]
    [InlineData(0, 100, 0)]          // +Y
    [InlineData(100, 0, 16384)]      // +X, a quarter turn clockwise
    [InlineData(0, -100, 32768)]
    [InlineData(-100, 0, 49152)]
    [InlineData(100, 100, 8192)]     // 45 degrees
    [InlineData(-100, -100, 40960)]
    [InlineData(-100, 100, 57344)]
    [InlineData(100, -100, 24576)]
    public void AngleOf_gives_the_binary_angle_of_the_axes_and_diagonals(long dx, long dy, int expected)
    {
        Assert.Equal((ushort)expected, FixedTrig.AngleOf(dx, dy));
    }

    [Fact]
    public void AngleOf_is_within_one_facing_step_of_atan2_all_around()
    {
        for (int degrees = 0; degrees < 360; degrees += 7)
        {
            double radians = degrees * Math.PI / 180;
            long dx = (long)Math.Round(Math.Sin(radians) * 100_000);
            long dy = (long)Math.Round(Math.Cos(radians) * 100_000);
            int expected = (int)Math.Round(degrees / 360.0 * 65536) & 0xFFFF;

            int angle = FixedTrig.AngleOf(dx, dy);
            int diff = Math.Abs((short)(angle - expected)); // wrap-around difference
            Assert.True(diff <= Step, $"{degrees} degrees: {angle}, expected about {expected}");
            Assert.Equal(0, angle % Step); // rounded to the table's step
        }
    }

    [Fact]
    public void AngleOf_handles_the_zero_vector_and_huge_vectors()
    {
        Assert.Equal(0, FixedTrig.AngleOf(0, 0));
        Assert.Equal(16384, FixedTrig.AngleOf(long.MaxValue / 2, 0));
        Assert.Equal(8192, FixedTrig.AngleOf(long.MaxValue / 2, long.MaxValue / 2));
    }

    [Fact]
    public void Along_the_angle_of_a_vector_points_the_same_way()
    {
        var target = FixVec2.FromCm(300, -700);
        ushort facing = FixedTrig.AngleOf(target.X, target.Y);
        var step = FixedTrig.Along(facing, FixedPoint.FromCm(100));

        // Same quadrant and within a degree of the direction: the cross product is small next to the lengths.
        Assert.True(step.X > 0 && step.Y < 0);
        double cross = (double)step.X * target.Y - (double)step.Y * target.X;
        double lengths = Math.Sqrt((double)step.X * step.X + (double)step.Y * step.Y)
            * Math.Sqrt((double)target.X * target.X + (double)target.Y * target.Y);
        Assert.True(Math.Abs(cross / lengths) < Math.Sin(Math.PI / 180));
    }

    [Fact]
    public void StepAlong_moves_one_tick_of_speed_along_the_facing()
    {
        var grid = GridCollisionTests.Build(new[] { "..........", "..........", "..........", ".........." });
        var start = new MovementState(FixVec2.FromCm(150, 150), 0);

        var east = MovementSim.StepAlong(start, 16384, false, 400, 20, grid);

        // 400 cm/s at 20 Hz: 20 cm per tick.
        Assert.Equal(FixVec2.FromCm(170, 150), east.Position);
        Assert.Equal(16384, east.Facing);
    }

    [Fact]
    public void StepAlong_does_nothing_while_an_action_runs()
    {
        var grid = GridCollisionTests.Build(new[] { "....", "...." });
        var start = new MovementState(FixVec2.FromCm(150, 50), 100);

        Assert.Equal(start, MovementSim.StepAlong(start, 16384, true, 400, 20, grid));
    }

    [Fact]
    public void StepAlong_stops_at_walls_with_the_shared_collision()
    {
        // Wall in column 2 ([200, 300) cm).
        var grid = GridCollisionTests.Build(new[] { "..#.", "..#." });
        var state = new MovementState(FixVec2.FromCm(150, 50), 0);
        for (int i = 0; i < 10; i++)
            state = MovementSim.StepAlong(state, 16384, false, 400, 20, grid);

        Assert.True(grid.IsWalkable(state.Position));
        Assert.Equal(200 * FixedPoint.RawPerCm - 1, state.Position.X);
    }

    [Fact]
    public void A_requested_action_starts_from_idle_and_locks_the_aim()
    {
        var state = new ActionMachineState(Catalog());
        int cut = Catalog().Find("skill.cut")!.Index;

        var result = Step(state, ActionInput.Request(cut, aim: 12345));

        Assert.Equal(cut, result.StartedIndex);
        Assert.Equal(12345, state.Facing);
        Assert.Equal(ActionState.Startup, state.State);
    }

    [Fact]
    public void A_requested_action_is_refused_on_cooldown_and_ignored_while_busy()
    {
        var state = new ActionMachineState(Catalog());
        int cut = Catalog().Find("skill.cut")!.Index;
        int attack = Catalog().Loadout(null).AttackIndex;
        Step(state, ActionInput.Request(cut, 0));

        // Busy: a request neither cancels nor buffers.
        var busy = Step(state, ActionInput.Request(attack, 0));
        Assert.Equal(-1, busy.StartedIndex);
        Assert.Equal(cut, state.ActionIndex);
        RunToIdle(state);
        Assert.Equal(-1, state.BufferedIndex);

        // Idle again, but the 3 s cooldown still runs.
        var refused = Step(state, ActionInput.Request(cut, 0));
        Assert.Equal(ActionReason.OnCooldown, refused.Refused);
        Assert.Equal(ActionState.Idle, state.State);
    }

    [Fact]
    public void A_requested_action_fires_its_effects_like_a_pressed_one()
    {
        var state = new ActionMachineState(Catalog());
        int fired = 0;
        var buffer = new FiredEffect[ActionMachine.MaxFiredPerStep];
        var result = ActionMachine.Step(state, ActionInput.Request(Catalog().Loadout(null).AttackIndex, 0), UnlimitedActionResources.Instance, buffer);
        while (state.IsBusy)
        {
            fired += result.FiredCount;
            result = ActionMachine.Step(state, NoInput(), UnlimitedActionResources.Instance, buffer);
        }

        Assert.Equal(2, fired); // the strike and the late effect
    }

    [Fact]
    public void No_request_is_the_default_input()
    {
        Assert.Equal(-1, default(ActionInput).RequestedAction);
        Assert.Equal(-1, NoInput().RequestedAction);
        Assert.Equal(-1, ActionInput.Request(-1, 0).RequestedAction);
    }

    [Fact]
    public void Revive_brings_a_dead_machine_back_idle_with_full_charges_and_no_cooldowns()
    {
        var state = new ActionMachineState(Catalog());
        Step(state, Input(InputActions.Dodge));
        Assert.Equal(2, state.Charges(Shared.Data.Tables.ChargePool.Dodge));
        ActionMachine.Kill(state);

        Assert.True(ActionMachine.Revive(state));

        Assert.Equal(ActionState.Idle, state.State);
        Assert.Equal(-1, state.ActionIndex);
        Assert.Equal(3, state.Charges(Shared.Data.Tables.ChargePool.Dodge));
        Assert.Equal(0, state.CooldownTicks(Catalog().Loadout(null).DodgeIndex));
        // It takes presses again.
        Assert.Equal(Catalog().Loadout(null).AttackIndex, Step(state, Input(InputActions.Attack)).StartedIndex);
    }

    [Fact]
    public void Revive_leaves_a_living_machine_alone()
    {
        var state = new ActionMachineState(Catalog());
        Step(state, Input(InputActions.Attack));

        Assert.False(ActionMachine.Revive(state));
        Assert.Equal(Catalog().Loadout(null).AttackIndex, state.ActionIndex);
    }
}
