using Shared.Protocol;
using Shared.Simulation;
using SimMoveKeys = Shared.Simulation.MoveKeys;

namespace Shared.Tests.Simulation;

/// <summary>
/// R-2.2-01, M3 decision 14 as revised after play: every key is an offset from the facing (<see cref="MoveSteering"/> in
/// <see cref="MovementSim"/>): W at the mouse, S away, D and A at plus and minus a quarter turn, the pairs between.
/// </summary>
public class MoveSteeringTests
{
    // StartingValuesM1 (data/tables/StartingValuesM1.json): 400 cm/s at 20 Hz = 5120 raw per tick.
    private const int SpeedCmPerSec = 400;
    private const int TickRateHz = 20;

    // Direction index clockwise from the facing and its offset in degrees.
    private static readonly (SimMoveKeys Keys, int OffsetDeg)[] EightKeys =
    {
        (SimMoveKeys.W, 0), (SimMoveKeys.W | SimMoveKeys.D, 45), (SimMoveKeys.D, 90), (SimMoveKeys.S | SimMoveKeys.D, 135),
        (SimMoveKeys.S, 180), (SimMoveKeys.S | SimMoveKeys.A, 225), (SimMoveKeys.A, 270), (SimMoveKeys.W | SimMoveKeys.A, 315),
    };

    private static double DegreesOf(FixVec2 delta)
    {
        double degrees = Math.Atan2(delta.X, delta.Y) * 180 / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private static double DegreesOf(ushort angle) => angle * 360.0 / 65536;

    private static double AngleBetween(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);

    private static MovementState Step(SimMoveKeys keys, ushort facing, int speed = SpeedCmPerSec) =>
        MovementSim.Step(new MovementState(FixVec2.Zero, 0), new MovementInput(keys, facing), false, speed, TickRateHz);

    [Fact]
    public void Every_key_moves_along_the_facing_plus_its_offset_at_every_wire_facing()
    {
        // W at the mouse, S away, D at +90, A at -90, the pairs at the 45-degree offsets; every facing on the wire's steps.
        for (int wire = 0; wire < 1024; wire++)
        {
            ushort facing = (ushort)(wire << 6);
            foreach (var (keys, offset) in EightKeys)
            {
                var next = Step(keys, facing);
                Assert.True(AngleBetween(DegreesOf(next.Position), DegreesOf(facing) + offset) < 0.1,
                    $"facing {facing} keys {keys}: moved at {DegreesOf(next.Position):0.00}");
                Assert.Equal(facing, next.Facing);
            }
        }
    }

    [Fact]
    public void A_facing_exactly_on_a_compass_direction_takes_the_old_world_axis_step()
    {
        // The facing at north is the pre-decision rule exactly; at east every key is the old one a quarter turn on.
        var old = new[] { (0, 5120), (3620, 3620), (5120, 0), (3620, -3620), (0, -5120), (-3620, -3620), (-5120, 0), (-3620, 3620) };
        for (int compass = 0; compass < 8; compass++)
        {
            for (int k = 0; k < 8; k++)
            {
                var next = Step(EightKeys[k].Keys, (ushort)(compass * MoveSteering.DirectionAngle));
                var (x, y) = old[(k + compass) % 8];
                Assert.Equal(new FixVec2(x, y), next.Position);
            }
        }
    }

    [Theory]
    [InlineData(SimMoveKeys.W | SimMoveKeys.S)]
    [InlineData(SimMoveKeys.A | SimMoveKeys.D)]
    [InlineData(SimMoveKeys.W | SimMoveKeys.A | SimMoveKeys.S | SimMoveKeys.D)]
    [InlineData(SimMoveKeys.None)]
    public void Opposite_keys_cancel_at_any_facing(SimMoveKeys keys)
    {
        var next = Step(keys, 12_345);

        Assert.Equal(FixVec2.Zero, next.Position);
        Assert.Equal(12_345, next.Facing);
    }

    [Fact]
    public void No_step_is_longer_than_a_straight_one()
    {
        // SpeedCheck measures displacement: an off-compass step must never exceed StepPerTick despite the sine table's rounding.
        for (int wire = 0; wire < 1024; wire++)
        {
            ushort facing = (ushort)(wire << 6);
            foreach (var (keys, _) in EightKeys)
            {
                foreach (int speed in new[] { 1, 37, 400, 410, 999, 10_000 })
                {
                    var next = Step(keys, facing, speed);
                    long step = MovementSim.StepPerTick(speed, TickRateHz);
                    Assert.True(next.Position.LengthSquared <= step * step,
                        $"facing {facing} keys {keys} speed {speed}: {next.Position.LengthSquared} > {step * step}");
                }
            }
        }
    }

    [Fact]
    public void A_moving_mouse_re_aims_the_held_key_every_frame()
    {
        // W held while the mouse sweeps a full turn: each frame moves along that frame's facing.
        var state = new MovementState(FixVec2.Zero, 0);
        for (int i = 0; i < 64; i++)
        {
            ushort facing = (ushort)(i * 1_024 + 320);
            var next = MovementSim.Step(state, new MovementInput(SimMoveKeys.W, facing), false, SpeedCmPerSec, TickRateHz);
            Assert.True(AngleBetween(DegreesOf(next.Position - state.Position), DegreesOf(facing)) < 0.1, $"frame {i}");
            state = next;
        }
    }

    [Fact]
    public void The_inverse_gives_W_toward_the_facing_and_the_nearest_offset_otherwise()
    {
        for (int wire = 0; wire < 1024; wire++)
        {
            ushort facing = (ushort)(wire << 6);
            Assert.Equal(SimMoveKeys.W, MoveSteering.KeysToward(facing, facing));
            foreach (var (keys, offset) in EightKeys)
            {
                Assert.Equal(keys, MoveSteering.KeysToward((ushort)(facing + offset * 65536 / 360), facing));
                // 20 degrees past an offset still picks it; any wanted heading ends within 22.5 degrees.
                ushort wanted = (ushort)(facing + (offset + 20) * 65536 / 360);
                var picked = MoveSteering.KeysToward(wanted, facing);
                Assert.Equal(keys, picked);
                Assert.True(AngleBetween(DegreesOf(Step(picked, facing).Position), DegreesOf(wanted)) <= 22.6);
            }
        }
    }

    [Fact]
    public void The_wire_inverse_rounds_the_facing_as_the_server_decodes_it()
    {
        ushort facing = 3_641; // 20 degrees, not on the wire's 1024 steps
        var keys = MovementInputMapping.KeysToward(facing, facing);
        var input = MovementInputMapping.FromFrame(new InputFrame { Keys = keys, Facing = facing });
        var next = MovementSim.Step(new MovementState(FixVec2.Zero, 0), input, false, SpeedCmPerSec, TickRateHz);

        Assert.Equal(Shared.Protocol.MoveKeys.W, keys);
        Assert.InRange(AngleBetween(DegreesOf(next.Position), 20), 0, 0.5);
    }

    [Fact]
    public void Offsets_and_headings_of_the_keys()
    {
        foreach (var (keys, offset) in EightKeys)
        {
            Assert.True(MoveSteering.TryOffset(keys, out ushort angle));
            Assert.Equal(offset * 65536 / 360, angle);
            Assert.True(MoveSteering.TryHeading(keys, 60_000, out ushort heading));
            Assert.Equal((ushort)(60_000 + offset * 65536 / 360), heading);
        }
        Assert.False(MoveSteering.TryOffset(SimMoveKeys.W | SimMoveKeys.S, out _));
        Assert.False(MoveSteering.TryHeading(SimMoveKeys.None, 1_000, out _));
    }
}
