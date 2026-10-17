using Shared.Protocol;

namespace Shared.Tests.Protocol;

public class QuantizeTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(31, 0)] // just under half a step rounds down
    [InlineData(32, 1)] // half a step rounds up
    [InlineData(16384, 256)] // a quarter turn
    [InlineData(65504, 0)] // the last half step wraps to 0
    [InlineData(65503, 1023)]
    public void Angles_round_to_the_nearest_step(int angle, int wire)
    {
        Assert.Equal((uint)wire, Quantize.AngleToWire((ushort)angle, 10));
    }

    [Fact]
    public void Angle_round_trip_error_is_at_most_half_a_step()
    {
        for (int angle = 0; angle < 65536; angle++)
        {
            ushort back = Quantize.RoundTripAngle((ushort)angle, 10);
            int error = Math.Abs((short)(ushort)(back - angle));
            Assert.True(error <= 32, $"angle {angle} came back as {back}");
            Assert.Equal(back, Quantize.RoundTripAngle(back, 10));
        }
    }

    [Fact]
    public void Sixteen_bit_angles_are_exact()
    {
        Assert.Equal(12345u, Quantize.AngleToWire(12345, 16));
        Assert.Equal((ushort)12345, Quantize.AngleFromWire(12345, 16));
    }

    [Fact]
    public void Wire_angles_that_do_not_fit_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantize.AngleFromWire(1024, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantize.AngleToWire(0, 17));
    }

    [Theory]
    [InlineData(1_048_575, 0, true)]
    [InlineData(1_048_576, 0, false)]
    [InlineData(-1_048_576, 0, true)]
    [InlineData(-1_048_577, 0, false)]
    [InlineData(int.MinValue, int.MaxValue, false)] // no int overflow in the subtraction
    [InlineData(500_100, 500_000, true)]
    public void Offsets_are_range_checked(int position, int origin, bool fits)
    {
        Assert.Equal(fits, Quantize.TryOffset(position, origin, 21, out int offset));
        if (fits)
            Assert.Equal(position - origin, offset);
    }

    [Theory]
    [InlineData(0, 1000, 0)]
    [InlineData(-5, 1000, 0)]
    [InlineData(1, 1000, 1)] // alive never looks empty
    [InlineData(999, 1000, 62)] // damaged never looks full
    [InlineData(1000, 1000, 63)]
    [InlineData(5000, 1000, 63)]
    [InlineData(500, 1000, 32)]
    [InlineData(int.MaxValue - 1, int.MaxValue, 62)] // no overflow
    [InlineData(10, 0, 0)]
    public void Hp_buckets(int current, int max, int bucket)
    {
        Assert.Equal(bucket, Quantize.Bucket(current, max, Snapshot.TopHpBucket));
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(0, 65535, true)] // wraps
    [InlineData(0, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(32767, 0, true)]
    [InlineData(32768, 0, false)]
    public void Sequences_compare_across_the_wrap(int a, int b, bool newer)
    {
        Assert.Equal(newer, Sequence.IsNewer((ushort)a, (ushort)b));
    }
}
