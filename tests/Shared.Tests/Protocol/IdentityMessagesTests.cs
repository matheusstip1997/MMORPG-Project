using System.Buffers.Binary;
using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>The reliable EntityIdentity message (spec 9.3 name plates, protocol 10) and its bounds (R-2.5-04).</summary>
public class IdentityMessagesTests
{
    [Theory]
    [InlineData("Arthas")]
    [InlineData("Zoë Ünal")]
    [InlineData("名前")]
    public void Round_trips_entity_id_and_name(string name)
    {
        var buffer = new byte[EntityIdentity.MaxSize];

        int length = EntityIdentity.Write(buffer, 42, name);

        Assert.Equal((byte)ReliableMessageId.EntityIdentity, buffer[0]);
        Assert.True(EntityIdentity.TryRead(buffer.AsSpan(0, length), out uint id, out string read));
        Assert.Equal(42u, id);
        Assert.Equal(name, read);
    }

    [Fact]
    public void Longest_allowed_name_fits_the_max_size()
    {
        var name = new string('名', EntityIdentity.MaxNameBytes / 3);
        var buffer = new byte[EntityIdentity.MaxSize];

        int length = EntityIdentity.Write(buffer, 1, name);

        Assert.True(length <= EntityIdentity.MaxSize);
        Assert.True(EntityIdentity.TryRead(buffer.AsSpan(0, length), out _, out _));
    }

    [Fact]
    public void Writing_an_empty_or_too_long_name_throws()
    {
        var buffer = new byte[EntityIdentity.MaxSize * 2];

        Assert.Throws<ArgumentException>(() => EntityIdentity.Write(buffer, 1, ""));
        Assert.Throws<ArgumentException>(() => EntityIdentity.Write(buffer, 1, new string('a', EntityIdentity.MaxNameBytes + 1)));
    }

    [Fact]
    public void Truncated_or_padded_messages_are_refused()
    {
        var buffer = new byte[EntityIdentity.MaxSize + 1];
        int length = EntityIdentity.Write(buffer, 7, "Name");

        Assert.False(EntityIdentity.TryRead(buffer.AsSpan(0, length - 1), out _, out _));
        Assert.False(EntityIdentity.TryRead(buffer.AsSpan(0, length + 1), out _, out _));
        Assert.False(EntityIdentity.TryRead(ReadOnlySpan<byte>.Empty, out _, out _));
    }

    [Fact]
    public void Wrong_id_zero_length_oversized_length_and_invalid_utf8_are_refused()
    {
        var buffer = new byte[EntityIdentity.MaxSize];
        int length = EntityIdentity.Write(buffer, 7, "Name");

        var wrongId = buffer.AsSpan(0, length).ToArray();
        wrongId[0] = (byte)ReliableMessageId.ActionResult;
        Assert.False(EntityIdentity.TryRead(wrongId, out _, out _));

        var zero = new byte[EntityIdentity.HeaderSize + 1];
        zero[0] = (byte)ReliableMessageId.EntityIdentity;
        Assert.False(EntityIdentity.TryRead(zero, out _, out _));

        var oversized = new byte[EntityIdentity.HeaderSize + EntityIdentity.MaxNameBytes + 1];
        oversized[0] = (byte)ReliableMessageId.EntityIdentity;
        BinaryPrimitives.WriteUInt16LittleEndian(oversized.AsSpan(5), EntityIdentity.MaxNameBytes + 1);
        Assert.False(EntityIdentity.TryRead(oversized, out _, out _));

        var invalid = buffer.AsSpan(0, length).ToArray();
        invalid[EntityIdentity.HeaderSize] = 0xFF;
        Assert.False(EntityIdentity.TryRead(invalid, out _, out string name));
        Assert.Equal(string.Empty, name);
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(1234);
        var buffer = new byte[EntityIdentity.MaxSize + 8];
        for (int i = 0; i < 2_000; i++)
        {
            int length = random.Next(buffer.Length);
            random.NextBytes(buffer);
            buffer[0] = (byte)ReliableMessageId.EntityIdentity;
            EntityIdentity.TryRead(buffer.AsSpan(0, length), out _, out _);
        }
    }
}
