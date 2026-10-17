using System.Security.Cryptography;
using Shared.Util;

namespace GameServer.Net.Tickets;

/// <summary>
/// Issues tickets the way the Auth API will, for tests, bots, and the dev login (spec 2.1).
/// It needs an explicit signing key; the dev login endpoint that exposes it must stay behind a build flag.
/// </summary>
public sealed class DevTicketIssuer(byte keyId, byte[] key, IClock clock, int ticketLifetimeMs)
{
    private readonly byte[] _key = key.Length >= TicketKeyRing.MinKeySize
        ? key.ToArray()
        : throw new ArgumentException($"Ticket keys must be at least {TicketKeyRing.MinKeySize} bytes.", nameof(key));

    public byte[] Issue(long accountId, int realmId, byte slot)
    {
        var ticket = new SessionTicket(
            accountId,
            realmId,
            slot,
            ExpiresAtMs: clock.UtcNowMs + ticketLifetimeMs,
            Nonce: RandomNumberGenerator.GetBytes(SessionTicket.NonceSize),
            KeyId: keyId);
        return SessionTicketCodec.Encode(ticket, _key);
    }
}
