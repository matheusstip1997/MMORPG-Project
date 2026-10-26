using MemoryPack;
using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>The chat messages (R-6.5-01, spec 9.4): MemoryPack bodies behind the reliable id byte, strict readers (R-2.5-04).</summary>
public class ChatMessagesTests
{
    private readonly ReliableMessageWriter _writer = new();

    [Fact]
    public void Send_round_trips()
    {
        var bytes = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = "hello, città 👋" }).ToArray();

        Assert.Equal((byte)ReliableMessageId.ChatSend, bytes[0]);
        Assert.True(ChatCodec.TryReadSend(bytes, out var text));
        Assert.Equal("hello, città 👋", text);
    }

    [Fact]
    public void Line_round_trips()
    {
        var sent = new ChatLineMessage { SenderEntityId = 0xDEADBEEF, SenderName = "Aria", Text = "well met" };
        var bytes = ChatCodec.WriteLine(_writer, sent).ToArray();

        Assert.Equal((byte)ReliableMessageId.ChatMessage, bytes[0]);
        Assert.True(ChatCodec.TryReadLine(bytes, out var line));
        Assert.Equal(0xDEADBEEFu, line.SenderEntityId);
        Assert.Equal("Aria", line.SenderName);
        Assert.Equal("well met", line.Text);
    }

    [Fact]
    public void Every_refusal_reason_round_trips()
    {
        for (var reason = ChatReason.ChatEmpty; reason <= ChatCodec.LastReason; reason++)
        {
            var bytes = ChatCodec.WriteRefused(_writer, new ChatRefusedMessage { Reason = reason }).ToArray();

            Assert.Equal(ChatCodec.RefusedSize, bytes.Length);
            Assert.True(ChatCodec.TryReadRefused(bytes, out var read));
            Assert.Equal(reason, read);
        }
    }

    [Fact]
    public void The_last_reason_is_the_highest_enum_member()
    {
        Assert.Equal(ChatCodec.LastReason, Enum.GetValues<ChatReason>().Max());
    }

    [Fact]
    public void The_longest_lines_fit_the_size_caps()
    {
        // 4-byte UTF-8 characters are the worst case for the byte caps.
        string text = string.Concat(Enumerable.Repeat("😀", ChatCodec.MaxTextChars));
        string name = string.Concat(Enumerable.Repeat("😀", ChatCodec.MaxNameChars));

        var send = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = text }).ToArray();
        Assert.Equal(ChatCodec.MaxSendSize, send.Length);
        Assert.True(ChatCodec.TryReadSend(send, out _));

        var line = ChatCodec.WriteLine(_writer, new ChatLineMessage { SenderEntityId = 1, SenderName = name, Text = text }).ToArray();
        Assert.Equal(ChatCodec.MaxLineSize, line.Length);
        Assert.True(ChatCodec.TryReadLine(line, out _));
    }

    [Fact]
    public void A_send_over_the_byte_cap_is_refused_before_decoding()
    {
        string text = new('a', ChatCodec.MaxSendSize);
        var bytes = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = text }).ToArray();

        Assert.False(ChatCodec.TryReadSend(bytes, out _));
    }

    [Fact]
    public void A_send_may_carry_text_the_server_refuses_later()
    {
        // Decoding only checks the frame; ChatText decides the reason (too long, empty, invalid) on the server.
        var bytes = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = "  \n " }).ToArray();

        Assert.True(ChatCodec.TryReadSend(bytes, out var text));
        Assert.Equal("  \n ", text);
    }

    [Theory]
    [InlineData(ReliableMessageId.ChatMessage)]
    [InlineData(ReliableMessageId.ChatRefused)]
    [InlineData(ReliableMessageId.ActionResult)]
    [InlineData(ReliableMessageId.None)]
    public void A_send_reader_refuses_other_ids(ReliableMessageId id)
    {
        var bytes = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = "hi" }).ToArray();
        bytes[0] = (byte)id;

        Assert.False(ChatCodec.TryReadSend(bytes, out _));
    }

    [Fact]
    public void Truncated_and_padded_messages_are_refused()
    {
        var bytes = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = "hello" }).ToArray();

        for (int length = 0; length < bytes.Length; length++)
            Assert.False(ChatCodec.TryReadSend(bytes.AsSpan(0, length), out _));
        Assert.False(ChatCodec.TryReadSend(bytes.Append((byte)0).ToArray(), out _));
    }

    [Fact]
    public void A_null_body_is_refused()
    {
        var bytes = new[] { (byte)ReliableMessageId.ChatSend, MemoryPackCode.NullObject };

        Assert.False(ChatCodec.TryReadSend(bytes, out _));
    }

    [Fact]
    public void A_string_header_claiming_a_huge_length_is_refused()
    {
        var bytes = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = "hello" }).ToArray();
        // Body: member count, then the UTF-8 string's headers (~byteCount, charCount).
        BitConverter.TryWriteBytes(bytes.AsSpan(2), ~int.MaxValue);
        Assert.False(ChatCodec.TryReadSend(bytes, out _));

        bytes = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = "hello" }).ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(6), int.MaxValue);
        Assert.False(ChatCodec.TryReadSend(bytes, out _));
    }

    [Fact]
    public void Invalid_utf8_is_refused()
    {
        var bytes = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = "ab" }).ToArray();
        bytes[^1] = 0xFF;

        Assert.False(ChatCodec.TryReadSend(bytes, out _));
    }

    [Theory]
    [InlineData(-1)] // null
    [InlineData(3)] // a UTF-16 string
    [InlineData(-6)] // ~5 bytes, one more than the body holds
    [InlineData(int.MinValue)] // ~int.MaxValue bytes
    [InlineData(-2147483645)]
    public void String_headers_our_writer_never_produces_are_refused(int header)
    {
        var bytes = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = "abcd" }).ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(2), header);

        Assert.False(ChatCodec.TryReadSend(bytes, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(1_000)]
    [InlineData(-5)]
    public void A_utf16_length_that_does_not_match_the_bytes_is_refused(int utf16Length)
    {
        // This header, mutated, is what made MemoryPack read past the buffer before the guard existed.
        var bytes = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = "abcd" }).ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(6), utf16Length);

        Assert.False(ChatCodec.TryReadSend(bytes, out _));
    }

    [Fact]
    public void A_wrong_member_count_is_refused()
    {
        var bytes = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = "abcd" }).ToArray();
        bytes[1] = 2;

        Assert.False(ChatCodec.TryReadSend(bytes, out _));
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("Aria", "")]
    [InlineData("Aria", " padded ")]
    [InlineData("Ar\nia", "text")]
    [InlineData("Aria", "line\nbreak")]
    public void A_line_with_unclean_text_is_refused(string name, string text)
    {
        var bytes = ChatCodec.WriteLine(_writer, new ChatLineMessage { SenderEntityId = 1, SenderName = name, Text = text }).ToArray();

        Assert.False(ChatCodec.TryReadLine(bytes, out _));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)5)]
    [InlineData((byte)255)]
    public void A_refusal_with_an_unknown_reason_is_refused(byte reason)
    {
        var bytes = ChatCodec.WriteRefused(_writer, new ChatRefusedMessage { Reason = (ChatReason)reason }).ToArray();

        Assert.False(ChatCodec.TryReadRefused(bytes, out _));
    }

    [Fact]
    public void The_writer_reuses_its_buffer()
    {
        var message = new ChatLineMessage { SenderEntityId = 7, SenderName = "Aria", Text = "again" };
        ChatCodec.WriteLine(_writer, message);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            ChatCodec.WriteLine(_writer, message);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
