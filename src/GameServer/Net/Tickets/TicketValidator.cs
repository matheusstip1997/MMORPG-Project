using Shared.Protocol;
using Shared.Util;

namespace GameServer.Net.Tickets;

/// <summary>
/// Result of a ticket check. <see cref="SessionKey"/> is set only when <see cref="Ok"/>; the transport keeps it
/// for channel crypto (R-2.5-02) and does not pass it on.
/// </summary>
public readonly record struct TicketValidation(ConnectRejectReason Reason, SessionTicket? Ticket, byte[]? SessionKey = null)
{
    public bool Ok => Reason == ConnectRejectReason.None;
}

/// <summary>
/// Checks a session ticket before the transport accepts the peer (R-2.5-01): format and version, key ID, signature,
/// session key, realm, expiry, and single use. The nonce is consumed only when every other check passed.
/// </summary>
public sealed class TicketValidator(TicketKeyRing keys, INonceCache nonces, IClock clock, int realmId, int ticketLifetimeMs)
{
    public TicketValidation Validate(ReadOnlySpan<byte> ticketBytes)
    {
        if (!SessionTicketCodec.TryReadPayload(ticketBytes, out var ticket))
            return Invalid();
        if (!keys.TryGet(ticket.KeyId, out var key) || !SessionTicketCodec.HasValidSignature(ticketBytes, key))
            return Invalid();
        if (ticket.RealmId != realmId)
            return Invalid();

        var now = clock.UtcNowMs;
        if (ticket.ExpiresAtMs <= now)
            return new TicketValidation(ConnectRejectReason.TicketExpired, null);
        // A ticket that claims to live longer than any issuer may grant is not trusted.
        if (ticket.ExpiresAtMs - now > ticketLifetimeMs)
            return Invalid();

        // The signature already covers the sealed key, so this fails only if the issuer itself was wrong.
        var sessionKey = new byte[SessionTicket.SessionKeySize];
        if (!SessionTicketCodec.TryDecryptSessionKey(ticketBytes, key, sessionKey))
            return Invalid();

        bool firstUse;
        try
        {
            firstUse = nonces.TryUse(ticket.Nonce, ticket.ExpiresAtMs);
        }
        catch (Exception)
        {
            // The shared nonce store (Redis) is unreachable: single use cannot be proven, so refuse (fail closed).
            return new TicketValidation(ConnectRejectReason.ServerUnavailable, null);
        }
        if (!firstUse)
            return Invalid();

        return new TicketValidation(ConnectRejectReason.None, ticket, sessionKey);
    }

    private static TicketValidation Invalid() => new(ConnectRejectReason.InvalidTicket, null);
}
