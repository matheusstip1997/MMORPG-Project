using Shared.Simulation;

namespace Shared.Tests.Simulation;

public class FixedPointTests
{
    [Fact]
    public void Centimeters_round_trip()
    {
        Assert.Equal(256, FixedPoint.FromCm(1));
        Assert.Equal(-37, FixedPoint.ToCmFloor(FixedPoint.FromCm(-37)));
        Assert.Equal(1234, FixedPoint.ToCmRound(FixedPoint.FromCm(1234)));
    }

    [Theory]
    [InlineData(127, 0, 0)]
    [InlineData(128, 0, 1)]
    [InlineData(-1, -1, 0)]
    [InlineData(-129, -1, -1)]
    public void Floor_and_round(int raw, int floorCm, int roundCm)
    {
        Assert.Equal(floorCm, FixedPoint.ToCmFloor(raw));
        Assert.Equal(roundCm, FixedPoint.ToCmRound(raw));
    }

    [Fact]
    public void Vector_arithmetic_and_length()
    {
        var a = FixVec2.FromCm(3, 4);
        var b = FixVec2.FromCm(1, 1);

        Assert.Equal(FixVec2.FromCm(4, 5), a + b);
        Assert.Equal(FixVec2.FromCm(2, 3), a - b);
        Assert.Equal(25L * 256 * 256, a.LengthSquared);
    }

    [Fact]
    public void Length_squared_does_not_overflow_at_the_range_limit()
    {
        var far = new FixVec2(int.MaxValue, -int.MaxValue);

        Assert.Equal(2L * int.MaxValue * int.MaxValue, far.LengthSquared);
    }
}
