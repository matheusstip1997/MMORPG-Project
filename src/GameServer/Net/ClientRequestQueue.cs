using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// A decoded reliable request of a client, waiting for the world: a pickup names <see cref="DropEntityId"/> (backlog G); a chat
/// line carries its <see cref="Text"/> (local chat); a bag move (WP16) names <see cref="FromSlot"/>, <see cref="ToSlot"/> and the
/// client's <see cref="RequestId"/>. Outpost and vendor requests (WP-M2-4) name the NPC in <see cref="DropEntityId"/> (the
/// entity the request is about), a sale its slot in <see cref="FromSlot"/>, and use <see cref="Count"/>,
/// <see cref="ItemNetId"/>, <see cref="QuotedTotal"/> and <see cref="Interaction"/>.
/// </summary>
public readonly record struct ClientRequest(ReliableMessageId Kind, uint DropEntityId, string? Text = null, byte FromSlot = 0,
    byte ToSlot = 0, uint RequestId = 0, byte FromContainer = 0, byte ToContainer = 0, ushort Count = 0, ushort ItemNetId = 0,
    long QuotedTotal = 0, Shared.Data.Tables.NpcInteraction Interaction = Shared.Data.Tables.NpcInteraction.None)
{
    /// <summary>A move inside the Normal bag (container 0, the M1 move).</summary>
    public static ClientRequest Move(uint requestId, byte fromSlot, byte toSlot) =>
        new(ReliableMessageId.InventoryMoveRequest, 0, null, fromSlot, toSlot, requestId);

    /// <summary>A move between container slots (<c>ContainerDef.code</c>s; WP-M2-1): a move, an equip or an unequip.</summary>
    public static ClientRequest Move(uint requestId, byte fromContainer, byte fromSlot, byte toContainer, byte toSlot) =>
        new(ReliableMessageId.InventoryMoveRequest, 0, null, fromSlot, toSlot, requestId, fromContainer, toContainer);

    public static ClientRequest Interact(OutpostInteractRequest request) =>
        new(ReliableMessageId.OutpostInteractRequest, request.NpcEntityId, RequestId: request.RequestId, Interaction: request.Kind);

    public static ClientRequest Quote(SellQuoteRequest request) =>
        new(ReliableMessageId.SellQuoteRequest, request.NpcEntityId, FromSlot: request.Slot, RequestId: request.RequestId,
            Count: request.Count);

    public static ClientRequest Sell(SellRequest request) =>
        new(ReliableMessageId.SellRequest, request.NpcEntityId, FromSlot: request.Slot, RequestId: request.RequestId,
            Count: request.Count, QuotedTotal: request.QuotedTotal);

    public static ClientRequest Buy(VendorBuyRequest request) =>
        new(ReliableMessageId.VendorBuyRequest, request.NpcEntityId, RequestId: request.RequestId, Count: request.Count,
            ItemNetId: request.ItemNetId, QuotedTotal: request.QuotedTotal);
}

/// <summary>
/// Reliable requests of one session (spec 1.6: the network thread only enqueues decoded requests; the world's simulation
/// thread takes them all in phase 1). Fixed capacity (<c>LootSettings.maxQueuedPickupRequests</c> plus
/// <c>InventorySettings.maxQueuedMoveRequests</c>): a request that
/// arrives with the queue full is dropped and counted, so a client cannot queue work faster than the world does it.
/// Never allocates after construction.
/// </summary>
public sealed class ClientRequestQueue
{
    private readonly ClientRequest[] _requests;
    private readonly Lock _lock = new();
    private int _head;
    private int _count;
    private long _dropped;

    /// <param name="capacity">0 accepts nothing (a world without the tables the requests need).</param>
    public ClientRequestQueue(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "must not be negative");
        _requests = new ClientRequest[capacity];
    }

    public int Capacity => _requests.Length;

    /// <summary>Requests dropped because the queue was full.</summary>
    public long Dropped
    {
        get { lock (_lock) return _dropped; }
    }

    /// <summary>False (and counted as dropped) when the queue is full.</summary>
    public bool TryEnqueue(in ClientRequest request)
    {
        lock (_lock)
        {
            if (_count == _requests.Length)
            {
                _dropped++;
                return false;
            }
            _requests[(_head + _count) % _requests.Length] = request;
            _count++;
            return true;
        }
    }

    public bool TryDequeue(out ClientRequest request)
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                request = default;
                return false;
            }
            request = _requests[_head];
            _head = (_head + 1) % _requests.Length;
            _count--;
            return true;
        }
    }
}
