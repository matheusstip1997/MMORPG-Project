using GameServer.Net.Tickets;
using GameServer.World;
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
/// characters. TODO(D2): reliable requests (actions, hotbar).
/// </summary>
public sealed class InputRouter : ITransportListener
{
    private readonly WorldInbox _inbox;
    private readonly int _queueCapacity;
    private readonly Dictionary<int, Session> _sessions = new();
    private readonly InputFrame[] _frames = new InputFrame[InputPacket.MaxFrames];
    private long _malformedPackets;
    private long _staleFrames;
    private long _redundantFrames;

    public InputRouter(WorldInbox inbox, int queueCapacity)
    {
        _inbox = inbox;
        _queueCapacity = queueCapacity;
    }

    /// <summary>
    /// Unreliable messages that were not a well-formed input packet (R-2.5-04). Safe to read from any thread; the world
    /// sends the total to telemetry in phase 12. TODO: count per account once bot scoring exists (spec 7.3).
    /// </summary>
    public long MalformedPackets => Interlocked.Read(ref _malformedPackets);

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
        var inputs = new InputQueue(_queueCapacity);
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
        if (channel != Channel.UnreliableSequenced || !_sessions.TryGetValue(peerId, out var session))
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
