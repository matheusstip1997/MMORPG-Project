using GameServer.Net.Tickets;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Network transport (spec 1.6, 11.2). The game never talks to LiteNetLib directly.
/// <see cref="PollEvents"/> runs on a network thread and raises listener callbacks on that thread;
/// listeners only decode and enqueue, they never touch world state.
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>The bound UDP port (useful when started on port 0).</summary>
    int LocalPort { get; }

    int ConnectedPeers { get; }

    void Start(int port);

    void Stop();

    void PollEvents();

    /// <summary>
    /// Sends one message. An unreliable message larger than <see cref="MaxUnreliableBytes"/> is dropped and counted in
    /// <see cref="UnreliableTooLarge"/>, never thrown: an unreliable message is one datagram, and the world thread that
    /// sends it must keep ticking (R-2.5-03 packet size).
    /// </summary>
    void Send(int peerId, Channel channel, ReadOnlySpan<byte> data);

    /// <summary>
    /// The largest unreliable message the peer takes now as one datagram (LiteNetLib: its current MTU minus the sequenced
    /// header), or 0 when the peer is gone. Unreliable senders write within it and leave out what does not fit (spec 1.7).
    /// Safe from the world thread; allocates nothing.
    /// </summary>
    int MaxUnreliableBytes(int peerId);

    /// <summary>Unreliable messages <see cref="Send"/> dropped because they were over the peer's limit. Safe to read from any thread.</summary>
    long UnreliableTooLarge { get; }

    /// <summary>
    /// Drops a peer, telling the client why (<paramref name="reason"/>, in the <see cref="ConnectReject"/> layout, spec
    /// 9.10). Safe to call from any thread: the disconnect happens on the network thread at its next poll.
    /// </summary>
    void Disconnect(int peerId, ConnectRejectReason reason = ConnectRejectReason.None);
}

public enum PeerDisconnectReason : byte
{
    Timeout,
    ClosedByClient,
    ClosedByServer,
    NetworkError,
    /// <summary>The server dropped the peer: a message failed decryption, was replayed, too large, or over the rate limit (R-2.5-02, -03).</summary>
    SecurityViolation,
}

/// <summary>Receives transport events. <paramref name="data"/> is only valid during the call.</summary>
public interface ITransportListener
{
    void OnPeerConnected(int peerId, SessionTicket ticket);

    void OnPeerDisconnected(int peerId, PeerDisconnectReason reason);

    void OnDataReceived(int peerId, Channel channel, ReadOnlySpan<byte> data);

    /// <summary>
    /// The round-trip time the transport measured for a peer, in ms (LiteNetLib's own ping), reported just before each input
    /// packet it came with. Lag compensation limits what view lag a client may claim with it (R-3.1-03 clock sync).
    /// </summary>
    void OnRoundTripTime(int peerId, int roundTripMs);
}
