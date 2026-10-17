using Shared.Protocol;

namespace Shared.Tests.Protocol;

public class BitIoTests
{
    [Fact]
    public void Every_width_round_trips_at_every_alignment()
    {
        var buffer = new byte[64];
        for (int offset = 0; offset < 8; offset++)
        {
            for (int bits = 1; bits <= 32; bits++)
            {
                uint max = bits == 32 ? uint.MaxValue : (1u << bits) - 1;
                foreach (uint value in new[] { 0u, 1u, max, max >> 1, max & 0xA5A5A5A5u })
                {
                    Array.Fill(buffer, (byte)0xFF);
                    var writer = new BitWriter(buffer);
                    if (offset > 0)
                        writer.WriteBits(0, offset);
                    writer.WriteBits(value, bits);
                    writer.WriteBits(1, 1);
                    int length = writer.Finish();

                    var reader = new BitReader(buffer.AsSpan(0, length));
                    if (offset > 0)
                        Assert.Equal(0u, reader.ReadBits(offset));
                    Assert.Equal(value, reader.ReadBits(bits));
                    Assert.True(reader.ReadBool());
                    Assert.True(reader.IsAtCleanEnd());
                }
            }
        }
    }

    [Theory]
    [InlineData(2, -2, 1)]
    [InlineData(21, -1_048_576, 1_048_575)]
    [InlineData(32, int.MinValue, int.MaxValue)]
    public void Signed_values_round_trip_at_their_limits(int bits, int min, int max)
    {
        var buffer = new byte[16];
        var writer = new BitWriter(buffer);
        writer.WriteSigned(min, bits);
        writer.WriteSigned(max, bits);
        writer.WriteSigned(-1, bits);
        writer.WriteSigned(0, bits);
        int length = writer.Finish();

        var reader = new BitReader(buffer.AsSpan(0, length));
        Assert.Equal(min, reader.ReadSigned(bits));
        Assert.Equal(max, reader.ReadSigned(bits));
        Assert.Equal(-1, reader.ReadSigned(bits));
        Assert.Equal(0, reader.ReadSigned(bits));
        Assert.True(reader.IsAtCleanEnd());
    }

    [Fact]
    public void Values_that_do_not_fit_are_programming_errors()
    {
        var buffer = new byte[8];
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitWriter(buffer).WriteBits(8, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitWriter(buffer).WriteBits(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitWriter(buffer).WriteBits(0, 33));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitWriter(buffer).WriteSigned(2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitWriter(buffer).WriteSigned(-3, 2));
    }

    [Fact]
    public void Writing_past_the_end_overflows_without_touching_the_buffer()
    {
        var buffer = new byte[2];
        var writer = new BitWriter(buffer);
        writer.WriteBits(0x1FF, 9);
        writer.WriteBits(0xFF, 8);

        Assert.True(writer.Overflowed);
        Assert.Equal(9, writer.BitPosition);
        writer.WriteBits(1, 1);
        Assert.Equal(9, writer.BitPosition);

        writer.Rewind(9);
        Assert.False(writer.Overflowed);
        writer.WriteBits(0x7F, 7);
        Assert.Equal(2, writer.Finish());
        Assert.Equal(new byte[] { 0xFF, 0xFF }, buffer);
    }

    [Fact]
    public void Patched_bits_replace_a_reserved_field()
    {
        var buffer = new byte[4];
        var writer = new BitWriter(buffer);
        writer.WriteBits(1, 3);
        int countAt = writer.BitPosition;
        writer.WriteBits(0, 10);
        writer.WriteBits(5, 3);
        writer.PatchBits(countAt, 1000, 10);
        int length = writer.Finish();

        var reader = new BitReader(buffer.AsSpan(0, length));
        Assert.Equal(1u, reader.ReadBits(3));
        Assert.Equal(1000u, reader.ReadBits(10));
        Assert.Equal(5u, reader.ReadBits(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitWriter(buffer).PatchBits(0, 1, 1));
    }

    [Fact]
    public void Finish_clears_stale_padding_bits()
    {
        var buffer = new byte[] { 0xFF };
        var writer = new BitWriter(buffer);
        writer.WriteBits(1, 3);

        Assert.Equal(1, writer.Finish());
        Assert.Equal(0x01, buffer[0]);
    }

    [Fact]
    public void Reading_past_the_end_fails_and_stays_failed()
    {
        // R-2.5-04: every read is bound-checked.
        var reader = new BitReader(new byte[] { 0xAB });
        Assert.Equal(0xBu, reader.ReadBits(4));
        Assert.Equal(0u, reader.ReadBits(5));
        Assert.True(reader.Failed);
        Assert.Equal(0u, reader.ReadBits(1));
        Assert.False(reader.IsAtCleanEnd());
    }

    [Fact]
    public void Clean_end_refuses_trailing_bytes_and_padding()
    {
        var trailing = new BitReader(new byte[] { 0x01, 0x00 });
        trailing.ReadBits(8);
        Assert.False(trailing.IsAtCleanEnd());

        var dirtyPadding = new BitReader(new byte[] { 0x81 });
        dirtyPadding.ReadBits(1);
        Assert.False(dirtyPadding.IsAtCleanEnd());

        var empty = new BitReader(ReadOnlySpan<byte>.Empty);
        Assert.True(empty.IsAtCleanEnd());
    }
}
