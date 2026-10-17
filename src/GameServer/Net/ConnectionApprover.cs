using GameServer.Net.Tickets;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Decides whether a connection request is accepted, before any peer is allocated (R-2.5-01):
/// protocol version first (so old clients are told to update), then the session ticket.
/// </summary>
public sealed class ConnectionApprover(TicketValidator tickets)
{
    public TicketValidation Approve(ReadOnlySpan<byte> requestData)
    {
        if (!ConnectRequest.TryReadVersion(requestData, out var version))
            return new TicketValidation(ConnectRejectReason.InvalidTicket, null);
        if (version != ProtocolVersion.Current)
            return new TicketValidation(ConnectRejectReason.VersionMismatch, null);
        if (!ConnectRequest.TryRead(requestData, out _, out var ticket))
            return new TicketValidation(ConnectRejectReason.InvalidTicket, null);
        return tickets.Validate(ticket);
    }
}
