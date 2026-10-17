using Shared.Simulation;

namespace Shared.Tests.Simulation;

/// <summary>Spec 2.2: distance per tick against maxSpeed with a tolerance.</summary>
public class SpeedCheckTests
{
    // 400 cm/s at 20 Hz: 20 cm = 5120 raw per tick; +10% = 5632 raw.
    private static readonly long Allowed = SpeedCheck.AllowedRawPerTick(400, 20, tolerancePercent: 10);

    [Fact]
    public void The_allowance_is_the_step_plus_the_tolerance()
    {
        Assert.Equal(5_632, Allowed);
        Assert.Equal(5_120, SpeedCheck.AllowedRawPerTick(400, 20, 0));
    }

    [Theory]
    [InlineData(MoveKeys.W)]
    [InlineData(MoveKeys.D)]
    [InlineData(MoveKeys.W | MoveKeys.D)]
    [InlineData(MoveKeys.S | MoveKeys.A)]
    public void A_MovementSim_step_never_exceeds_even_without_tolerance(MoveKeys keys)
    {
        var start = new MovementState(FixVec2.FromCm(1_000, -2_000), 0);
        var next = MovementSim.Step(start, new MovementInput(keys, 0), actionActive: false, 400, 20);

        Assert.False(SpeedCheck.Exceeds(start.Position, next.Position, SpeedCheck.AllowedRawPerTick(400, 20, 0)));
    }

    [Fact]
    public void A_move_just_inside_and_just_outside_the_allowance()
    {
        Assert.False(SpeedCheck.Exceeds(FixVec2.Zero, new FixVec2(5_632, 0), Allowed));
        Assert.True(SpeedCheck.Exceeds(FixVec2.Zero, new FixVec2(5_633, 0), Allowed));
        // Diagonal: 3982^2 * 2 = 31.71e6 <= 5632^2 = 31.72e6; 3983 is over.
        Assert.False(SpeedCheck.Exceeds(FixVec2.Zero, new FixVec2(3_982, -3_982), Allowed));
        Assert.True(SpeedCheck.Exceeds(FixVec2.Zero, new FixVec2(3_983, -3_983), Allowed));
    }

    [Fact]
    public void Extreme_teleports_do_not_overflow()
    {
        Assert.True(SpeedCheck.Exceeds(new FixVec2(int.MinValue, int.MinValue), new FixVec2(int.MaxValue, int.MaxValue), Allowed));
        Assert.False(SpeedCheck.Exceeds(new FixVec2(int.MaxValue, 0), new FixVec2(int.MaxValue, 0), Allowed));
    }

    [Fact]
    public void Distance_is_reported_in_whole_cm()
    {
        Assert.Equal(500, SpeedCheck.DistanceCm(FixVec2.Zero, FixVec2.FromCm(300, -400)));
        Assert.Equal(0, SpeedCheck.DistanceCm(FixVec2.Zero, new FixVec2(255, 0)));
    }
}
