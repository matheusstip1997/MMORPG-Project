using Shared.Simulation;

namespace Shared.Tests.Simulation;

/// <summary>M3 decision 16 (R-2.2-06, spec 3.1): the lock aim from the walked position toward the locked enemy.</summary>
public class LockAimTests
{
    [Theory]
    [InlineData(0, 500, 0)]          // north
    [InlineData(500, 0, 16_384)]     // east
    [InlineData(0, -500, 32_768)]    // south
    [InlineData(-500, 0, 49_152)]    // west
    [InlineData(300, 300, 8_192)]    // north-east
    public void Toward_is_the_binary_angle_to_the_target(int dxCm, int dyCm, int expected)
    {
        var from = FixVec2.FromCm(100, -200);
        Assert.True(LockAim.Toward(from, from + FixVec2.FromCm(dxCm, dyCm), out ushort aim));
        Assert.Equal((ushort)expected, aim);
    }

    [Fact]
    public void Standing_on_the_target_has_no_aim()
    {
        var at = FixVec2.FromCm(7, 9);
        Assert.False(LockAim.Toward(at, at, out ushort aim));
        Assert.Equal(0, aim);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(32, 50, -25)]
    [InlineData(16, 25, -12)]     // each axis rounded toward the first sample
    [InlineData(64, 100, -50)]
    public void Lerp64_places_the_view_clock_fraction_between_two_ticks(int sub64, int expectedX, int expectedY)
    {
        var a = new FixVec2(0, 0);
        var b = new FixVec2(100, -50);
        Assert.Equal(new FixVec2(expectedX, expectedY), LockAim.Lerp64(a, b, sub64));
    }
}
