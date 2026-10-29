using GameServer.Net.Tickets;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Transport listener that feeds a world (spec 1.6: network threads only enqueue decoded inputs). Runs on the network
/// thread: it decodes input packets, drops frames it has already queued, and enqueues the rest oldest first into the
/// session's <see cref="InputQueue"/>. Connects and disconnects become <see cref="WorldCommand"/>s. It never touches
/// world state.
/// <para>
/// Each packet repeats the last few frames (spec 2.2 input flow; 12.7 "Input redundancy": the last 3 inputs per packet),
/// so most frames arrive more than once and packets can arrive out of order: a frame is queued only if its sequence is
/// newer than the newest one queued for that session. Frames lost in every packet that carried them are skipped; the
/// client's next correction covers them. A repeated copy of a frame already queued counts as
/// <see cref="RedundantFrames"/> (the normal case); only a frame that arrives after a newer one was queued, and was
/// never queued itself, counts as <see cref="StaleFrames"/> (really late or out of order).
/// </para>
/// The world's <c>SessionManager</c> (backlog B4) turns those commands into logins, reattaches and lingering
/// characters. Reliable requests go to the session's <see cref="ClientRequestQueue"/>s: pickups and bag moves (backlog G,
/// WP16) and chat lines (local chat, R-6.5-01).
/// TODO(D2): more reliable requests (the hotbar is in the skill-tree request, WP-M2-5).
/// </summary>
public sealed class InputRouter : ITransportListener
{
    private readonly WorldInbox _inbox;
    private readonly int _queueCapacity;
    private readonly int _requestCapacity;
    private readonly Dictionary<int, Session> _sessions = new();
    private readonly InputFrame[] _frames = new InputFrame[InputPacket.MaxFrames];
    private long _malformedPackets;
    private long _malformedRequests;
    private long _staleFrames;
    private long _redundantFrames;
    private long _chatLinesDropped;
    private readonly int _chatCapacity;
    private readonly int[] _reliableCaps;

    /// <param name="requestCapacity">
    /// Reliable requests one session may have queued (<c>LootSettings.maxQueuedPickupRequests</c> plus
    /// <c>InventorySettings.maxQueuedMoveRequests</c>); 0 ignores them all.
    /// </param>
    /// <param name="chatCapacity">
    /// Chat lines one session may have queued for the next tick (<c>ChatSettings.maxQueuedLines</c>); 0 ignores them all.
    /// </param>
    /// <param name="reliableCaps">
    /// The largest reliable message a client may send, by id byte (<c>ReliableMessageCaps.ById</c>, R-2.5-07); 0 refuses
    /// the id. Null uses the protocol bounds (<c>ReliableMessageCaps.ProtocolBounds</c>).
    /// </param>
    public InputRouter(WorldInbox inbox, int queueCapacity, int requestCapacity = 0, int chatCapacity = 0,
        int[]? reliableCaps = null)
    {
        _inbox = inbox;
        _queueCapacity = queueCapacity;
        _requestCapacity = requestCapacity;
        _chatCapacity = chatCapacity;
        var caps = reliableCaps ?? ReliableMessageCaps.ProtocolBounds();
        if (caps.Length != 256)
            throw new ArgumentException("One cap per id byte (256).", nameof(reliableCaps));
        _reliableCaps = (int[])caps.Clone();
    }

    /// <summary>
    /// Well-formed chat lines dropped because the session's chat queue was full (R-2.5-03 reliable backlog). Safe to read
    /// from any thread; the world sends the total to telemetry (<c>RequestCounters</c>).
    /// </summary>
    public long ChatLinesDropped => Interlocked.Read(ref _chatLinesDropped);

    /// <summary>
    /// Unreliable messages that were not a well-formed input packet (R-2.5-04). Safe to read from any thread; the world
    /// sends the total to telemetry in phase 12. TODO: count per account once bot scoring exists (spec 7.3).
    /// </summary>
    public long MalformedPackets => Interlocked.Read(ref _malformedPackets);

    /// <summary>
    /// Reliable messages that were not a well-formed request a client may send (R-2.5-04), or arrived while requests are
    /// off. Safe to read from any thread.
    /// </summary>
    public long MalformedRequests => Interlocked.Read(ref _malformedRequests);

    /// <summary>
    /// Frames that arrived too late to be queued: older than the newest frame queued for the session and never queued
    /// themselves (a reordered packet). Redundant copies are not counted here. The world sends the total to telemetry
    /// (<c>WorldCounters.staleFrames</c>, spec 7.2). Safe to read from any thread.
    /// </summary>
    public long StaleFrames => Interlocked.Read(ref _staleFrames);

    /// <summary>
    /// Copies of frames already queued from an earlier packet (spec 12.7 input redundancy), about two per packet for a
    /// healthy client. Diagnostics only, not sent to telemetry. Safe to read from any thread.
    /// </summary>
    public long RedundantFrames => Interlocked.Read(ref _redundantFrames);

    public void OnPeerConnected(int peerId, SessionTicket ticket)
    {
        var inputs = new InputQueue(_queueCapacity, _requestCapacity, _chatCapacity);
        _sessions[peerId] = new Session(inputs);
        _inbox.Post(WorldCommand.Join(peerId, ticket.AccountId, ticket.Slot, inputs));
    }

    public void OnPeerDisconnected(int peerId, PeerDisconnectReason reason)
    {
        if (_sessions.Remove(peerId))
            _inbox.Post(WorldCommand.Leave(peerId));
    }

    public void OnDataReceived(int peerId, Channel channel, ReadOnlySpan<byte> data)
    {
        if (!_sessions.TryGetValue(peerId, out var session))
            return;
        if (channel == Channel.ReliableOrdered)
        {
            OnReliable(session, data);
            return;
        }
        if (channel != Channel.UnreliableSequenced)
            return;

        if (!InputPacket.TryRead(data, _frames, out int count))
        {
            Interlocked.Increment(ref _malformedPackets);
            return;
        }

        for (int i = count - 1; i >= 0; i--)
        {
            ref readonly var frame = ref _frames[i];
            if (session.HasQueued && !Sequence.IsNewer(frame.Sequence, session.NewestQueued))
            {
                if (session.WasQueued(frame.Sequence))
                    Interlocked.Increment(ref _redundantFrames);
                else
                    Interlocked.Increment(ref _staleFrames);
                continue;
            }
            session.Inputs.Enqueue(frame);
            session.MarkQueued(frame.Sequence);
        }
    }

    /// <summary>
    /// A reliable request (backlog G: pickup or bag move; local chat line): decoded here by its id, never as a client-chosen
    /// type (R-2.5-04), and queued for phase 1. First, for every id, the one guard (R-2.5-07): the id's <c>ReliableMessageCaps</c> cap (0 for ids a client
    /// may not send), then <see cref="ReliableMessageReader.IsWellFormed"/> walks the whole MemoryPack body, so a new
    /// request id cannot reach MemoryPack without a cap and a layout. Anything else a client sends on the reliable channel
    /// is counted as malformed and dropped.
    /// </summary>
    private void OnReliable(Session session, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || !ReliableMessageReader.IsWellFormed(data, _reliableCaps[data[0]]))
        {
            Interlocked.Increment(ref _malformedRequests);
            return;
        }
        if (ReliableMessages.PeekId(data) == ReliableMessageId.PickupRequest
            && session.Inputs.Requests.Capacity > 0
            && ReliableMessages.TryRead(data, out PickupRequest request))
        {
            session.Inputs.Requests.TryEnqueue(new ClientRequest(ReliableMessageId.PickupRequest, request.DropEntityId));
            return;
        }
        if (ReliableMessages.PeekId(data) == ReliableMessageId.InventoryMoveRequest
            && session.Inputs.Requests.Capacity > 0
            && ReliableMessages.TryRead(data, out InventoryMoveRequest move))
        {
            session.Inputs.Requests.TryEnqueue(ClientRequest.Move(move.RequestId, move.FromContainer, move.FromSlot, move.ToContainer,
                move.ToSlot, move.Count));
            return;
        }
        if (TryEnqueueOutpostRequest(session, data))
            return;
        if (ReliableMessages.PeekId(data) == ReliableMessageId.SkillTreeRequest
            && session.Inputs.Requests.Capacity > 0
            && ReliableMessages.TryRead(data, out SkillTreeRequest skill))
        {
            // The skill tree (WP-M2-5): queued with the other requests, handled in phase 1 in arrival order.
            session.Inputs.Requests.TryEnqueue(ClientRequest.SkillTree(skill));
            return;
        }
        if (ReliableMessages.PeekId(data) == ReliableMessageId.ChatSend
            && session.Inputs.ChatRequests.Capacity > 0
            && ChatCodec.TryReadSend(data, out var text))
        {
            // Local chat (R-6.5-01): the world checks the text and the rate limit in phase 1.
            if (!session.Inputs.ChatRequests.TryEnqueue(new ClientRequest(ReliableMessageId.ChatSend, 0, text)))
                Interlocked.Increment(ref _chatLinesDropped);
            return;
        }
        Interlocked.Increment(ref _malformedRequests);
    }

    /// <summary>
    /// Outpost and vendor requests (WP-M2-4): interact, quote, sell, buy. Queued with the pickups and bag moves, handled in
    /// phase 1 in arrival order. False when <paramref name="data"/> is none of them (or does not decode).
    /// </summary>
    private static bool TryEnqueueOutpostRequest(Session session, ReadOnlySpan<byte> data)
    {
        var requests = session.Inputs.Requests;
        if (requests.Capacity == 0)
            return false;
        switch (ReliableMessages.PeekId(data))
        {
            case ReliableMessageId.OutpostInteractRequest when ReliableMessages.TryRead(data, out OutpostInteractRequest interact):
                requests.TryEnqueue(ClientRequest.Interact(interact));
                return true;
            case ReliableMessageId.SellQuoteRequest when ReliableMessages.TryRead(data, out SellQuoteRequest quote):
                requests.TryEnqueue(ClientRequest.Quote(quote));
                return true;
            case ReliableMessageId.SellRequest when ReliableMessages.TryRead(data, out SellRequest sell):
                requests.TryEnqueue(ClientRequest.Sell(sell));
                return true;
            case ReliableMessageId.VendorBuyRequest when ReliableMessages.TryRead(data, out VendorBuyRequest buy):
                requests.TryEnqueue(ClientRequest.Buy(buy));
                return true;
            // The Bystial book (WP-M2-8): place, swap or take out a card.
            case ReliableMessageId.BookChangeRequest when ReliableMessages.TryRead(data, out BookChangeRequest book):
                requests.TryEnqueue(ClientRequest.Book(book));
                return true;
            // Free attribute points (WP-M2-13, R-3.4-08): place one point.
            case ReliableMessageId.AttributeSpendRequest when ReliableMessages.TryRead(data, out AttributeSpendRequest attribute):
                requests.TryEnqueue(ClientRequest.Attribute(attribute));
                return true;
            case ReliableMessageId.QuestRunRequest when ReliableMessages.TryRead(data, out QuestRunRequest run): // WP-M2-6
                requests.TryEnqueue(ClientRequest.QuestRun(run));
                return true;
            default:
                return false;
        }
    }

    /// <summary>The measured round trip goes to the session's queue, where phase 1 reads it (R-3.1-03 clock-sync limits).</summary>
    public void OnRoundTripTime(int peerId, int roundTripMs)
    {
        if (_sessions.TryGetValue(peerId, out var session))
            session.Inputs.RoundTripMs = roundTripMs;
    }

    /// <summary>Network-thread state of one connected peer.</summary>
    private sealed class Session(InputQueue inputs)
    {
        /// <summary>How many sequences back from the newest the queued mask remembers.</summary>
        private const int MaskBits = 64;

        // Bit n set: sequence NewestQueued - n was queued.
        private ulong _queuedMask;

        public InputQueue Inputs { get; } = inputs;

        public bool HasQueued { get; private set; }

        public ushort NewestQueued { get; private set; }

        /// <summary>Records <paramref name="sequence"/>, newer than <see cref="NewestQueued"/>, as queued.</summary>
        public void MarkQueued(ushort sequence)
        {
            int shift = (ushort)(sequence - NewestQueued);
            _queuedMask = !HasQueued || shift >= MaskBits ? 1UL : (_queuedMask << shift) | 1UL;
            NewestQueued = sequence;
            HasQueued = true;
        }

        /// <summary>
        /// True when <paramref name="sequence"/> (not newer than <see cref="NewestQueued"/>) was queued. Frames further
        /// back than the mask remembers count as not queued.
        /// </summary>
        public bool WasQueued(ushort sequence)
        {
            int age = (ushort)(NewestQueued - sequence);
            return age < MaskBits && (_queuedMask & (1UL << age)) != 0;
        }
    }
}
