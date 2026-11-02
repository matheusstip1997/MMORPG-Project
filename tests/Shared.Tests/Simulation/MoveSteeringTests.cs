using Shared.Protocol;
using Shared.Simulation;
using SimMoveKeys = Shared.Simulation.MoveKeys;

namespace Shared.Tests.Simulation;

/// <summary>R-2.2-01, M3 decision 14: the move keys turn with the mouse (<see cref="MoveSteering"/> in <see cref="MovementSim"/>).</summary>
public class MoveSteeringTests
{
    // StartingValuesM1 (data/tables/StartingValuesM1.json): 400 cm/s at 20 Hz = 5120 raw per tick; hysteresis 3 degrees.
    private const int SpeedCmPerSec = 400;
    private const int TickRateHz = 20;
    private const int StraightStep = 5120;
    private static readonly ushort Hysteresis = MoveSteering.HysteresisFrom(3);

    private static readonly SimMoveKeys[] EightKeys =
    {
        SimMoveKeys.W, SimMoveKeys.W | SimMoveKeys.D, SimMoveKeys.D, SimMoveKeys.S | SimMoveKeys.D,
        SimMoveKeys.S, SimMoveKeys.S | SimMoveKeys.A, SimMoveKeys.A, SimMoveKeys.W | SimMoveKeys.A,
    };

    // On the wire's 1024 steps, where the sine table is exact.
    private static ushort Degrees(double degrees) => (ushort)(((int)Math.Round(degrees * 1024 / 360) << 6) & 0xFFFF);

    private static double DegreesOf(FixVec2 delta)
    {
        double degrees = Math.Atan2(delta.X, delta.Y) * 180 / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private static double AngleBetween(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);

    private static MovementState Step(MovementState state, SimMoveKeys keys, ushort facing, ushort hysteresis = 0) =>
        MovementSim.Step(state, new MovementInput(keys, facing), false, SpeedCmPerSec, TickRateHz, hysteresis);

    private static MovementState At(ushort facing, byte sector = 0) => new MovementState(FixVec2.Zero, facing, sector);

    [Fact]
    public void A_facing_exactly_on_a_compass_direction_moves_every_key_as_before()
    {
        for (int compass = 0; compass < 8; compass++)
        {
            ushort facing = (ushort)(compass * MoveSteering.SectorAngle);
            foreach (var keys in EightKeys)
            {
                var turned = Step(At(facing), keys, facing, Hysteresis);
                // The old rule: world axes, the diagonal scaled by InvSqrt2Q16 (the facing 0 case, which never turned).
                var old = Step(At(0, 1), keys, 0);
                Assert.Equal(old.Position, turned.Position);
                Assert.Equal(compass + 1, turned.KeySector);
            }
        }
    }

    [Theory]
    [InlineData(20, SimMoveKeys.W, 20)]
    [InlineData(20, SimMoveKeys.S, 200)]
    [InlineData(20, SimMoveKeys.D, 110)]
    [InlineData(20, SimMoveKeys.A, 290)]
    [InlineData(30, SimMoveKeys.W | SimMoveKeys.D, 30)]   // 30 degrees lies in north-east's sector: the pair runs at it
    [InlineData(30, SimMoveKeys.W, 345)]                  // and W 45 degrees to its left
    [InlineData(30, SimMoveKeys.S | SimMoveKeys.A, 210)]
    [InlineData(270, SimMoveKeys.A, 270)]                 // mouse exactly left: A runs at it
    [InlineData(300, SimMoveKeys.W | SimMoveKeys.A, 300)]
    [InlineData(300, SimMoveKeys.D | SimMoveKeys.S, 120)]
    public void The_keys_turn_with_the_mouse(double mouseDegrees, SimMoveKeys keys, double expectedDegrees)
    {
        ushort facing = Degrees(mouseDegrees);
        var next = Step(At(facing), keys, facing, Hysteresis);

        Assert.InRange(AngleBetween(DegreesOf(next.Position), expectedDegrees), 0, 0.2); // the mouse rounded to the wire step
    }

    [Fact]
    public void The_turn_stays_within_half_a_sector_plus_the_hysteresis_and_no_step_is_longer_than_a_straight_one()
    {
        for (int wire = 0; wire < 1024; wire++)
        {
            ushort facing = (ushort)(wire << 6);
            for (byte previous = 0; previous <= 8; previous++)
            {
                int sector = MoveSteering.Sector(facing, previous, Hysteresis);
                short turn = MoveSteering.Turn(facing, (byte)(sector + 1));
                Assert.InRange(Math.Abs((int)turn), 0, MoveSteering.HalfSector + Hysteresis);
                Assert.InRange(Math.Abs((int)MoveSteering.Turn(facing, 0)), 0, MoveSteering.HalfSector);
            }
            foreach (var keys in EightKeys)
            {
                foreach (int speed in new[] { 1, 37, 400, 410, 999, 10_000 })
                {
                    var next = MovementSim.Step(At(facing), new MovementInput(keys, facing), false, speed, TickRateHz);
                    long step = MovementSim.StepPerTick(speed, TickRateHz);
                    Assert.True(next.Position.LengthSquared <= step * step,
                        $"facing {facing} keys {keys} speed {speed}: {next.Position.LengthSquared} > {step * step}");
                }
            }
        }
    }

    [Fact]
    public void Each_key_stays_within_half_a_sector_plus_the_hysteresis_of_its_own_direction()
    {
        double limit = (MoveSteering.HalfSector + Hysteresis) * 360.0 / 65536 + 0.1;
        for (int wire = 0; wire < 1024; wire++)
        {
            ushort facing = (ushort)(wire << 6);
            for (int k = 0; k < 8; k++)
            {
                var next = Step(At(facing, (byte)(MoveSteering.Nearest(facing) + 1)), EightKeys[k], facing, Hysteresis);
                Assert.InRange(AngleBetween(DegreesOf(next.Position), k * 45), 0, limit);
            }
        }
    }

    [Fact]
    public void A_mouse_on_a_sector_edge_keeps_one_sector_until_it_leaves_the_hysteresis_band()
    {
        // 22.5 degrees is the edge between north (sector 0) and north-east (1); one wire step is 64.
        ushort edge = MoveSteering.HalfSector;
        var state = Step(At(Degrees(10)), SimMoveKeys.W, Degrees(10), Hysteresis);
        Assert.Equal(1, state.KeySector);
        foreach (int offset in new[] { 64, -64, 128, -64, 64, 0, 64 })
        {
            ushort facing = (ushort)(edge + offset);
            state = Step(state, SimMoveKeys.W, facing, Hysteresis);
            Assert.Equal(1, state.KeySector); // still north: W runs at the mouse, no flip to the north-east pair
            Assert.InRange(AngleBetween(DegreesOf(Step(At(facing, 1), SimMoveKeys.W, facing, Hysteresis).Position),
                facing * 360.0 / 65536), 0, 0.1);
        }
        // Past the band (22.5 + 3 degrees): the north-east sector, and back inside it stays there.
        state = Step(state, SimMoveKeys.W, (ushort)(edge + Hysteresis + 64), Hysteresis);
        Assert.Equal(2, state.KeySector);
        state = Step(state, SimMoveKeys.W, (ushort)(edge - 64), Hysteresis);
        Assert.Equal(2, state.KeySector);
        // Without hysteresis the nearest sector wins each tick.
        Assert.Equal(1, Step(At(0, 2), SimMoveKeys.W, (ushort)(edge - 64)).KeySector);
    }

    [Fact]
    public void The_sector_follows_the_mouse_while_an_action_runs_and_the_entity_stays_put()
    {
        var start = new MovementState(new FixVec2(100, 200), Degrees(5), 1);
        var next = MovementSim.Step(start, new MovementInput(SimMoveKeys.W, Degrees(90)), true, SpeedCmPerSec, TickRateHz, Hysteresis);

        Assert.Equal(start.Position, next.Position);
        Assert.Equal(start.Facing, next.Facing);
        Assert.Equal(3, next.KeySector); // east
    }

    [Fact]
    public void The_inverse_gives_the_key_that_runs_at_the_wanted_heading()
    {
        for (int wire = 0; wire < 1024; wire++)
        {
            ushort facing = (ushort)(wire << 6);
            for (byte previous = 0; previous <= 8; previous++)
            {
                // Facing the target: straight at it.
                var keys = MoveSteering.KeysToward(facing, facing, previous, Hysteresis);
                var next = Step(At(facing, previous), keys, facing, Hysteresis);
                Assert.InRange(AngleBetween(DegreesOf(next.Position), facing * 360.0 / 65536), 0, 0.1);

                // Any other heading: the nearest turned key, within half a sector.
                ushort wanted = (ushort)(facing + 20_000);
                keys = MoveSteering.KeysToward(wanted, facing, previous, Hysteresis);
                next = Step(At(facing, previous), keys, facing, Hysteresis);
                Assert.InRange(AngleBetween(DegreesOf(next.Position), wanted * 360.0 / 65536), 0, 22.6);
            }
        }
    }

    [Fact]
    public void The_wire_inverse_rounds_the_facing_as_the_server_decodes_it()
    {
        ushort facing = 3641; // 20 degrees, not on the wire's 1024 steps
        var keys = MovementInputMapping.KeysToward(facing, facing, 0, Hysteresis);
        var input = MovementInputMapping.FromFrame(new InputFrame { Keys = keys, Facing = facing });
        var next = MovementSim.Step(At(0), input, false, SpeedCmPerSec, TickRateHz, Hysteresis);

        Assert.Equal(Shared.Protocol.MoveKeys.W, keys);
        Assert.InRange(AngleBetween(DegreesOf(next.Position), 20), 0, 0.5);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 546)]
    [InlineData(22, 4005)]
    public void The_hysteresis_degrees_become_a_binary_angle(int degrees, int expected) =>
        Assert.Equal(expected, MoveSteering.HysteresisFrom(degrees));

    [Theory]
    [InlineData(-1)]
    [InlineData(23)]
    public void A_hysteresis_of_half_a_sector_or_more_is_refused(int degrees) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MoveSteering.HysteresisFrom(degrees));
}
