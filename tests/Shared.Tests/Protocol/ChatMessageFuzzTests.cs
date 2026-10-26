using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// R-2.5-04: the chat readers are fuzz-tested. Random and mutated messages are refused cleanly (false) and never throw;
/// whatever is accepted re-encodes to a message that reads back the same. Seeds are fixed so failures reproduce.
/// </summary>
public class ChatMessageFuzzTests
{
    private const int Iterations = 20_000;

    private static readonly ReliableMessageId[] Ids =
        { ReliableMessageId.ChatSend, ReliableMessageId.ChatMessage, ReliableMessageId.ChatRefused };

    private readonly ReliableMessageWriter _writer = new();

    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(4321);
        for (int i = 0; i < Iterations; i++)
        {
            var message = new byte[random.Next(0, 80)];
            random.NextBytes(message);
            if (message.Length > 0)
                message[0] = (byte)Ids[random.Next(Ids.Length)];

            ReadAll(message);
        }
    }

    [Fact]
    public void Mutated_sends_are_refused_or_read_back_the_same()
    {
        var random = new Random(8765);
        int accepted = 0;
        for (int i = 0; i < Iterations; i++)
        {
            var valid = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = RandomText(random, 40) }).ToArray();
            var message = Mutate(valid, random);
            if (!ChatCodec.TryReadSend(message, out var text))
                continue;
            accepted++;
            var again = ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = text }).ToArray();
            Assert.True(ChatCodec.TryReadSend(again, out var reread));
            Assert.Equal(text, reread);
        }
        Assert.True(accepted > 0);
    }

    [Fact]
    public void Mutated_lines_are_refused_or_read_back_the_same()
    {
        var random = new Random(2468);
        int accepted = 0;
        for (int i = 0; i < Iterations; i++)
        {
            var sent = new ChatLineMessage
            {
                SenderEntityId = (uint)random.Next(),
                SenderName = "N" + RandomText(random, 10),
                Text = "T" + RandomText(random, 40),
            };
            var message = Mutate(ChatCodec.WriteLine(_writer, sent).ToArray(), random);
            if (!ChatCodec.TryReadLine(message, out var line))
                continue;
            accepted++;
            var again = ChatCodec.WriteLine(_writer, line).ToArray();
            Assert.True(ChatCodec.TryReadLine(again, out var reread));
            Assert.Equal((line.SenderEntityId, line.SenderName, line.Text), (reread.SenderEntityId, reread.SenderName, reread.Text));
        }
        Assert.True(accepted > 0);
    }

    [Fact]
    public void Mutated_refusals_never_throw()
    {
        var random = new Random(1357);
        for (int i = 0; i < Iterations; i++)
        {
            var valid = ChatCodec.WriteRefused(_writer, new ChatRefusedMessage { Reason = ChatReason.ChatRateLimited }).ToArray();
            ReadAll(Mutate(valid, random));
        }
    }

    private static void ReadAll(byte[] message)
    {
        ChatCodec.TryReadSend(message, out _);
        ChatCodec.TryReadLine(message, out _);
        ChatCodec.TryReadRefused(message, out _);
    }

    /// <summary>Letters, spaces, accented and 4-byte characters (no control characters, so the text itself is valid).</summary>
    private static string RandomText(Random random, int maxChars)
    {
        int count = random.Next(0, maxChars + 1);
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            builder.Append(random.Next(4) switch
            {
                0 => "a",
                1 => "é",
                2 => "😀",
                _ => (char)('A' + random.Next(26)),
            });
        }
        return builder.ToString().Trim();
    }

    /// <summary>Flips bytes, truncates or extends: the usual corruptions of a decoder's input.</summary>
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
