using System.Net;
using GameServer.Net.Security;
using GameServer.Net.Tickets;
using LiteNetLib;
using Shared.Protocol;
using Shared.Security;

namespace GameServer.Tests.Support;

/// <summary>
/// A minimal game client over a real LiteNetLib connection: connects with a ticket, sends tagged input packets
/// (R-2.5-02) and decodes the snapshots it receives. Plays the client side the way the Unity client and bots will.
/// </summary>
public sealed class LoopbackGameClient : IDisposable
{
    private readonly NetManager _manager;
    private readonly SessionCrypto _crypto;
    private readonly List<InputFrame> _sent = [];
    private readonly HitEntry[] _hitBuffer = new HitEntry[HitMessage.MaxHits];
    private NetPeer? _peer;

    public LoopbackGameClient(int port, IssuedTicket issued)
    {
        _crypto = new SessionCrypto(issued.SessionKey, SessionRole.Client, SystemChaCha20Poly1305.Create);
        var events = new EventBasedNetListener();
        events.PeerConnectedEvent += peer => _peer = peer;
        events.PeerDisconnectedEvent += (peer, info) =>
        {
            Disconnected = true;
            DisconnectReason = info.Reason;
            // A refusal or a kick carries the reason code (spec 9.10).
            if (info.AdditionalData != null
                && ConnectReject.TryRead(info.AdditionalData.GetRemainingBytesSpan(), out var reason, out _))
                ServerReason = reason;
        };
        events.NetworkReceiveEvent += (_, reader, _, method) =>
        {
            if (method == DeliveryMethod.ReliableOrdered)
            {
                OnReliable(reader.GetRemainingBytes());
                return;
            }
            if (method != DeliveryMethod.Sequenced)
                return;
            var data = reader.GetRemainingBytes();
            if (data.Length > 0 && data[0] == (byte)HotMessageId.Hit)
            {
                Assert.True(HitMessage.TryRead(data, out uint tick, _hitBuffer, out int count));
                for (int i = 0; i < count; i++)
                    Hits.Add((tick, _hitBuffer[i]));
                return;
            }
            Snapshots.Add(DecodedSnapshot.Decode(data));
        };
        _manager = new NetManager(events) { AutoRecycle = true };
        _manager.Start();

        var request = new byte[ConnectRequest.SizeFor(issued.Ticket.Length)];
        ConnectRequest.Write(request, ProtocolVersion.Current, issued.Ticket);
        _manager.Connect(new IPEndPoint(IPAddress.Loopback, port), request);
    }

    public bool Connected => _peer != null;

    public bool Disconnected { get; private set; }

    public DisconnectReason? DisconnectReason { get; private set; }

    /// <summary>The reason code the server sent with a refusal or a kick, if any.</summary>
    public ConnectRejectReason ServerReason { get; private set; }

    public List<DecodedSnapshot> Snapshots { get; } = [];

    /// <summary>Hit messages received (backlog D3), each hit with its server tick.</summary>
    public List<(uint Tick, HitEntry Hit)> Hits { get; } = [];

    /// <summary>Pickup answers received on the reliable channel (backlog G).</summary>
    public List<PickupResult> PickupResults { get; } = [];

    /// <summary>Committed bags received on the reliable channel (backlog G), newest last.</summary>
    public List<InventoryContents> Inventories { get; } = [];

    /// <summary>Bag move answers (WP16), in arrival order.</summary>
    public List<InventoryMoveResult> MoveResults { get; } = [];

    /// <summary>Outpost and vendor answers and committed gold balances (WP-M2-4), in arrival order.</summary>
    public List<OutpostInteractResult> InteractResults { get; } = [];
    public List<SellQuoteResult> Quotes { get; } = [];
    public List<SellResult> SellResults { get; } = [];
    public List<VendorBuyResult> BuyResults { get; } = [];
    public List<long> Gold { get; } = [];
    /// <summary>Own level, exp and rank the server sent (WP-M2-3), in order.</summary>
    public List<ProgressionState> Progressions { get; } = [];

    /// <summary>Skill-tree states and answers (WP-M2-5), in arrival order.</summary>
    public List<SkillTreeState> SkillTrees { get; } = [];
    public List<SkillTreeResult> SkillResults { get; } = [];
    /// <summary>Own Bystial books and book change answers the server sent (WP-M2-8), in order.</summary>
    public List<BookState> Books { get; } = [];
    public List<BookChangeResult> BookResults { get; } = [];

    /// <summary>Own stats the server sent (WP-M2-2), in order.</summary>
    public List<OwnStats> OwnStats { get; } = [];
    /// <summary>Quest boards, run request answers and run states (WP-M2-6), in arrival order.</summary>
    public List<QuestBoard> QuestBoards { get; } = [];
    public List<QuestRunResult> QuestResults { get; } = [];
    public List<RunStatus> RunStatuses { get; } = [];

    /// <summary>The entity this client controls: the first entity of its snapshots.</summary>
    public uint? OwnEntityId => Snapshots.Count > 0 ? Snapshots[^1].Self.EntityId : null;

    public ushort LastSequence => _sent.Count > 0 ? _sent[^1].Sequence : (ushort)0;

    /// <summary>Entities the server spawned for this client as NPCs (WP-M2-4: the city's outpost and vendor near the spawn).</summary>
    public HashSet<uint> NpcIds() =>
        Snapshots.SelectMany(s => s.Spawns).Where(sp => sp.Kind == EntityKind.Npc).Select(sp => sp.EntityId).ToHashSet();

    /// <summary>The entities of <paramref name="snapshot"/> that are not NPCs, in snapshot order.</summary>
    public List<EntitySnapshot> WithoutNpcs(DecodedSnapshot snapshot)
    {
        var npcs = NpcIds();
        return snapshot.Entities.Where(e => !npcs.Contains(e.EntityId)).ToList();
    }

    public void Poll() => _manager.PollEvents();

    /// <summary>Where this client stands, for the message of a wait that ran out.</summary>
    public string Describe() =>
        $"connected {Connected}, disconnected {Disconnected}"
        + (Disconnected ? $" ({DisconnectReason}, server reason {ServerReason})" : "")
        + $", {Snapshots.Count} snapshots, own entity {OwnEntityId?.ToString() ?? "none"}"
        + (Snapshots.Count > 0 ? $", {Snapshots[^1].Entities.Count} entities in the last one" : "")
        + $", {Inventories.Count} bags received";

    /// <summary>
    /// Samples one input frame and sends it with the frames before it (spec 1.7 redundancy). <paramref name="viewTick"/> is
    /// the server tick the client renders others at (R-3.1-03), 0 for none.
    /// </summary>
    public void SendFrame(MoveKeys keys, ushort facing, int redundancy, InputActions actions = InputActions.None, uint viewTick = 0,
        byte skillSlot = 0)
    {
        ushort sequence = (ushort)(LastSequence + 1);
        _sent.Add(new InputFrame
        {
            Sequence = sequence, ClientTick = (uint)(_sent.Count + 1), Keys = keys, Facing = facing, Actions = actions,
            ViewTick = viewTick, SkillSlot = skillSlot,
        });

        int count = Math.Min(redundancy, _sent.Count);
        var frames = new InputFrame[count];
        for (int i = 0; i < count; i++)
            frames[i] = _sent[_sent.Count - 1 - i];

        var payload = new byte[InputPacket.SizeFor(count)];
        InputPacket.Write(payload, frames);
        var packet = new byte[payload.Length + SessionCrypto.InputOverhead];
        _crypto.TagInput(payload, packet);
        _peer!.Send(packet, DeliveryMethod.Sequenced);
    }

    /// <summary>Seals (R-2.5-02) and sends a reliable message, as the Unity client's <c>SendReliable</c> does.</summary>
    public void SendReliable(byte[] message)
    {
        var packet = new byte[message.Length + SessionCrypto.ReliableOverhead];
        _crypto.SealReliable(message, packet);
        _peer!.Send(packet, DeliveryMethod.ReliableOrdered);
    }

    public void SendPickup(uint dropId) =>
        SendReliable(ReliableMessages.Write(ReliableMessageId.PickupRequest, new PickupRequest { DropEntityId = dropId }));

    public void SendMove(uint requestId, byte fromSlot, byte toSlot, ushort count = 0) =>
        SendReliable(ReliableMessages.Write(ReliableMessageId.InventoryMoveRequest,
            new InventoryMoveRequest { RequestId = requestId, FromSlot = fromSlot, ToSlot = toSlot, Count = count }));

    public void SendInteract(uint requestId, uint npcEntityId, Shared.Data.Tables.NpcInteraction kind) =>
        SendReliable(ReliableMessages.Write(ReliableMessageId.OutpostInteractRequest,
            new OutpostInteractRequest { RequestId = requestId, NpcEntityId = npcEntityId, Kind = kind }));

    public void SendQuote(uint requestId, uint npcEntityId, byte slot, ushort count) =>
        SendReliable(ReliableMessages.Write(ReliableMessageId.SellQuoteRequest,
            new SellQuoteRequest { RequestId = requestId, NpcEntityId = npcEntityId, Slot = slot, Count = count }));

    public void SendSell(uint requestId, uint npcEntityId, byte slot, ushort count, long quotedTotal) =>
        SendReliable(ReliableMessages.Write(ReliableMessageId.SellRequest,
            new SellRequest { RequestId = requestId, NpcEntityId = npcEntityId, Slot = slot, Count = count, QuotedTotal = quotedTotal }));

    public void SendQuestRun(uint requestId, uint npcEntityId, QuestRunAction action, uint epoch = 0, byte step = 0,
        params byte[] slots) =>
        SendReliable(ReliableMessages.Write(ReliableMessageId.QuestRunRequest, new QuestRunRequest
        {
            RequestId = requestId, NpcEntityId = npcEntityId, Action = action, Epoch = epoch, StepIndex = step, Slots = slots,
        }));

    public void SendBuy(uint requestId, uint npcEntityId, ushort itemNetId, ushort count, long quotedTotal) =>
        SendReliable(ReliableMessages.Write(ReliableMessageId.VendorBuyRequest,
            new VendorBuyRequest { RequestId = requestId, NpcEntityId = npcEntityId, ItemNetId = itemNetId, Count = count, QuotedTotal = quotedTotal }));

    /// <summary>A skill-tree request (WP-M2-5).</summary>
    public void SendSkillTree(uint requestId, SkillTreeOp op, string target = "", byte slot = 0) =>
        SendReliable(ReliableMessages.Write(ReliableMessageId.SkillTreeRequest,
            new SkillTreeRequest { RequestId = requestId, Op = op, Target = target, Slot = slot }));
    public void SendBookChange(uint requestId, ushort monsterNetId, byte slot) =>
        SendReliable(ReliableMessages.Write(ReliableMessageId.BookChangeRequest,
            new BookChangeRequest { RequestId = requestId, MonsterNetId = monsterNetId, Slot = slot }));

    private void OnReliable(byte[] sealedMessage)
    {
        var plaintext = new byte[sealedMessage.Length];
        Assert.Equal(OpenResult.Ok, _crypto.OpenReliable(sealedMessage, plaintext, out var length));
        var message = plaintext.AsSpan(0, length);
        switch (ReliableMessages.PeekId(message))
        {
            case ReliableMessageId.PickupResult:
                Assert.True(ReliableMessages.TryRead(message, out PickupResult result));
                PickupResults.Add(result);
                break;
            case ReliableMessageId.InventoryContents:
                Assert.True(ReliableMessages.TryRead(message, out InventoryContents contents));
                Inventories.Add(contents);
                break;
            case ReliableMessageId.InventoryMoveResult:
                Assert.True(ReliableMessages.TryRead(message, out InventoryMoveResult move));
                MoveResults.Add(move);
                break;
            case ReliableMessageId.OutpostInteractResult:
                Assert.True(ReliableMessages.TryRead(message, out OutpostInteractResult interact));
                InteractResults.Add(interact);
                break;
            case ReliableMessageId.SellQuoteResult:
                Assert.True(ReliableMessages.TryRead(message, out SellQuoteResult quote));
                Quotes.Add(quote);
                break;
            case ReliableMessageId.SellResult:
                Assert.True(ReliableMessages.TryRead(message, out SellResult sale));
                SellResults.Add(sale);
                break;
            case ReliableMessageId.VendorBuyResult:
                Assert.True(ReliableMessages.TryRead(message, out VendorBuyResult purchase));
                BuyResults.Add(purchase);
                break;
            case ReliableMessageId.GoldBalance:
                Assert.True(ReliableMessages.TryRead(message, out GoldBalance gold));
                Gold.Add(gold.Gold);
                break;
            case ReliableMessageId.ProgressionState:
                Assert.True(ReliableMessages.TryRead(message, out ProgressionState progression));
                Progressions.Add(progression);
                break;
            case ReliableMessageId.SkillTreeState:
                Assert.True(ReliableMessages.TryRead(message, out SkillTreeState tree));
                SkillTrees.Add(tree);
                break;
            case ReliableMessageId.SkillTreeResult:
                Assert.True(ReliableMessages.TryRead(message, out SkillTreeResult skill));
                SkillResults.Add(skill);
                break;
            case ReliableMessageId.BookState:
                Assert.True(ReliableMessages.TryRead(message, out BookState book));
                Books.Add(book);
                break;
            case ReliableMessageId.BookChangeResult:
                Assert.True(ReliableMessages.TryRead(message, out BookChangeResult bookResult));
                BookResults.Add(bookResult);
                break;
            case ReliableMessageId.OwnStats:
                Assert.True(StatsCodec.TryRead(message, out var stats));
                OwnStats.Add(stats);
                break;
            case ReliableMessageId.QuestBoard:
                Assert.True(ReliableMessages.TryRead(message, out QuestBoard board));
                QuestBoards.Add(board);
                break;
            case ReliableMessageId.QuestRunResult:
                Assert.True(ReliableMessages.TryRead(message, out QuestRunResult run));
                QuestResults.Add(run);
                break;
            case ReliableMessageId.RunStatus:
                Assert.True(ReliableMessages.TryRead(message, out RunStatus status));
                RunStatuses.Add(status);
                break;
        }
    }

    public void Dispose()
    {
        _manager.Stop();
        _crypto.Dispose();
    }
}
