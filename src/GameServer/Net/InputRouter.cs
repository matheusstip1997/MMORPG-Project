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
/// Each packet repeats the last few frames (spec 1.7), so most frames arrive more than once and packets can arrive out
/// of order: a frame is queued only if its sequence is newer than the newest one queued for that session. Frames
/// lost in every packet that carried them are skipped; the client's next correction covers them.
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

    /// <summary>Frames already queued from an earlier packet, or older than one that was. Safe to read from any thread.</summary>
    public long StaleFrames => Interlocked.Read(ref _staleFrames);

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
                Interlocked.Increment(ref _staleFrames);
                continue;
            }
            session.Inputs.Enqueue(frame);
            session.NewestQueued = frame.Sequence;
            session.HasQueued = true;
        }
    }

    /// <summary>Network-thread state of one connected peer.</summary>
    private sealed class Session(InputQueue inputs)
    {
        public InputQueue Inputs { get; } = inputs;

        public bool HasQueued { get; set; }

        public ushort NewestQueued { get; set; }
    }
}
