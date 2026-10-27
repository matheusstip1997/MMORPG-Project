using GameServer.Net;
using GameServer.Net.Tickets;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Tests.Net;

/// <summary>
/// R-2.5-07 on the network thread: the router runs the one guard on every reliable message before it looks at the id
/// (the <c>ReliableMessageCaps</c> cap first, then the layout), and every message a client may send has a route.
/// </summary>
public class ReliableGuardTests
{
    private const int PeerId = 3;

    /// <summary>A valid message of each client-to-server id; a new client id needs one (and a route in the router).</summary>
    private static readonly IReadOnlyDictionary<ReliableMessageId, byte[]> ClientSamples = new Dictionary<ReliableMessageId, byte[]>
    {
        [ReliableMessageId.PickupRequest] =
            ReliableMessages.Write(ReliableMessageId.PickupRequest, new PickupRequest { DropEntityId = 5 }),
        [ReliableMessageId.ChatSend] =
            ChatCodec.WriteSend(new ReliableMessageWriter(), new ChatSendMessage { Text = "hello there" }).ToArray(),
        [ReliableMessageId.InventoryMoveRequest] = ReliableMessages.Write(ReliableMessageId.InventoryMoveRequest,
            new InventoryMoveRequest { RequestId = uint.MaxValue, FromSlot = 255, ToSlot = 255 }),
        // WP-M2-4: the largest of each, so the ReliableMessageCaps rows are checked against them below.
        [ReliableMessageId.OutpostInteractRequest] = ReliableMessages.Write(ReliableMessageId.OutpostInteractRequest,
            new OutpostInteractRequest { RequestId = uint.MaxValue, NpcEntityId = uint.MaxValue, Kind = NpcInteraction.Quests }),
        [ReliableMessageId.SellQuoteRequest] = ReliableMessages.Write(ReliableMessageId.SellQuoteRequest,
            new SellQuoteRequest { RequestId = uint.MaxValue, NpcEntityId = uint.MaxValue, Slot = 255, Count = ushort.MaxValue }),
        [ReliableMessageId.SellRequest] = ReliableMessages.Write(ReliableMessageId.SellRequest, new SellRequest
        {
            RequestId = uint.MaxValue, NpcEntityId = uint.MaxValue, Slot = 255, Count = ushort.MaxValue, QuotedTotal = long.MaxValue,
        }),
        [ReliableMessageId.VendorBuyRequest] = ReliableMessages.Write(ReliableMessageId.VendorBuyRequest, new VendorBuyRequest
        {
            RequestId = uint.MaxValue, NpcEntityId = uint.MaxValue, ItemNetId = ushort.MaxValue, Count = ushort.MaxValue,
            QuotedTotal = long.MaxValue,
        }),
    };

    [Fact]
    public void The_outpost_request_caps_fit_their_largest_message()
    {
        var caps = TestCaps();
        foreach (var id in new[]
                 {
                     ReliableMessageId.OutpostInteractRequest, ReliableMessageId.SellQuoteRequest, ReliableMessageId.SellRequest,
                     ReliableMessageId.VendorBuyRequest,
                 })
            Assert.True(ClientSamples[id].Length <= caps[(int)id], $"{id}: {ClientSamples[id].Length} bytes over its cap {caps[(int)id]}");
    }

    private static int[] TestCaps()
    {
        var tables = GameServer.Host.Config.ConfigLoader.LoadAndValidate(Support.RepositoryPaths.Tables(), Shared.Data.ConfigTables.All);
        Assert.True(tables.Ok, string.Join("\n", tables.Errors));
        return ReliableMessageCaps.ById(tables.Set);
    }

    private static (InputRouter Router, InputQueue Inputs) Connected(int[]? caps = null)
    {
        var inbox = new WorldInbox();
        var router = new InputRouter(inbox, 4, requestCapacity: 4, chatCapacity: 4, reliableCaps: caps);
        router.OnPeerConnected(PeerId, new SessionTicket(PeerId, 1, 0, 0, new byte[16], 1));
        Assert.True(inbox.TryTake(out var join));
        return (router, join.Inputs!);
    }

    [Fact]
    public void Every_client_message_has_a_sample_and_a_route()
    {
        var clientIds = ReliableMessageReader.All.Where(s => s.Direction == ReliableDirection.ClientToServer).Select(s => s.Id);
        Assert.Equal(clientIds.OrderBy(id => id), ClientSamples.Keys.OrderBy(id => id));

        foreach (var (id, message) in ClientSamples)
        {
            var (router, inputs) = Connected();
            router.OnDataReceived(PeerId, Channel.ReliableOrdered, message);

            Assert.True(router.MalformedRequests == 0, $"{id}: a valid client message has no route in InputRouter.OnReliable");
            Assert.True(inputs.Requests.TryDequeue(out _) || inputs.ChatRequests.TryDequeue(out _), $"{id} was not queued");
        }
    }

    [Fact]
    public void A_message_above_its_configured_cap_is_dropped_before_it_is_read()
    {
        var caps = ReliableMessageCaps.ProtocolBounds();
        caps[(int)ReliableMessageId.ChatSend] = ClientSamples[ReliableMessageId.ChatSend].Length - 1;
        var (router, inputs) = Connected(caps);

        router.OnDataReceived(PeerId, Channel.ReliableOrdered, ClientSamples[ReliableMessageId.ChatSend]);
        router.OnDataReceived(PeerId, Channel.ReliableOrdered, ClientSamples[ReliableMessageId.PickupRequest]);

        Assert.Equal(1, router.MalformedRequests);
        Assert.False(inputs.ChatRequests.TryDequeue(out _));
        Assert.True(inputs.Requests.TryDequeue(out _));
    }

    [Fact]
    public void An_id_without_a_cap_is_dropped()
    {
        var caps = ReliableMessageCaps.ProtocolBounds();
        caps[(int)ReliableMessageId.PickupRequest] = 0;
        var (router, inputs) = Connected(caps);

        router.OnDataReceived(PeerId, Channel.ReliableOrdered, ClientSamples[ReliableMessageId.PickupRequest]);

        Assert.Equal(1, router.MalformedRequests);
        Assert.False(inputs.Requests.TryDequeue(out _));
    }

    /// <summary>The bytes of <c>MemoryPackCrashReproductionTests</c>: an unguarded read kills the process; the router counts them.</summary>
    [Fact]
    public void The_overflowing_string_header_is_counted_not_fatal()
    {
        var (router, inputs) = Connected();
        byte[] message = [(byte)ReliableMessageId.ChatSend, 1, 0x00, 0x00, 0x00, 0x80, 0, 0, 0, 0, (byte)'A', (byte)'B'];

        router.OnDataReceived(PeerId, Channel.ReliableOrdered, message);

        Assert.Equal(1, router.MalformedRequests);
        Assert.False(inputs.ChatRequests.TryDequeue(out _));
    }

    [Fact]
    public void A_cap_table_of_the_wrong_size_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new InputRouter(new WorldInbox(), 4, reliableCaps: new int[8]));
    }
}
