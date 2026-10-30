using System.Buffers.Binary;
using System.Text;
using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// R-2.5-07: one guarded entry point for every reliable message. Every id has a spec and a fuzz sample (a new id without
/// them fails here), the size cap comes first, the protocol bounds match the codecs, and the guard never allocates.
/// </summary>
public class ReliableMessageReaderTests
{
    [Fact]
    public void Every_reliable_message_id_has_a_guard_spec_and_a_fuzz_sample()
    {
        var ids = Enum.GetValues<ReliableMessageId>().Where(id => id != ReliableMessageId.None).ToArray();

        foreach (var id in ids)
        {
            Assert.True(ReliableMessageReader.TryGetSpec(id, out var spec), $"{id} has no spec in ReliableMessageReader.Build");
            Assert.Equal(id, spec.Id);
            Assert.True(spec.Layout != null == (spec.BodyType != null), $"{id}: a MemoryPack body needs a type and a layout");
            Assert.True(ReliableMessageSamples.Valid.ContainsKey(id), $"{id} has no sample in ReliableMessageSamples (fuzz tests)");
        }
        Assert.Equal(ids, ReliableMessageReader.All.Select(spec => spec.Id));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)200)]
    [InlineData((byte)255)]
    public void An_id_without_a_spec_is_refused(byte id)
    {
        var message = ReliableMessages.Write(ReliableMessageId.PickupRequest, new PickupRequest { DropEntityId = 1 });
        message[0] = id;
        PickupRequest? value = null;

        Assert.False(ReliableMessageReader.TryGetSpec((ReliableMessageId)id, out _));
        Assert.False(ReliableMessageReader.IsWellFormed(message, int.MaxValue));
        Assert.False(ReliableMessageReader.TryRead(message, (ReliableMessageId)id, ref value));
    }

    [Fact]
    public void A_body_is_read_only_as_the_type_registered_for_its_id()
    {
        var message = ReliableMessages.Write(ReliableMessageId.PickupRequest, new PickupRequest { DropEntityId = 1 });
        ChatRefusedMessage? wrong = null;
        PickupRequest? right = null;

        Assert.False(ReliableMessageReader.TryRead(message, ReliableMessageId.PickupRequest, ref wrong));
        Assert.True(ReliableMessageReader.TryRead(message, ReliableMessageId.PickupRequest, ref right));
        Assert.Equal(1u, right!.DropEntityId);
    }

    [Theory]
    [MemberData(nameof(ReliableMessageFuzzTests.Ids), MemberType = typeof(ReliableMessageFuzzTests))]
    public void Valid_samples_pass_within_their_bound_and_fail_one_byte_under_the_cap(ReliableMessageId id)
    {
        var random = new Random(11);
        Assert.True(ReliableMessageReader.TryGetSpec(id, out var spec));
        for (int i = 0; i < 100; i++)
        {
            var message = ReliableMessageSamples.Valid[id](random);
            Assert.True(message.Length <= spec.MaxBytes);
            Assert.True(ReliableMessageReader.IsWellFormed(message, message.Length));
            Assert.False(ReliableMessageReader.IsWellFormed(message, message.Length - 1));
        }
    }

    [Fact]
    public void The_protocol_bounds_are_the_largest_messages_the_writers_make()
    {
        Assert.Equal(ChatCodec.MaxSendSize, Bound(ReliableMessageId.ChatSend));
        Assert.Equal(ChatCodec.MaxLineSize, Bound(ReliableMessageId.ChatMessage));
        Assert.Equal(ChatCodec.RefusedSize, Bound(ReliableMessageId.ChatRefused));
        Assert.Equal(ActionResult.Size, Bound(ReliableMessageId.ActionResult));
        Assert.Equal(EntityIdentity.MaxSize, Bound(ReliableMessageId.EntityIdentity));

        // The largest of each: 255 four-byte characters, a full 200-slot bag.
        var longest = string.Concat(Enumerable.Repeat("😀", ChatText.MaxTextChars));
        var send = ChatCodec.WriteSend(new ReliableMessageWriter(), new ChatSendMessage { Text = longest }).ToArray();
        Assert.Equal(ChatCodec.MaxSendSize, send.Length);
        Assert.True(ChatCodec.TryReadSend(send, out _));
        var full = ReliableMessages.Write(ReliableMessageId.InventoryContents,
            ReliableMessageSamples.Inventory(new Random(2), ReliableMessages.MaxInventorySlots, ReliableMessages.MaxInventorySlots,
                ReliableMessages.MaxInventoryContainers, ReliableMessages.MaxInventorySlots, fullProperties: true));
        Assert.Equal(Bound(ReliableMessageId.InventoryContents), full.Length);
        Assert.True(ReliableMessages.TryRead(full, out InventoryContents contents));
        Assert.Equal(ReliableMessages.MaxInventorySlots, contents.Slots.Length);
        Assert.Equal(ReliableMessages.MaxInventoryContainers, contents.Containers.Length);

        // WP16 bag moves: fixed-size bodies, so every move message is exactly its bound.
        var move = ReliableMessages.Write(ReliableMessageId.InventoryMoveRequest,
            new InventoryMoveRequest { RequestId = uint.MaxValue, FromSlot = 255, ToSlot = 255, FromContainer = 255, ToContainer = 255 });
        Assert.Equal(Bound(ReliableMessageId.InventoryMoveRequest), move.Length);
        var answer = ReliableMessages.Write(ReliableMessageId.InventoryMoveResult,
            new InventoryMoveResult { RequestId = uint.MaxValue, Outcome = InventoryMoveOutcome.Swapped });
        Assert.Equal(Bound(ReliableMessageId.InventoryMoveResult), answer.Length);

        // WP-M2-3 progression: a fixed-size body too.
        var progression = ReliableMessages.Write(ReliableMessageId.ProgressionState, new ProgressionState
        {
            Level = int.MaxValue, Exp = long.MaxValue, ExpToNext = long.MaxValue, Rank = Shared.Simulation.Stats.Rank.SS,
            SkillPoints = int.MaxValue, BandPercent = int.MaxValue, Cause = ProgressionCause.RankUp,
            AttributePoints = int.MaxValue, StrPlaced = int.MaxValue, DexPlaced = int.MaxValue, StaPlaced = int.MaxValue, IntPlaced = int.MaxValue,
        });
        Assert.Equal(Bound(ReliableMessageId.ProgressionState), progression.Length);
    }

    [Fact]
    public void A_message_above_the_cap_is_refused_whatever_it_holds()
    {
        var oversize = new byte[Bound(ReliableMessageId.ChatSend) + 1];
        oversize[0] = (byte)ReliableMessageId.ChatSend;

        Assert.False(ReliableMessageReader.IsWellFormed(oversize, int.MaxValue));
        Assert.False(ChatCodec.TryReadSend(oversize, out _));
    }

    /// <summary>Spec 11.2 and CLAUDE.md, the pattern of <c>HotMessageAllocationTests</c>: the guard allocates nothing, and a refused message costs no garbage.</summary>
    [Fact]
    public void Guarding_valid_and_malformed_messages_does_not_allocate()
    {
        var random = new Random(12);
        var messages = new List<byte[]>();
        foreach (var make in ReliableMessageSamples.Valid.Values)
        {
            var valid = make(random);
            messages.Add(valid);
            messages.Add(valid.AsSpan(0, valid.Length - 1).ToArray());
            var lie = (byte[])valid.Clone();
            if (lie.Length >= 6)
                BinaryPrimitives.WriteInt32LittleEndian(lie.AsSpan(2), int.MinValue);
            messages.Add(lie);
        }
        // The reproduction's overflowing string header, and an inventory array count of int.MaxValue.
        messages.Add(new[] { (byte)ReliableMessageId.ChatSend }.Concat(MemoryPackCrashReproductionTests.OverflowingStringBody).ToArray());
        var countLie = ReliableMessages.Write(ReliableMessageId.InventoryContents, ReliableMessageSamples.Inventory(random, 2, 30));
        BinaryPrimitives.WriteInt32LittleEndian(countLie.AsSpan(1 + 1 + 4 + 1), int.MaxValue);
        messages.Add(countLie);
        var malformed = messages.Where(m => !ReliableMessageReader.IsWellFormed(m, int.MaxValue)).ToArray();
        // 31 MemoryPack truncations (3 of them the skill tree, 3 the book, 4 the quests, 2 the attribute points), a string lie in
        // ChatSend, the two above.
        Assert.Equal(34, malformed.Length);

        Guard(messages, malformed); // warm up the JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            Guard(messages, malformed);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    /// <summary>The guard's UTF-8 check agrees with .NET's strict decoder on random bytes (overlong, surrogates, truncation).</summary>
    [Fact]
    public void The_string_check_is_strict_utf8_with_an_exact_utf16_length()
    {
        var strict = new UTF8Encoding(false, true);
        var random = new Random(13);
        var pool = new byte[] { 0x41, 0x7F, 0x80, 0xBF, 0xC0, 0xC1, 0xC2, 0xDF, 0xE0, 0xED, 0xEF, 0xF0, 0xF4, 0xF5, 0xFF, 0xA0, 0x90, 0x9F };
        for (int i = 0; i < 50_000; i++)
        {
            var bytes = new byte[random.Next(1, 9)];
            for (int b = 0; b < bytes.Length; b++)
                bytes[b] = random.Next(3) == 0 ? (byte)random.Next(256) : pool[random.Next(pool.Length)];
            int expected;
            try
            {
                expected = strict.GetCharCount(bytes);
            }
            catch (DecoderFallbackException)
            {
                expected = -1;
            }

            Assert.Equal(expected >= 0, CheckString(bytes, expected >= 0 ? expected : 1));
            if (expected >= 0)
            {
                Assert.False(CheckString(bytes, expected + 1));
                Assert.False(CheckString(bytes, expected - 1));
                Assert.False(CheckString(bytes, expected, maxBytes: bytes.Length - 1));
            }
        }
    }

    [Theory]
    [InlineData(new byte[] { 0xC0, 0x80 })] // overlong NUL
    [InlineData(new byte[] { 0xE0, 0x80, 0x80 })] // overlong
    [InlineData(new byte[] { 0xF0, 0x80, 0x80, 0x80 })] // overlong
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })] // a UTF-16 surrogate
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 })] // above U+10FFFF
    [InlineData(new byte[] { 0xE2, 0x82 })] // truncated
    [InlineData(new byte[] { 0x80 })] // a lone continuation byte
    public void Invalid_utf8_is_refused(byte[] bytes)
    {
        for (int length = -1; length <= bytes.Length; length++)
            Assert.False(CheckString(bytes, length));
    }

    private static void Guard(List<byte[]> messages, byte[][] malformed)
    {
        foreach (var message in messages)
            ReliableMessageReader.IsWellFormed(message, int.MaxValue);
        foreach (var message in malformed)
        {
            if (ReliableMessages.TryRead(message, out PickupRequest _) | ReliableMessages.TryRead(message, out PickupResult _)
                | ReliableMessages.TryRead(message, out InventoryContents _) | ChatCodec.TryReadSend(message, out _)
                | ChatCodec.TryReadLine(message, out _) | ChatCodec.TryReadRefused(message, out _))
                throw new InvalidOperationException("A malformed message was read.");
        }
    }

    private static bool CheckString(byte[] utf8, int utf16Length, int maxBytes = 1024)
    {
        var body = new byte[8 + utf8.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, ~utf8.Length);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), utf16Length);
        utf8.CopyTo(body, 8);
        ReadOnlySpan<byte> span = body;
        return MemoryPackGuard.String(ref span, maxBytes) && span.IsEmpty;
    }

    private static int Bound(ReliableMessageId id)
    {
        Assert.True(ReliableMessageReader.TryGetSpec(id, out var spec));
        return spec.MaxBytes;
    }
}
