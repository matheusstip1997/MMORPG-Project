using Shared.Simulation;

namespace Shared.Tests.Simulation;

/// <summary>
/// R-2.2-01, R-2.2-06, M3 decision 15 (<see cref="MoveSteering"/> in <see cref="MovementSim"/>): without a lock the keys are
/// world directions and the body faces its walk; with a lock the facing is the input's and the keys are offsets from it.
/// </summary>
public class MoveSteeringTests
{
    // StartingValuesM1 (data/tables/StartingValuesM1.json): 400 cm/s at 20 Hz = 5120 raw per tick.
    private const int SpeedCmPerSec = 400;
    private const int TickRateHz = 20;

    // Direction index clockwise and its angle in degrees.
    private static readonly (MoveKeys Keys, int Degrees)[] EightKeys =
    {
        (MoveKeys.W, 0), (MoveKeys.W | MoveKeys.D, 45), (MoveKeys.D, 90), (MoveKeys.S | MoveKeys.D, 135),
        (MoveKeys.S, 180), (MoveKeys.S | MoveKeys.A, 225), (MoveKeys.A, 270), (MoveKeys.W | MoveKeys.A, 315),
    };

    private static double DegreesOf(FixVec2 delta)
    {
        double degrees = Math.Atan2(delta.X, delta.Y) * 180 / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private static double AngleBetween(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);

    private static MovementState Step(MoveKeys keys, ushort facing, bool locked, ushort previousFacing = 0, int speed = SpeedCmPerSec) =>
        MovementSim.Step(new MovementState(FixVec2.Zero, previousFacing), new MovementInput(keys, facing, 0, locked), false, speed, TickRateHz);

    [Theory]
    [InlineData(MoveKeys.W, false, 30_000, 5_000, 0)]                  // unlocked: the keys' world heading
    [InlineData(MoveKeys.S | MoveKeys.A, false, 30_000, 5_000, 40_960)]
    [InlineData(MoveKeys.None, false, 30_000, 5_000, 5_000)]          // no key: the facing kept
    [InlineData(MoveKeys.W | MoveKeys.S, false, 30_000, 5_000, 5_000)] // opposite keys cancel: kept
    [InlineData(MoveKeys.W, true, 30_000, 5_000, 30_000)]              // locked: the input's facing
    [InlineData(MoveKeys.None, true, 30_000, 5_000, 30_000)]
    public void The_facing_a_frame_leaves(MoveKeys keys, bool locked, int requested, int previous, int expected) =>
        Assert.Equal((ushort)expected, MoveSteering.FacingFor(keys, locked, (ushort)requested, (ushort)previous));

    [Fact]
    public void Without_a_lock_every_key_walks_its_world_direction_whatever_the_input_facing()
    {
        // The old axis step (the client maps screen keys by the camera step, R-2.2-05), and the body turns to the walk.
        var old = new[] { (0, 5120), (3620, 3620), (5120, 0), (3620, -3620), (0, -5120), (-3620, -3620), (-5120, 0), (-3620, 3620) };
        for (int k = 0; k < 8; k++)
        {
            foreach (ushort facing in new ushort[] { 0, 7_040, 33_000, 61_000 })
            {
                var next = Step(EightKeys[k].Keys, facing, false, previousFacing: 12_345);
                Assert.Equal(new FixVec2(old[k].Item1, old[k].Item2), next.Position);
                Assert.Equal((ushort)(k * 8_192), next.Facing);
            }
        }
    }

    [Fact]
    public void With_a_lock_every_key_moves_along_the_facing_plus_its_offset_at_every_wire_facing()
    {
        // W toward the target, S away, D and A around it; every facing on the wire's steps.
        for (int wire = 0; wire < 1024; wire++)
        {
            ushort facing = (ushort)(wire << 6);
            foreach (var (keys, offset) in EightKeys)
            {
                var next = Step(keys, facing, true);
                Assert.True(AngleBetween(DegreesOf(next.Position), facing * 360.0 / 65536 + offset) < 0.1,
                    $"facing {facing} keys {keys}: moved at {DegreesOf(next.Position):0.00}");
                Assert.Equal(facing, next.Facing);
            }
        }
    }

    [Fact]
    public void A_locked_facing_on_a_compass_direction_takes_the_axis_step()
    {
        // Locked toward the east: W walks the old east step, D the old south step.
        Assert.Equal(new FixVec2(5120, 0), Step(MoveKeys.W, 16_384, true).Position);
        Assert.Equal(new FixVec2(0, -5120), Step(MoveKeys.D, 16_384, true).Position);
        Assert.Equal(new FixVec2(3620, -3620), Step(MoveKeys.W | MoveKeys.D, 16_384, true).Position);
    }

    [Fact]
    public void No_step_is_longer_than_a_straight_one()
    {
        // SpeedCheck measures displacement: a locked off-compass step must never exceed StepPerTick despite the sine table.
        for (int wire = 0; wire < 1024; wire++)
        {
            ushort facing = (ushort)(wire << 6);
            foreach (var (keys, _) in EightKeys)
            {
                foreach (int speed in new[] { 1, 37, 400, 410, 999, 10_000 })
                {
                    var next = Step(keys, facing, true, speed: speed);
                    long step = MovementSim.StepPerTick(speed, TickRateHz);
                    Assert.True(next.Position.LengthSquared <= step * step,
                        $"facing {facing} keys {keys} speed {speed}: {next.Position.LengthSquared} > {step * step}");
                }
            }
        }
    }

    [Fact]
    public void Move_headings_of_the_keys()
    {
        foreach (var (keys, degrees) in EightKeys)
        {
            ushort offset = (ushort)(degrees * 65536 / 360);
            Assert.True(MoveSteering.TryMoveHeading(keys, false, 60_000, out ushort world));
            Assert.Equal(offset, world);
            Assert.True(MoveSteering.TryMoveHeading(keys, true, 60_000, out ushort locked));
            Assert.Equal((ushort)(60_000 + offset), locked);
        }
        Assert.False(MoveSteering.TryMoveHeading(MoveKeys.W | MoveKeys.S, true, 1_000, out _));
        Assert.False(MoveSteering.TryMoveHeading(MoveKeys.None, false, 1_000, out _));
    }
}
