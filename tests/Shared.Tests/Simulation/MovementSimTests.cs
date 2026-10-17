using Shared.Simulation;
using Shared.Util;

namespace Shared.Tests.Simulation;

public class MovementSimTests
{
    // StartingValuesM1 (data/tables/StartingValuesM1.json).
    private const int SpeedCmPerSec = 400;
    private const int TickRateHz = 20;

    // 400 cm/s at 20 Hz = 20 cm per tick = 5120 raw; diagonal axis = 5120 * 46340 >> 16 = 3620.
    private const int StraightStep = 5120;
    private const int DiagonalAxisStep = 3620;

    private static readonly MovementState Origin = new MovementState(FixVec2.Zero, 0);

    [Theory]
    [InlineData(MoveKeys.W, 0, StraightStep)]
    [InlineData(MoveKeys.S, 0, -StraightStep)]
    [InlineData(MoveKeys.D, StraightStep, 0)]
    [InlineData(MoveKeys.A, -StraightStep, 0)]
    [InlineData(MoveKeys.W | MoveKeys.D, DiagonalAxisStep, DiagonalAxisStep)]
    [InlineData(MoveKeys.S | MoveKeys.A, -DiagonalAxisStep, -DiagonalAxisStep)]
    public void Each_key_moves_one_step_in_its_direction(MoveKeys keys, int expectedX, int expectedY)
    {
        var next = Step(Origin, keys, facing: 0, actionActive: false);

        Assert.Equal(new FixVec2(expectedX, expectedY), next.Position);
    }

    [Theory]
    [InlineData(MoveKeys.W | MoveKeys.S)]
    [InlineData(MoveKeys.A | MoveKeys.D)]
    [InlineData(MoveKeys.W | MoveKeys.A | MoveKeys.S | MoveKeys.D)]
    [InlineData(MoveKeys.None)]
    public void Opposite_keys_cancel(MoveKeys keys)
    {
        var next = Step(Origin, keys, facing: 0, actionActive: false);

        Assert.Equal(FixVec2.Zero, next.Position);
    }

    [Fact]
    public void Three_keys_move_along_the_remaining_axis_at_straight_speed()
    {
        var next = Step(Origin, MoveKeys.W | MoveKeys.A | MoveKeys.D, facing: 0, actionActive: false);

        Assert.Equal(new FixVec2(0, StraightStep), next.Position);
    }

    [Fact]
    public void Facing_follows_the_input_when_no_action_is_active()
    {
        var next = Step(Origin, MoveKeys.None, facing: 16384, actionActive: false);

        Assert.Equal(16384, next.Facing);
    }

    [Fact]
    public void Diagonal_is_never_faster_than_straight_for_any_configurable_speed()
    {
        // StartingValuesM1 bounds: MoveSpeedCmPerSec 1..10000, TickRateHz 1..120.
        foreach (int hz in new[] { 1, 7, 20, 30, 60, 120 })
        {
            for (int speed = 1; speed <= 10_000; speed++)
            {
                var straight = MovementSim.Step(Origin, new MovementInput(MoveKeys.W, 0), false, speed, hz);
                var diagonal = MovementSim.Step(Origin, new MovementInput(MoveKeys.W | MoveKeys.D, 0), false, speed, hz);

                Assert.True(diagonal.Position.LengthSquared <= straight.Position.LengthSquared,
                    $"speed {speed} at {hz} Hz");
            }
        }
    }

    [Fact]
    public void Diagonal_at_M1_speed_is_within_a_tenth_of_a_percent_of_straight()
    {
        var straight = Step(Origin, MoveKeys.W, 0, false).Position.LengthSquared;
        var diagonal = Step(Origin, MoveKeys.W | MoveKeys.D, 0, false).Position.LengthSquared;

        // Compare squared lengths: (1 - 0.001)^2 is about 0.998.
        Assert.True(diagonal * 1000 >= straight * 998);
    }

    [Fact]
    public void One_second_of_straight_movement_covers_the_configured_speed()
    {
        var state = Origin;
        for (int tick = 0; tick < TickRateHz; tick++)
            state = Step(state, MoveKeys.D, 0, false);

        Assert.Equal(FixVec2.FromCm(SpeedCmPerSec, 0), state.Position);
    }

    [Fact]
    public void Fractional_steps_accumulate_instead_of_being_dropped()
    {
        // 410 cm/s at 20 Hz = 20.5 cm per tick.
        var state = Origin;
        for (int tick = 0; tick < 20; tick++)
            state = MovementSim.Step(state, new MovementInput(MoveKeys.W, 0), false, 410, 20);

        Assert.Equal(FixVec2.FromCm(0, 410), state.Position);
    }

    [Fact]
    public void Action_ignores_move_keys_and_keeps_the_facing_locked()
    {
        // R-2.2-02: movement stops during actions and facing is locked from the action's start.
        var start = new MovementState(FixVec2.FromCm(100, -50), 8192);

        var state = start;
        for (int tick = 0; tick < 50; tick++)
            state = Step(state, MoveKeys.W | MoveKeys.D, facing: (ushort)(tick * 1000), actionActive: true);

        Assert.Equal(start, state);
    }

    [Fact]
    public void Movement_resumes_after_the_action_ends()
    {
        var locked = Step(Origin, MoveKeys.W, facing: 500, actionActive: true);
        var free = Step(locked, MoveKeys.W, facing: 500, actionActive: false);

        Assert.Equal(Origin, locked);
        Assert.Equal(new MovementState(new FixVec2(0, StraightStep), 500), free);
    }

    [Theory]
    [InlineData(1, 120, 2, 1)]          // slowest speed, fastest tick: still moves
    [InlineData(10_000, 1, 2_560_000, 1_810_156)]
    [InlineData(0, 20, 0, 0)]
    public void Boundary_speeds_and_tick_rates(int speed, int hz, int expectedStraight, int expectedDiagonalAxis)
    {
        Assert.Equal(expectedStraight, MovementSim.StepPerTick(speed, hz));

        var diagonal = MovementSim.Step(Origin, new MovementInput(MoveKeys.W | MoveKeys.D, 0), false, speed, hz);
        Assert.Equal(new FixVec2(expectedDiagonalAxis, expectedDiagonalAxis), diagonal.Position);
    }

    [Fact]
    public void Zero_speed_still_updates_facing()
    {
        var next = MovementSim.Step(Origin, new MovementInput(MoveKeys.W, 777), false, 0, TickRateHz);

        Assert.Equal(new MovementState(FixVec2.Zero, 777), next);
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(400, 0)]
    [InlineData(400, -20)]
    public void Rejects_negative_speed_and_non_positive_tick_rate(int speed, int hz)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MovementSim.Step(Origin, new MovementInput(MoveKeys.W, 0), false, speed, hz));
    }

    [Fact]
    public void Same_inputs_give_identical_states_on_every_tick()
    {
        var first = GoldenScenario.RunTrace();
        var second = GoldenScenario.RunTrace();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Scripted_run_matches_the_golden_result()
    {
        // The Unity EditMode test (unity/Assets/Tests/EditMode/MovementSimTests.cs) asserts the same
        // numbers, which proves Mono and .NET produce bit-identical results.
        var result = GoldenScenario.Run();

        Assert.Equal(GoldenScenario.ExpectedFinal, result.Final);
        Assert.Equal(GoldenScenario.ExpectedChecksum, result.Checksum);
    }

    private static MovementState Step(MovementState state, MoveKeys keys, ushort facing, bool actionActive) =>
        MovementSim.Step(state, new MovementInput(keys, facing), actionActive, SpeedCmPerSec, TickRateHz);

    /// <summary>
    /// 10,000 ticks of pseudo-random inputs (seeded PCG32) with actions active on about 20% of ticks.
    /// Keep in sync with the copy in the Unity EditMode tests.
    /// </summary>
    private static class GoldenScenario
    {
        public const int Ticks = 10_000;
        public const ulong Seed = 20261016;
        public const ulong Stream = 7;

        public static readonly MovementState ExpectedFinal = new MovementState(new FixVec2(-109280, -147960), 55444);
        public const ulong ExpectedChecksum = 652694746327926806UL;

        public static (MovementState Final, ulong Checksum) Run()
        {
            var rng = new Pcg32Rng(Seed, Stream);
            var state = new MovementState(FixVec2.Zero, 0);
            ulong checksum = 14695981039346656037UL; // FNV-1a 64 offset basis
            for (int tick = 0; tick < Ticks; tick++)
            {
                state = Next(rng, state);
                checksum = Mix(checksum, (uint)state.Position.X);
                checksum = Mix(checksum, (uint)state.Position.Y);
                checksum = Mix(checksum, state.Facing);
            }
            return (state, checksum);
        }

        public static MovementState[] RunTrace()
        {
            var rng = new Pcg32Rng(Seed, Stream);
            var trace = new MovementState[Ticks];
            var state = new MovementState(FixVec2.Zero, 0);
            for (int tick = 0; tick < Ticks; tick++)
                trace[tick] = state = Next(rng, state);
            return trace;
        }

        private static MovementState Next(IRng rng, MovementState state)
        {
            var keys = (MoveKeys)rng.NextInt(16);
            var facing = (ushort)rng.NextInt(65536);
            bool actionActive = rng.RollPercent(20);
            return MovementSim.Step(state, new MovementInput(keys, facing), actionActive, SpeedCmPerSec, TickRateHz);
        }

        private static ulong Mix(ulong hash, uint value)
        {
            for (int i = 0; i < 4; i++)
            {
                hash ^= (value >> (i * 8)) & 0xFF;
                hash *= 1099511628211UL; // FNV-1a 64 prime
            }
            return hash;
        }
    }
}
