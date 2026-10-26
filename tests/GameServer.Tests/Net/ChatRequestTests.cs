using GameServer.Net;
using GameServer.World;
using Shared.Protocol;

namespace GameServer.Tests.Net;

/// <summary>
/// Chat lines on the reliable channel (R-6.5-01): the router decodes <c>ChatSend</c> only (R-2.5-04) into the session's
/// bounded chat queue (<c>ChatSettings.maxQueuedLines</c>, R-2.5-03 reliable backlog), apart from the pickups.
/// </summary>
public class ChatRequestTests
{
    private const int PeerId = 7;

    private static (InputRouter Router, InputQueue Inputs) Connected(int chatCapacity = 2)
    {
        var inbox = new WorldInbox();
        var router = new InputRouter(inbox, 4, requestCapacity: 4, chatCapacity: chatCapacity);
        router.OnPeerConnected(PeerId, new GameServer.Net.Tickets.SessionTicket(PeerId, 1, 0, 0, new byte[16], 1));
        Assert.True(inbox.TryTake(out var join));
        return (router, join.Inputs!);
    }

    [Fact]
    public void A_chat_send_is_queued_with_its_text()
    {
        var (router, inputs) = Connected();

        router.OnDataReceived(PeerId, Channel.ReliableOrdered, ChatSend("hi there"));

        Assert.True(inputs.ChatRequests.TryDequeue(out var line));
        Assert.Equal((ReliableMessageId.ChatSend, "hi there"), (line.Kind, line.Text));
        Assert.False(inputs.Requests.TryDequeue(out _)); // the pickup queue stays empty
    }

    [Fact]
    public void Past_the_queue_size_lines_are_dropped_and_counted()
    {
        var (router, inputs) = Connected(chatCapacity: 2);

        for (int i = 0; i < 5; i++)
            router.OnDataReceived(PeerId, Channel.ReliableOrdered, ChatSend("line " + i));

        Assert.Equal(3, router.ChatLinesDropped);
        Assert.True(inputs.ChatRequests.TryDequeue(out var first));
        Assert.Equal("line 0", first.Text);
        Assert.True(inputs.ChatRequests.TryDequeue(out _));
        Assert.False(inputs.ChatRequests.TryDequeue(out _));
    }

    [Fact]
    public void With_chat_off_a_chat_send_is_counted_as_malformed()
    {
        var (router, inputs) = Connected(chatCapacity: 0);

        router.OnDataReceived(PeerId, Channel.ReliableOrdered, ChatSend("hello"));

        Assert.Equal(1, router.MalformedRequests);
        Assert.False(inputs.ChatRequests.TryDequeue(out _));
    }

    [Theory]
    [InlineData(ReliableMessageId.ChatMessage)]
    [InlineData(ReliableMessageId.ChatRefused)]
    [InlineData((ReliableMessageId)200)]
    public void A_chat_body_under_another_id_is_malformed(ReliableMessageId id)
    {
        var (router, inputs) = Connected();
        var message = ChatSend("hi");
        message[0] = (byte)id;

        router.OnDataReceived(PeerId, Channel.ReliableOrdered, message);

        Assert.Equal(1, router.MalformedRequests);
        Assert.False(inputs.ChatRequests.TryDequeue(out _));
    }

    [Fact]
    public void Corrupt_chat_sends_are_counted_and_never_throw()
    {
        var (router, inputs) = Connected(chatCapacity: 2_000);
        var random = new Random(99);

        for (int i = 0; i < 1_000; i++)
        {
            var message = ChatSend("corrupt me please");
            message[random.Next(1, message.Length)] ^= (byte)(1 << random.Next(8));
            router.OnDataReceived(PeerId, Channel.ReliableOrdered, message);
        }

        // Some flips leave a valid line (another letter); the guard refuses every other one before MemoryPack reads it.
        int queued = 0;
        while (inputs.ChatRequests.TryDequeue(out _))
            queued++;
        Assert.Equal(1_000, router.MalformedRequests + queued);
        Assert.True(router.MalformedRequests > 0);
    }

    private static byte[] ChatSend(string text) =>
        ChatCodec.WriteSend(new ReliableMessageWriter(), new ChatSendMessage { Text = text }).ToArray();
}
