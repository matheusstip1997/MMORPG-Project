using GameServer.Net.Security;
using GameServer.Net.Tickets;
using Shared.Security;

namespace GameServer.Net;

/// <summary>Transport state of one accepted peer, stored in <c>NetPeer.Tag</c>.</summary>
internal sealed class PeerSession(SessionTicket ticket, SessionCrypto crypto)
{
    public SessionTicket Ticket { get; } = ticket;

    public SessionCrypto Crypto { get; } = crypto;

    /// <summary>Messages per second from this peer (R-2.5-03). A field so the limiter can update it in place.</summary>
    public TokenBucket MessageBucket;

    /// <summary>Set when the server dropped the peer for a security violation; later messages are ignored.</summary>
    public volatile bool Violated;

    /// <summary>
    /// Set when the peer disconnected, before its keys are disposed. LiteNetLib can still deliver packets the client sent
    /// before it learned of a server kick; they are dropped, never opened with the disposed keys.
    /// </summary>
    public volatile bool Closed;
}
