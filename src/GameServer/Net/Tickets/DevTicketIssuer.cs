using System.Security.Cryptography;
using Shared.Util;

namespace GameServer.Net.Tickets;

/// <summary>What the Auth API hands the client over HTTPS: the opaque ticket and the session key (R-2.5-02).</summary>
public sealed record IssuedTicket(byte[] Ticket, byte[] SessionKey);

/// <summary>
/// Issues tickets the way the Auth API will, for tests, bots, and the dev login (spec 2.1).
/// It needs an explicit secret; the dev login endpoint that exposes it must stay behind a build flag.
/// </summary>
public sealed class DevTicketIssuer(byte keyId, byte[] secret, IClock clock, int ticketLifetimeMs)
{
    private readonly TicketKey _key = TicketKey.FromSecret(secret);

    public IssuedTicket Issue(long accountId, int realmId, byte slot)
    {
        var ticket = new SessionTicket(
            accountId,
            realmId,
            slot,
            ExpiresAtMs: clock.UtcNowMs + ticketLifetimeMs,
            Nonce: RandomNumberGenerator.GetBytes(SessionTicket.NonceSize),
            KeyId: keyId);
        var sessionKey = RandomNumberGenerator.GetBytes(SessionTicket.SessionKeySize);
        return new IssuedTicket(SessionTicketCodec.Encode(ticket, sessionKey, _key), sessionKey);
    }
}
