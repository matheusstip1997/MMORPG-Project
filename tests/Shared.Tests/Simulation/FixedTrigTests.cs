using Shared.Simulation;

namespace Shared.Tests.Simulation;

/// <summary>The integer sine table dashes move with (architecture rule 6: no floats in simulation).</summary>
public class FixedTrigTests
{
    [Fact]
    public void Matches_the_rounded_sine_at_every_wire_facing()
    {
        for (int step = 0; step < 1024; step++)
        {
            ushort angle = (ushort)(step << 6);
            double radians = step * Math.PI / 512;
            Assert.Equal((int)Math.Round(Math.Sin(radians) * FixedTrig.One), FixedTrig.Sin(angle));
            Assert.Equal((int)Math.Round(Math.Cos(radians) * FixedTrig.One), FixedTrig.Cos(angle));
        }
    }

    [Fact]
    public void Finer_angles_round_down_to_the_wire_step()
    {
        Assert.Equal(FixedTrig.Sin(64), FixedTrig.Sin(127));
        Assert.Equal(FixedTrig.Sin(65_472), FixedTrig.Sin(65_535));
    }

    [Fact]
    public void Along_follows_the_facing_convention_of_MovementSim()
    {
        int d = FixedPoint.FromCm(100);
        Assert.Equal(new FixVec2(0, d), FixedTrig.Along(0, d));          // 0 = +Y
        Assert.Equal(new FixVec2(d, 0), FixedTrig.Along(16_384, d));     // clockwise: a quarter turn is +X
        Assert.Equal(new FixVec2(0, -d), FixedTrig.Along(32_768, d));
        Assert.Equal(new FixVec2(-d, 0), FixedTrig.Along(49_152, d));
    }
}
