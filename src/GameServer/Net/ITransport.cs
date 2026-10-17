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

    void Disconnect(int peerId);
}

public enum PeerDisconnectReason : byte
{
    Timeout,
    ClosedByClient,
    ClosedByServer,
    NetworkError,
}

/// <summary>Receives transport events. <paramref name="data"/> is only valid during the call.</summary>
public interface ITransportListener
{
    void OnPeerConnected(int peerId, SessionTicket ticket);

    void OnPeerDisconnected(int peerId, PeerDisconnectReason reason);

    void OnDataReceived(int peerId, Channel channel, ReadOnlySpan<byte> data);
}
