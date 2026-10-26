using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// Golden bytes of the chat messages from the server's build. The Unity EditMode test <c>ChatMessageTests</c> holds the
/// same strings, so both builds of Shared (net10.0 here, netstandard2.1 under Unity) write and read the same bytes.
/// </summary>
public class ChatMessageGoldenTests
{
    private const string SendHex = "0601f7ffffff080000006869207468657265";
    private const string LineHex = "070304030201fbffffff0400000041726961f4ffffff0800000068c3a96c6c6f20f09f9880";
    private const string RefusedHex = "080104";

    private readonly ReliableMessageWriter _writer = new();

    private static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Fact]
    public void Writes_the_golden_bytes()
    {
        Assert.Equal(SendHex, ToHex(ChatCodec.WriteSend(_writer, new ChatSendMessage { Text = "hi there" })));
        Assert.Equal(LineHex, ToHex(ChatCodec.WriteLine(_writer,
            new ChatLineMessage { SenderEntityId = 0x01020304, SenderName = "Aria", Text = "héllo 😀" })));
        Assert.Equal(RefusedHex, ToHex(ChatCodec.WriteRefused(_writer, new ChatRefusedMessage { Reason = ChatReason.ChatRateLimited })));
    }
}
