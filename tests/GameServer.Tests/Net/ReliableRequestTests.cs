using GameServer.Net;
using GameServer.Tests.Support;
using GameServer.World;
using Shared.Protocol;

namespace GameServer.Tests.Net;

/// <summary>
/// Client requests on the reliable channel (spec 1.7): the router decodes only the ids a client may send (R-2.5-04),
/// and each peer has a bounded number of requests waiting for the world (R-2.5-03 reliable backlog).
/// </summary>
public class ReliableRequestTests
{
    [Fact]
    public void A_chat_send_becomes_one_world_command_holding_a_budget_slot()
    {
        var inbox = new WorldInbox();
        var router = new InputRouter(inbox, 4, maxPendingRequests: 2);
        router.OnPeerConnected(7, Ticket());
        Assert.True(inbox.TryTake(out _)); // the join

        router.OnDataReceived(7, Channel.ReliableOrdered, ChatSend("hi"));

        Assert.True(inbox.TryTake(out var command));
        Assert.Equal(WorldCommandKind.ChatSend, command.Kind);
        Assert.Equal(7, command.PeerId);
        Assert.Equal("hi", command.Text);
        Assert.Equal(1, command.Budget!.Pending);
    }

    [Fact]
    public void Past_the_budget_requests_are_dropped_until_the_world_releases_a_slot()
    {
        var inbox = new WorldInbox();
        var router = new InputRouter(inbox, 4, maxPendingRequests: 2);
        router.OnPeerConnected(7, Ticket());
        inbox.TryTake(out _);

        for (int i = 0; i < 5; i++)
            router.OnDataReceived(7, Channel.ReliableOrdered, ChatSend("line " + i));

        Assert.Equal(3, router.RequestsDropped);
        Assert.True(inbox.TryTake(out var first));
        Assert.True(inbox.TryTake(out _));
        Assert.False(inbox.TryTake(out _));

        first.Budget!.Release();
        router.OnDataReceived(7, Channel.ReliableOrdered, ChatSend("again"));
        Assert.True(inbox.TryTake(out var again));
        Assert.Equal("again", again.Text);
    }

    [Theory]
    [InlineData(ReliableMessageId.None)]
    [InlineData(ReliableMessageId.ActionResult)]
    [InlineData(ReliableMessageId.EntityIdentity)]
    [InlineData(ReliableMessageId.ChatMessage)]
    [InlineData(ReliableMessageId.ChatRefused)]
    [InlineData((ReliableMessageId)200)]
    public void Ids_a_client_may_not_send_are_counted_as_malformed(ReliableMessageId id)
    {
        var inbox = new WorldInbox();
        var router = new InputRouter(inbox, 4, maxPendingRequests: 2);
        router.OnPeerConnected(7, Ticket());
        inbox.TryTake(out _);
        var message = ChatSend("hi");
        message[0] = (byte)id;

        router.OnDataReceived(7, Channel.ReliableOrdered, message);

        Assert.Equal(1, router.MalformedReliable);
        Assert.False(inbox.TryTake(out _));
    }

    [Fact]
    public void Empty_and_corrupt_reliable_messages_are_counted_and_never_throw()
    {
        var inbox = new WorldInbox();
        var router = new InputRouter(inbox, 4, maxPendingRequests: 2);
        router.OnPeerConnected(7, Ticket());
        inbox.TryTake(out _);
        var random = new Random(99);

        router.OnDataReceived(7, Channel.ReliableOrdered, ReadOnlySpan<byte>.Empty);
        for (int i = 0; i < 1_000; i++)
        {
            var message = ChatSend("corrupt me");
            message[random.Next(1, message.Length)] ^= (byte)(1 << random.Next(8));
            router.OnDataReceived(7, Channel.ReliableOrdered, message);
        }

        // Some flips leave a valid line (a different letter); everything else is counted, nothing throws.
        long accepted = 0;
        while (inbox.TryTake(out var command))
        {
            accepted++;
            command.Budget!.Release();
        }
        Assert.Equal(1_001, router.MalformedReliable + accepted + router.RequestsDropped);
        Assert.True(router.MalformedReliable > 0);
    }

    [Fact]
    public void Messages_from_unknown_peers_are_ignored()
    {
        var inbox = new WorldInbox();
        var router = new InputRouter(inbox, 4, maxPendingRequests: 2);

        router.OnDataReceived(9, Channel.ReliableOrdered, ChatSend("who am i"));

        Assert.False(inbox.TryTake(out _));
        Assert.Equal(0, router.MalformedReliable);
    }

    [Fact]
    public void A_budget_takes_up_to_its_capacity()
    {
        var budget = new RequestBudget(2);

        Assert.True(budget.TryTake());
        Assert.True(budget.TryTake());
        Assert.False(budget.TryTake());
        budget.Release();
        Assert.True(budget.TryTake());
        Assert.Equal(2, budget.Pending);
    }

    private static byte[] ChatSend(string text) =>
        ChatCodec.WriteSend(new ReliableMessageWriter(), new ChatSendMessage { Text = text }).ToArray();

    private static GameServer.Net.Tickets.SessionTicket Ticket() => new(7, 1, 0, 0, new byte[16], 1);
}
