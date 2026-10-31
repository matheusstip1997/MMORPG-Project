using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// A decoded reliable request of a client, waiting for the world: a pickup names <see cref="DropEntityId"/> (backlog G); a chat
/// line carries its <see cref="Text"/> (local chat); a bag move (WP16) names <see cref="FromSlot"/>, <see cref="ToSlot"/> and the
/// client's <see cref="RequestId"/>. Outpost and vendor requests (WP-M2-4) name the NPC in <see cref="DropEntityId"/> (the
/// entity the request is about), a sale its slot in <see cref="FromSlot"/>, and use <see cref="Count"/>,
/// <see cref="ItemNetId"/>, <see cref="QuotedTotal"/> and <see cref="Interaction"/>. A skill-tree request (WP-M2-5) carries its
/// <see cref="SkillOp"/>, the node or class id in <see cref="Text"/> and the hotbar slot in <see cref="FromSlot"/>. A run request
/// (WP-M2-6) names the NPC the same way, its <see cref="QuestAction"/>, a deliver or forfeit step in <see cref="FromSlot"/>, and
/// for an accept the <see cref="BoardEpoch"/> and the offer slots in run order, one byte each from the lowest of
/// <see cref="PackedSlots"/>, <see cref="Count"/> of them.
/// </summary>
public readonly record struct ClientRequest(ReliableMessageId Kind, uint DropEntityId, string? Text = null, byte FromSlot = 0,
    byte ToSlot = 0, uint RequestId = 0, byte FromContainer = 0, byte ToContainer = 0, ushort Count = 0, ushort ItemNetId = 0,
    long QuotedTotal = 0, Shared.Data.Tables.NpcInteraction Interaction = Shared.Data.Tables.NpcInteraction.None,
    SkillTreeOp SkillOp = SkillTreeOp.None, QuestRunAction QuestAction = QuestRunAction.None, uint BoardEpoch = 0,
    ulong PackedSlots = 0, IReadOnlyList<Shared.Simulation.Items.InventoryLocation>? Locations = null)
{
    /// <summary>A move inside the Normal bag (container 0, the M1 move).</summary>
    public static ClientRequest Move(uint requestId, byte fromSlot, byte toSlot) =>
        new(ReliableMessageId.InventoryMoveRequest, 0, null, fromSlot, toSlot, requestId);

    /// <summary>
    /// A move between container slots (<c>ContainerDef.code</c>s; WP-M2-1): a move, an equip or an unequip; with a
    /// <paramref name="count"/> below the stack's, a split (WP-M2-11).
    /// </summary>
    public static ClientRequest Move(uint requestId, byte fromContainer, byte fromSlot, byte toContainer, byte toSlot, ushort count = 0) =>
        new(ReliableMessageId.InventoryMoveRequest, 0, null, fromSlot, toSlot, requestId, fromContainer, toContainer, count);

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

    /// <summary>Learn, upgrade, evolve, respec, advance or a hotbar change (WP-M2-5).</summary>
    public static ClientRequest SkillTree(SkillTreeRequest request) =>
        new(ReliableMessageId.SkillTreeRequest, 0, request.Target, FromSlot: request.Slot, RequestId: request.RequestId,
            SkillOp: request.Op);
    /// <summary>A Bystial book change (WP-M2-8): the card's monster net id in <see cref="ItemNetId"/>, the slot in <see cref="ToSlot"/>.</summary>
    public static ClientRequest Book(BookChangeRequest request) =>
        new(ReliableMessageId.BookChangeRequest, 0, ToSlot: request.Slot, RequestId: request.RequestId, ItemNetId: request.MonsterNetId);
    /// <summary>A consumable use (spec 3.4, WP-M3-3): the container code in <see cref="FromContainer"/> (0: the Normal bag), the slot in <see cref="FromSlot"/>.</summary>
    public static ClientRequest UseItem(UseItemRequest request) =>
        new(ReliableMessageId.UseItemRequest, 0, FromSlot: request.Slot, RequestId: request.RequestId, FromContainer: request.Container);
    /// <summary>A harvest (spec 5.2, WP-M3-5): the resource node in <see cref="DropEntityId"/>.</summary>
    public static ClientRequest Harvest(HarvestRequest request) =>
        new(ReliableMessageId.HarvestRequest, request.NodeEntityId, RequestId: request.RequestId);
    /// <summary>A free attribute point (WP-M2-13, R-3.4-08): the attribute (<c>Attributes.Str</c> ... <c>Int</c>) in <see cref="ToSlot"/>.</summary>
    public static ClientRequest Attribute(AttributeSpendRequest request) =>
        new(ReliableMessageId.AttributeSpendRequest, 0, ToSlot: request.Attribute, RequestId: request.RequestId);
    /// <summary>A job pick (WP-M3-1): the job master in <see cref="DropEntityId"/>, the job net id in <see cref="ItemNetId"/>, the slot in <see cref="ToSlot"/>.</summary>
    public static ClientRequest Job(JobRequest request) =>
        new(ReliableMessageId.JobRequest, request.NpcEntityId, ToSlot: (byte)request.Slot, RequestId: request.RequestId,
            ItemNetId: request.JobNetId);
    /// <summary>A forge start (WP-M3-6): the station in <see cref="DropEntityId"/>, the kind in <see cref="FromSlot"/>, the mold or recipe net id in <see cref="ItemNetId"/>, the inputs in <see cref="Locations"/>.</summary>
    public static ClientRequest ForgeStart(ForgeStart request)
    {
        var inputs = new Shared.Simulation.Items.InventoryLocation[request.Inputs.Length];
        for (int i = 0; i < inputs.Length; i++)
            inputs[i] = new Shared.Simulation.Items.InventoryLocation(request.Inputs[i].Container, request.Inputs[i].Slot);
        return new(ReliableMessageId.ForgeStart, request.StationEntityId, FromSlot: (byte)request.Kind, RequestId: request.RequestId,
            ItemNetId: request.DefNetId, Locations: inputs);
    }
    /// <summary>A forge action (WP-M3-6): the <c>ForgeActionDef.netId</c> in <see cref="ItemNetId"/>.</summary>
    public static ClientRequest ForgeAction(ForgeAction request) =>
        new(ReliableMessageId.ForgeAction, 0, RequestId: request.RequestId, ItemNetId: request.ActionNetId);
    /// <summary>A forge finish or cancel (WP-M3-6).</summary>
    public static ClientRequest Forge(ReliableMessageId kind, uint requestId) => new(kind, 0, RequestId: requestId);
    /// <summary>A run request (WP-M2-6); the router read at most <c>QuestMessages.MaxRunSteps</c> (8) slots, so they pack into 64 bits.</summary>
    public static ClientRequest QuestRun(QuestRunRequest request)
    {
        ulong packed = 0;
        for (int i = 0; i < request.Slots.Length; i++)
            packed |= (ulong)request.Slots[i] << (8 * i);
        return new(ReliableMessageId.QuestRunRequest, request.NpcEntityId, FromSlot: request.StepIndex, RequestId: request.RequestId,
            Count: (ushort)request.Slots.Length, QuestAction: request.Action, BoardEpoch: request.Epoch, PackedSlots: packed);
    }

    /// <summary>Offer slot <paramref name="index"/> of a run request (below <see cref="Count"/>).</summary>
    public byte Slot(int index) => (byte)(PackedSlots >> (8 * index));
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
