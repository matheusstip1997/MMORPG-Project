using System.Buffers.Binary;
using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// R-2.5-04, R-2.5-07: every reliable message id on the wire is fuzzed through every reader: random bytes, truncations,
/// bit flips, and lies in the int headers (string byte counts and UTF-16 lengths, array counts, member counts). Nothing
/// throws or crashes the process, and whatever a reader accepts is exactly what our writer makes of the decoded value (the
/// guard leaves no second encoding of a value). Seeds are fixed so failures reproduce.
/// </summary>
public class ReliableMessageFuzzTests
{
    private const int Iterations = 5_000;

    /// <summary>Int header values that lie: the overflowing string header of the reproduction, null, UTF-16, huge counts.</summary>
    private static readonly int[] Lies =
    {
        int.MinValue, int.MinValue + 1, int.MinValue + 3, int.MinValue + 4, int.MaxValue, -1, -2, -3, 1, 2, 255, 256,
        0x7FFF, 0x10000, ~0x10000, ReliableMessages.MaxInventorySlots + 1,
    };

    public static IEnumerable<object[]> Ids() =>
        ReliableMessageSamples.Valid.Keys.Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(Ids))]
    public void Random_bytes_under_each_id_never_throw(ReliableMessageId id)
    {
        var random = new Random(4321 + (int)id);
        for (int i = 0; i < Iterations; i++)
        {
            var message = new byte[random.Next(1, 120)];
            random.NextBytes(message);
            message[0] = (byte)id;
            AssertCanonical(message);
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Every_truncation_of_a_valid_message_is_refused(ReliableMessageId id)
    {
        var random = new Random(97 + (int)id);
        for (int i = 0; i < 200; i++)
        {
            var valid = ReliableMessageSamples.Valid[id](random);
            Assert.NotNull(ReliableMessageSamples.ReadAllAndReencode(valid));
            for (int length = 0; length < valid.Length; length++)
                Assert.Null(ReliableMessageSamples.ReadAllAndReencode(valid.AsSpan(0, length).ToArray()));
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Mutated_messages_are_refused_or_read_back_exactly(ReliableMessageId id)
    {
        var random = new Random(8765 + (int)id);
        for (int i = 0; i < Iterations; i++)
            AssertCanonical(Mutate(ReliableMessageSamples.Valid[id](random), random));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void A_lying_int_header_at_any_offset_never_throws(ReliableMessageId id)
    {
        var random = new Random(1357 + (int)id);
        for (int i = 0; i < 60; i++)
        {
            var valid = ReliableMessageSamples.Valid[id](random);
            for (int offset = 1; offset + 4 <= valid.Length; offset++)
            {
                foreach (var lie in Lies)
                {
                    var message = (byte[])valid.Clone();
                    BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(offset), lie);
                    AssertCanonical(message);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void A_wrong_member_count_is_refused(ReliableMessageId id)
    {
        Assert.True(ReliableMessageReader.TryGetSpec(id, out var spec));
        if (spec.Layout == null)
            return; // hand-written: no member count
        var valid = ReliableMessageSamples.Valid[id](new Random(5));
        foreach (var count in new byte[] { 0, (byte)(valid[1] - 1), (byte)(valid[1] + 1), 249, 250, 255 })
        {
            var message = (byte[])valid.Clone();
            message[1] = count;
            Assert.False(ReliableMessageReader.IsWellFormed(message, int.MaxValue));
            Assert.Null(ReliableMessageSamples.ReadAllAndReencode(message));
        }
    }

    /// <summary>Chat strings: each header lie, in each string of each chat message, is refused before MemoryPack.</summary>
    [Fact]
    public void Lying_string_headers_are_refused()
    {
        var writer = new ReliableMessageWriter();
        var send = ChatCodec.WriteSend(writer, new ChatSendMessage { Text = "hello" }).ToArray();
        var line = ChatCodec.WriteLine(writer, new ChatLineMessage { SenderEntityId = 9, SenderName = "Aria", Text = "hi there" }).ToArray();
        // (message, offset of the string header): ChatSend's text; ChatMessage's name and text.
        var strings = new[] { (send, 2), (line, 6), (line, 6 + 8 + 4) };
        foreach (var (valid, offset) in strings)
        {
            int byteCount = ~BinaryPrimitives.ReadInt32LittleEndian(valid.AsSpan(offset));
            int utf16Length = BinaryPrimitives.ReadInt32LittleEndian(valid.AsSpan(offset + 4));
            var headers = Lies.Concat(new[] { ~(byteCount + 1), ~(valid.Length), ~(byteCount - 1) });
            foreach (var header in headers)
            {
                var message = (byte[])valid.Clone();
                BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(offset), header);
                Assert.False(ReliableMessageReader.IsWellFormed(message, int.MaxValue), $"header {header:X8}");
                Assert.Null(ReliableMessageSamples.ReadAllAndReencode(message));
            }
            foreach (var length in Lies.Concat(new[] { 0, utf16Length - 1, utf16Length + 1 }))
            {
                var message = (byte[])valid.Clone();
                BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(offset + 4), length);
                Assert.False(ReliableMessageReader.IsWellFormed(message, int.MaxValue), $"UTF-16 length {length}");
            }
        }
    }

    /// <summary>The inventory array: a count above the cap, above what the body holds, null, or negative is refused.</summary>
    [Fact]
    public void Lying_array_counts_are_refused()
    {
        var contents = ReliableMessageSamples.Inventory(new Random(3), 4, 30);
        var valid = ReliableMessages.Write(ReliableMessageId.InventoryContents, contents);
        const int countOffset = 1 + 1 + 4 + 1; // id, object header, Revision, SlotCount
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(valid.AsSpan(countOffset)));
        foreach (var count in Lies.Concat(new[] { 3, 5, valid.Length - countOffset - 4, valid.Length }))
        {
            var message = (byte[])valid.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(countOffset), count);
            Assert.False(ReliableMessageReader.IsWellFormed(message, int.MaxValue), $"count {count}");
            Assert.False(ReliableMessages.TryRead(message, out InventoryContents _));
        }
    }

    [Fact]
    public void A_bool_other_than_0_or_1_is_refused()
    {
        var contents = ReliableMessageSamples.Inventory(new Random(4), 1, 30);
        var valid = ReliableMessages.Write(ReliableMessageId.InventoryContents, contents);
        const int stolenOffset = 1 + 1 + 4 + 1 + 4 + 1 + 1 + 2 + 1; // ... array count, slot object header, Slot, ItemNetId, Rarity
        var message = (byte[])valid.Clone();
        message[stolenOffset] = 2;

        Assert.True(ReliableMessages.TryRead(valid, out InventoryContents _));
        Assert.False(ReliableMessageReader.IsWellFormed(message, int.MaxValue));
    }

    /// <summary>Refused, or accepted and re-encoded to the same bytes (no throw either way).</summary>
    private static void AssertCanonical(byte[] message)
    {
        var again = ReliableMessageSamples.ReadAllAndReencode(message);
        if (again != null)
            Assert.Equal(message, again);
    }

    /// <summary>Flips bits, sets a byte, truncates or extends: the usual corruptions of a decoder's input.</summary>
    private static byte[] Mutate(byte[] valid, Random random)
    {
        var message = (byte[])valid.Clone();
        switch (random.Next(4))
        {
            case 0:
                int flips = random.Next(1, 4);
                for (int f = 0; f < flips; f++)
                    message[random.Next(message.Length)] ^= (byte)(1 << random.Next(8));
                return message;
            case 1:
                message[random.Next(message.Length)] = (byte)random.Next(256);
                return message;
            case 2:
                return message.AsSpan(0, random.Next(message.Length)).ToArray();
            default:
                var longer = new byte[message.Length + random.Next(1, 8)];
                message.CopyTo(longer, 0);
                random.NextBytes(longer.AsSpan(message.Length));
                return longer;
        }
    }
}
