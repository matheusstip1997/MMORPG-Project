using Shared.Protocol;

namespace Shared.Tests.Protocol;

public class ConnectMessagesTests
{
    [Fact]
    public void Connect_request_round_trips()
    {
        byte[] ticket = [1, 2, 3, 4, 5];
        var buffer = new byte[ConnectRequest.SizeFor(ticket.Length)];

        var written = ConnectRequest.Write(buffer, 7, ticket);

        Assert.Equal(buffer.Length, written);
        Assert.True(ConnectRequest.TryRead(buffer, out var version, out var readTicket));
        Assert.Equal(7, version);
        Assert.Equal(ticket, readTicket.ToArray());
    }

    [Fact]
    public void Connect_request_with_wrong_ticket_length_is_refused()
    {
        var buffer = new byte[ConnectRequest.SizeFor(3)];
        ConnectRequest.Write(buffer, 1, [1, 2, 3]);

        Assert.False(ConnectRequest.TryRead(buffer.AsSpan(0, buffer.Length - 1), out _, out _));
        Assert.False(ConnectRequest.TryRead([.. buffer, 0], out _, out _));
    }

    [Fact]
    public void Connect_request_declaring_an_oversized_ticket_is_refused()
    {
        // R-2.5-04: every length is bound-checked.
        var buffer = new byte[ConnectRequest.HeaderSize + ConnectRequest.MaxTicketLength + 1];
        buffer[2] = (ConnectRequest.MaxTicketLength + 1) & 0xFF;
        buffer[3] = (ConnectRequest.MaxTicketLength + 1) >> 8;

        Assert.False(ConnectRequest.TryRead(buffer, out _, out _));
    }

    [Fact]
    public void Version_is_readable_even_when_the_rest_is_not()
    {
        byte[] buffer = [3, 0, 0xFF];

        Assert.True(ConnectRequest.TryReadVersion(buffer, out var version));
        Assert.Equal(3, version);
        Assert.False(ConnectRequest.TryRead(buffer, out _, out _));
    }

    [Fact]
    public void Connect_reject_round_trips()
    {
        var buffer = new byte[ConnectReject.Size];

        ConnectReject.Write(buffer, ConnectRejectReason.TicketExpired, 9);

        Assert.True(ConnectReject.TryRead(buffer, out var reason, out var serverVersion));
        Assert.Equal(ConnectRejectReason.TicketExpired, reason);
        Assert.Equal(9, serverVersion);
    }
}
