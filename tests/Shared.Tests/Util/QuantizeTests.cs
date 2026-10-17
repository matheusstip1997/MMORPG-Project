using Shared.Util;

namespace Shared.Tests.Util;

public class QuantizeTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Every_wire_facing_round_trips(int bits)
    {
        for (int value = 0; value < (1 << bits); value++)
            Assert.Equal(value, Quantize.FacingToBits(Quantize.FacingFromBits(value, bits), bits));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void Facing_error_is_at_most_half_a_step(int bits)
    {
        int halfStep = 1 << (Quantize.FacingFullBits - bits - 1);

        for (int facing = 0; facing <= ushort.MaxValue; facing++)
        {
            ushort back = Quantize.FacingFromBits(Quantize.FacingToBits((ushort)facing, bits), bits);
            int error = Math.Abs((short)(ushort)(back - facing));
            Assert.True(error <= halfStep, $"facing {facing} -> {back}");
        }
    }

    [Fact]
    public void Facing_just_below_a_full_turn_wraps_to_zero()
    {
        Assert.Equal(0, Quantize.FacingToBits(ushort.MaxValue, 8));
    }

    [Fact]
    public void Sixteen_bits_is_lossless()
    {
        Assert.Equal(12345, Quantize.FacingToBits(12345, 16));
        Assert.Equal(12345, Quantize.FacingFromBits(12345, 16));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void Rejects_bit_counts_outside_1_to_16(int bits)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantize.FacingToBits(0, bits));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantize.FacingFromBits(0, bits));
    }

    [Fact]
    public void Rejects_a_wire_value_that_does_not_fit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantize.FacingFromBits(256, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantize.FacingFromBits(-1, 8));
    }
}
