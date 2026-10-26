using GameServer.Net;
using GameServer.Net.Tickets;
using GameServer.World;
using Shared.Protocol;

namespace GameServer.Tests.Net;

/// <summary>
/// Reliable client requests on the network thread (backlog G, R-2.5-04): a pickup request is decoded by its id and queued
/// for phase 1; anything else is counted and dropped; a full queue drops and counts.
/// </summary>
public class ReliableRequestTests
{
    private static (InputRouter Router, InputQueue Inputs) Connected(int requestCapacity)
    {
        var inbox = new WorldInbox();
        var router = new InputRouter(inbox, queueCapacity: 4, requestCapacity);
        router.OnPeerConnected(1, new SessionTicket(1, 1, 0, 0, new byte[16], 1));
        Assert.True(inbox.TryTake(out var join));
        return (router, join.Inputs!);
    }

    private static byte[] Pickup(uint dropId) =>
        ReliableMessages.Write(ReliableMessageId.PickupRequest, new PickupRequest { DropEntityId = dropId });

    [Fact]
    public void A_pickup_request_is_queued_for_the_world()
    {
        var (router, inputs) = Connected(requestCapacity: 4);

        router.OnDataReceived(1, Channel.ReliableOrdered, Pickup(42));

        Assert.True(inputs.Requests.TryDequeue(out var request));
        Assert.Equal(new ClientRequest(ReliableMessageId.PickupRequest, 42), request);
        Assert.Equal(0, router.MalformedRequests);
        Assert.Equal(0, inputs.Count); // not an input frame
    }

    [Fact]
    public void Other_or_broken_reliable_messages_are_counted_and_dropped()
    {
        var (router, inputs) = Connected(requestCapacity: 4);

        router.OnDataReceived(1, Channel.ReliableOrdered, [1, 2, 3]); // an ActionResult id: server to client only
        router.OnDataReceived(1, Channel.ReliableOrdered, [(byte)ReliableMessageId.PickupRequest]);
        router.OnDataReceived(1, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.InventoryContents,
            new InventoryContents()));
        router.OnDataReceived(1, Channel.ReliableOrdered, []);

        Assert.False(inputs.Requests.TryDequeue(out _));
        Assert.Equal(4, router.MalformedRequests);
    }

    [Fact]
    public void A_full_queue_drops_and_counts_the_newest_request()
    {
        var (router, inputs) = Connected(requestCapacity: 2);

        for (uint i = 1; i <= 3; i++)
            router.OnDataReceived(1, Channel.ReliableOrdered, Pickup(i));

        Assert.Equal(1, inputs.Requests.Dropped);
        Assert.True(inputs.Requests.TryDequeue(out var first));
        Assert.True(inputs.Requests.TryDequeue(out var second));
        Assert.False(inputs.Requests.TryDequeue(out _));
        Assert.Equal((1u, 2u), (first.DropEntityId, second.DropEntityId));
    }

    [Fact]
    public void Without_item_tables_requests_are_not_accepted()
    {
        var (router, inputs) = Connected(requestCapacity: 0);

        router.OnDataReceived(1, Channel.ReliableOrdered, Pickup(42));

        Assert.False(inputs.Requests.TryDequeue(out _));
        Assert.Equal(1, router.MalformedRequests);
    }
}
