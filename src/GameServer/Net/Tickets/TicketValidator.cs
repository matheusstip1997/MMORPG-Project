using Shared.Protocol;
using Shared.Util;

namespace GameServer.Net.Tickets;

public readonly record struct TicketValidation(ConnectRejectReason Reason, SessionTicket? Ticket)
{
    public bool Ok => Reason == ConnectRejectReason.None;
}

/// <summary>
/// Checks a session ticket before the transport accepts the peer (R-2.5-01): format, key ID, signature,
/// realm, expiry, and single use. The nonce is consumed only when every other check passed.
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

        if (!nonces.TryUse(ticket.Nonce, ticket.ExpiresAtMs))
            return Invalid();

        return new TicketValidation(ConnectRejectReason.None, ticket);
    }

    private static TicketValidation Invalid() => new(ConnectRejectReason.InvalidTicket, null);
}
