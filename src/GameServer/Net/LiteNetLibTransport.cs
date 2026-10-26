using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using GameServer.Net.Security;
using LiteNetLib;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Security;
using Shared.Util;

namespace GameServer.Net;

/// <summary>
/// <see cref="ITransport"/> over LiteNetLib (decided in spec 11.2), with the connection security of spec 2.5.
/// <para>
/// Channel crypto (R-2.5-02) wraps whole messages here rather than running as a LiteNetLib packet layer: a packet
/// layer sees raw datagrams by endpoint before LiteNetLib knows the peer, so it would have to parse LiteNetLib's
/// internal header format (merged packets, fragments, acks) to tell reliable from unreliable traffic and to let
/// connect requests through. Wrapping messages works after fragment reassembly, knows the peer and channel, and does
/// not depend on LiteNetLib internals. The cost: LiteNetLib's own headers and acks are not authenticated, so an
/// on-path attacker can disrupt a connection (as they can by dropping packets), but cannot read, alter, inject, or
/// replay game messages.
/// </para>
/// <para>
/// Order of checks: rate limits and size caps first (cheap), then crypto. Reliable messages that fail are a protocol
/// violation and the peer is dropped, because LiteNetLib has already acknowledged them and the stream cannot recover.
/// Bad unreliable inputs are dropped. Every refusal is reported to the <see cref="ISecurityEventSink"/> (R-2.5-06).
/// </para>
/// </summary>
public sealed class LiteNetLibTransport : ITransport
{
    private readonly ConnectionApprover _approver;
    private readonly ITransportListener _listener;
    private readonly NetSecurity _limits;
    private readonly IClock _clock;
    private readonly ISecurityEventSink _securityEvents;
    private readonly RateLimiter _rateLimiter;
    private readonly NetManager _manager;
    private readonly ConcurrentQueue<(int PeerId, ConnectRejectReason Reason)> _disconnects = new();
    private long _unreliableTooLarge;

    /// <summary>Decrypted reliable messages; used on the network thread only.</summary>
    private readonly byte[] _receiveBuffer;

    public LiteNetLibTransport(
        ConnectionApprover approver,
        ITransportListener listener,
        NetSecurity limits,
        IClock clock,
        ISecurityEventSink? securityEvents = null)
    {
        // Startup check (R-2.5-03 packet size): a tagged input of the table's size must fit the datagram every peer starts
        // with, and snapshots sized by the table then fit it too. A larger value would only be dropped at send time.
        if (limits.MaxUnreliableMessageBytes + SessionCrypto.InputOverhead > InitialUnreliableBytes)
            throw new ArgumentOutOfRangeException(nameof(limits),
                $"NetSecurity.maxUnreliableMessageBytes is {limits.MaxUnreliableMessageBytes}; with the {SessionCrypto.InputOverhead}-byte "
                + $"input tag it must fit the {InitialUnreliableBytes} bytes LiteNetLib sends unfragmented.");
        _approver = approver;
        _listener = listener;
        _limits = limits;
        _clock = clock;
        _securityEvents = securityEvents ?? NullSecurityEventSink.Instance;
        _rateLimiter = new RateLimiter(limits);
        _receiveBuffer = new byte[limits.MaxReliableMessageBytes];

        var events = new EventBasedNetListener();
        events.ConnectionRequestEvent += OnConnectionRequest;
        events.PeerConnectedEvent += OnPeerConnected;
        events.PeerDisconnectedEvent += OnPeerDisconnected;
        events.NetworkReceiveEvent += OnNetworkReceive;

        _manager = new NetManager(events)
        {
            AutoRecycle = true,
            // R-2.5-03: unused features are disabled.
            UnconnectedMessagesEnabled = false,
            NatPunchEnabled = false,
            BroadcastReceiveEnabled = false,
        };
    }

    public int LocalPort => _manager.LocalPort;

    public int ConnectedPeers => _manager.ConnectedPeersCount;

    public void Start(int port)
    {
        if (!_manager.Start(port))
            throw new InvalidOperationException($"Could not bind UDP port {port}.");
    }

    /// <summary>Sends the kicks still queued (shutdown kicks carry ServerMaintenance), then disconnects everyone.</summary>
    public void Stop()
    {
        RunQueuedDisconnects();
        _manager.Stop();
    }

    public void PollEvents()
    {
        RunQueuedDisconnects();
        _manager.PollEvents();
    }

    /// <summary>
    /// The unreliable payload every peer takes at first: LiteNetLib's initial MTU minus the sequenced header. MTU discovery
    /// is off, so a peer's limit never drops below it.
    /// </summary>
    public static int InitialUnreliableBytes => NetConstants.InitialMtu - NetConstants.ChanneledHeaderSize;

    public long UnreliableTooLarge => Interlocked.Read(ref _unreliableTooLarge);

    public TooLargeSend LastUnreliableTooLarge { get; private set; }

    /// <summary>LiteNetLib refuses an unfragmented packet over MTU minus its header; the MTU is a plain field it updates.</summary>
    public int MaxUnreliableBytes(int peerId) =>
        _manager.GetPeerById(peerId) is { } peer ? peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced) : 0;

    /// <summary>
    /// Reliable messages are encrypted for the peer; unreliable server messages (snapshots) are sent as they are
    /// (R-2.5-02). Sending to a peer that is gone does nothing. An unreliable message over the peer's datagram limit
    /// is dropped and counted, never thrown: LiteNetLib's <see cref="TooBigPacketException"/> would stop the world thread.
    /// </summary>
    public void Send(int peerId, Channel channel, ReadOnlySpan<byte> data)
    {
        var peer = _manager.GetPeerById(peerId);
        if (peer?.Tag is not PeerSession session || session.Violated || session.Closed)
            return;

        if (channel != Channel.ReliableOrdered)
        {
            var method = ToDeliveryMethod(channel);
            int limit = peer.GetMaxSinglePacketSize(method);
            if (data.Length > limit)
            {
                CountTooLarge(peerId, data.Length, limit);
                return;
            }
            try
            {
                peer.Send(data, method);
            }
            catch (TooBigPacketException)
            {
                // The check above makes this unreachable unless LiteNetLib changes its rule; still never the world's problem.
                CountTooLarge(peerId, data.Length, limit);
            }
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(data.Length + SessionCrypto.ReliableOverhead);
        try
        {
            var size = session.Crypto.SealReliable(data, buffer);
            peer.Send(buffer.AsSpan(0, size), DeliveryMethod.ReliableOrdered);
        }
        catch (ObjectDisposedException)
        {
            // The peer disconnected on the network thread while this send was in flight.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void CountTooLarge(int peerId, int bytes, int limit)
    {
        LastUnreliableTooLarge = new TooLargeSend(peerId, bytes, limit);
        Interlocked.Increment(ref _unreliableTooLarge);
    }

    /// <summary>Queued, so the world thread can kick a peer without touching LiteNetLib from its own thread.</summary>
    public void Disconnect(int peerId, ConnectRejectReason reason = ConnectRejectReason.None) =>
        _disconnects.Enqueue((peerId, reason));

    /// <summary>Network thread (or the stopping thread once the network thread has ended).</summary>
    private void RunQueuedDisconnects()
    {
        while (_disconnects.TryDequeue(out var kick))
        {
            if (kick.PeerId < 0)
                continue;
            var peer = _manager.GetPeerById(kick.PeerId);
            if (peer == null)
                continue;
            if (kick.Reason == ConnectRejectReason.None)
            {
                _manager.DisconnectPeer(peer);
                continue;
            }
            var data = new byte[ConnectReject.Size];
            ConnectReject.Write(data, kick.Reason, ProtocolVersion.Current);
            _manager.DisconnectPeer(peer, data);
        }
    }

    public void Dispose() => _manager.Stop();

    private void OnConnectionRequest(ConnectionRequest request)
    {
        var address = request.RemoteEndPoint.Address;
        if (_rateLimiter.TryConnectAttempt(address, _clock.UtcNowMs) != ConnectAttemptResult.Allowed)
        {
            Report(new SecurityEvent(SecurityEventKind.ConnectRateLimited, _clock.UtcNowMs, address));
            Reject(request, ConnectRejectReason.TooManyAttempts);
            return;
        }

        if (_manager.ConnectedPeersCount >= _limits.MaxPeers)
        {
            RejectAndReport(request, ConnectRejectReason.ServerFull);
            return;
        }

        var approval = _approver.Approve(request.Data.GetRemainingBytesSpan());
        if (!approval.Ok)
        {
            RejectAndReport(request, approval.Reason);
            return;
        }

        var session = new PeerSession(approval.Ticket!, new SessionCrypto(approval.SessionKey, SessionRole.Server, SystemChaCha20Poly1305.Create));
        CryptographicOperations.ZeroMemory(approval.SessionKey);
        var peer = request.Accept();
        peer.Tag = session;
    }

    private void RejectAndReport(ConnectionRequest request, ConnectRejectReason reason)
    {
        Report(new SecurityEvent(SecurityEventKind.ConnectRejected, _clock.UtcNowMs, request.RemoteEndPoint.Address, RejectReason: reason));
        Reject(request, reason);
    }

    private static void Reject(ConnectionRequest request, ConnectRejectReason reason)
    {
        var data = new byte[ConnectReject.Size];
        ConnectReject.Write(data, reason, ProtocolVersion.Current);
        request.Reject(data);
    }

    private void OnPeerConnected(NetPeer peer) =>
        _listener.OnPeerConnected(peer.Id, ((PeerSession)peer.Tag).Ticket);

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        // Rejected requests never became peers the game knows about.
        if (peer.Tag is not PeerSession session)
            return;

        var reason = session.Violated
            ? PeerDisconnectReason.SecurityViolation
            : info.Reason switch
            {
                DisconnectReason.Timeout => PeerDisconnectReason.Timeout,
                DisconnectReason.RemoteConnectionClose => PeerDisconnectReason.ClosedByClient,
                DisconnectReason.DisconnectPeerCalled => PeerDisconnectReason.ClosedByServer,
                _ => PeerDisconnectReason.NetworkError,
            };
        session.Closed = true;
        session.Crypto.Dispose();
        _listener.OnPeerDisconnected(peer.Id, reason);
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        // A closed peer's packets were sent before the client learned of a kick: its keys are gone, drop them.
        if (peer.Tag is not PeerSession session || session.Violated || session.Closed)
            return;

        var data = reader.GetRemainingBytesSpan();
        var reliable = deliveryMethod == DeliveryMethod.ReliableOrdered;

        if (!_rateLimiter.TryMessage(ref session.MessageBucket, _clock.UtcNowMs))
        {
            // An acknowledged reliable message cannot be dropped without breaking the stream, so the peer goes.
            if (reliable)
                Violation(peer, session, SecurityEventKind.MessageRateLimited);
            else
                Report(PeerEvent(SecurityEventKind.MessageRateLimited, peer, session));
            return;
        }

        if (reliable)
            ReceiveReliable(peer, session, data);
        else
            ReceiveInput(peer, session, data);
    }

    private void ReceiveReliable(NetPeer peer, PeerSession session, ReadOnlySpan<byte> data)
    {
        if (data.Length > _limits.MaxReliableMessageBytes + SessionCrypto.ReliableOverhead)
        {
            Violation(peer, session, SecurityEventKind.MessageTooLarge);
            return;
        }

        switch (session.Crypto.OpenReliable(data, _receiveBuffer, out var length))
        {
            case OpenResult.Ok:
                _listener.OnDataReceived(peer.Id, Channel.ReliableOrdered, _receiveBuffer.AsSpan(0, length));
                break;
            case OpenResult.Replayed:
                Violation(peer, session, SecurityEventKind.ReliableReplayed);
                break;
            default:
                Violation(peer, session, SecurityEventKind.ReliableDecryptFailed);
                break;
        }
    }

    private void ReceiveInput(NetPeer peer, PeerSession session, ReadOnlySpan<byte> data)
    {
        if (data.Length > _limits.MaxUnreliableMessageBytes + SessionCrypto.InputOverhead)
        {
            Violation(peer, session, SecurityEventKind.MessageTooLarge);
            return;
        }

        switch (session.Crypto.VerifyInput(data, out var payload))
        {
            case OpenResult.Ok:
                _listener.OnRoundTripTime(peer.Id, peer.RoundTripTime);
                _listener.OnDataReceived(peer.Id, Channel.UnreliableSequenced, payload);
                break;
            case OpenResult.Replayed:
                Report(PeerEvent(SecurityEventKind.InputReplayed, peer, session));
                break;
            default:
                Report(PeerEvent(SecurityEventKind.InputTagFailed, peer, session));
                break;
        }
    }

    private void Violation(NetPeer peer, PeerSession session, SecurityEventKind kind)
    {
        session.Violated = true;
        Report(PeerEvent(kind, peer, session));
        _manager.DisconnectPeer(peer);
    }

    private SecurityEvent PeerEvent(SecurityEventKind kind, NetPeer peer, PeerSession session) =>
        new(kind, _clock.UtcNowMs, peer.Address, peer.Id, session.Ticket.AccountId);

    private void Report(in SecurityEvent securityEvent) => _securityEvents.Report(securityEvent);

    private static DeliveryMethod ToDeliveryMethod(Channel channel) => channel switch
    {
        Channel.ReliableOrdered => DeliveryMethod.ReliableOrdered,
        Channel.UnreliableSequenced => DeliveryMethod.Sequenced,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };
}
