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

    void Send(int peerId, Channel channel, ReadOnlySpan<byte> data);

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
