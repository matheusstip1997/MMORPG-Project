using Shared.Simulation.Economy;

namespace Shared.Tests.Simulation.Economy;

/// <summary>
/// The demand loop's respawn delay (R-4.2-03, R-5.2-03; WP-M3-4): one rule for monsters and resource nodes. The demand
/// percent scales the base delay, a shortened delay keeps its floor, and a respawn never comes at once.
/// </summary>
public class RespawnMathTests
{
    [Theory]
    [InlineData(60_000, 100, 10_000, 60_000)] // neutral: the base delay
    [InlineData(60_000, 300, 10_000, 180_000)] // heavily harvested: three times as long
    [InlineData(60_000, 50, 10_000, 30_000)] // little demand: half, above the floor
    [InlineData(60_000, 10, 10_000, 10_000)] // the floor holds
    [InlineData(5_000, 50, 10_000, 5_000)] // a base below the floor is never shortened
    [InlineData(5_000, 200, 10_000, 10_000)] // nor kept from growing
    public void The_percent_scales_the_base_and_the_floor_holds(long baseDelay, int percent, long floor, long expected)
    {
        Assert.Equal(expected, RespawnMath.ScaledDelay(baseDelay, percent, floor));
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(99, 1, 0)]
    public void A_respawn_never_comes_at_once(long baseDelay, int percent, long floor)
    {
        Assert.Equal(1, RespawnMath.ScaledDelay(baseDelay, percent, floor));
    }

    [Fact]
    public void Negative_inputs_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RespawnMath.ScaledDelay(-1, 100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RespawnMath.ScaledDelay(1_000, -1, 0));
    }
}
