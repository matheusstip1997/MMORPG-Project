using Shared.Simulation;

namespace Shared.Tests.Simulation;

/// <summary>
/// R-2.2-01, M3 decision 16 (<see cref="MoveSteering"/> in <see cref="MovementSim"/>): the keys are world directions and the
/// body faces its walk, keeping its facing while it stands; a target lock never changes the walk (R-2.2-06).
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

    private static MovementState Step(MoveKeys keys, ushort aim, ushort previousFacing = 0) =>
        MovementSim.Step(new MovementState(FixVec2.Zero, previousFacing), new MovementInput(keys, aim), false, SpeedCmPerSec, TickRateHz);

    [Theory]
    [InlineData(MoveKeys.W, 5_000, 0)]                  // the keys' world heading
    [InlineData(MoveKeys.S | MoveKeys.A, 5_000, 40_960)]
    [InlineData(MoveKeys.None, 5_000, 5_000)]          // no key: the facing kept
    [InlineData(MoveKeys.W | MoveKeys.S, 5_000, 5_000)] // opposite keys cancel: kept
    public void The_facing_a_frame_leaves(MoveKeys keys, int previous, int expected) =>
        Assert.Equal((ushort)expected, MoveSteering.FacingFor(keys, (ushort)previous));

    [Fact]
    public void Every_key_walks_its_world_direction_whatever_the_mouse_aim()
    {
        // The axis step (the client maps screen keys by the camera step, R-2.2-05), and the body turns to the walk.
        var old = new[] { (0, 5120), (3620, 3620), (5120, 0), (3620, -3620), (0, -5120), (-3620, -3620), (-5120, 0), (-3620, 3620) };
        for (int k = 0; k < 8; k++)
        {
            foreach (ushort aim in new ushort[] { 0, 7_040, 33_000, 61_000 })
            {
                var next = Step(EightKeys[k].Keys, aim, previousFacing: 12_345);
                Assert.Equal(new FixVec2(old[k].Item1, old[k].Item2), next.Position);
                Assert.Equal((ushort)(k * 8_192), next.Facing);
            }
        }
    }

    [Fact]
    public void Standing_keeps_the_facing_whatever_the_mouse_aim()
    {
        var next = Step(MoveKeys.None, 40_000, previousFacing: 12_345);
        Assert.Equal(FixVec2.Zero, next.Position);
        Assert.Equal((ushort)12_345, next.Facing);
    }

    [Fact]
    public void Move_headings_of_the_keys()
    {
        foreach (var (keys, degrees) in EightKeys)
        {
            Assert.True(MoveSteering.TryMoveHeading(keys, out ushort world));
            Assert.Equal((ushort)(degrees * 65536 / 360), world);
        }
        Assert.False(MoveSteering.TryMoveHeading(MoveKeys.W | MoveKeys.S, out _));
        Assert.False(MoveSteering.TryMoveHeading(MoveKeys.None, out _));
    }
}
