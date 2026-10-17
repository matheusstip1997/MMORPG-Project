using System.Diagnostics;
using Game.Net;
using GameServer.Net;
using GameServer.Net.Tickets;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Tests.Net;

/// <summary>
/// The Unity client's <see cref="ClientConnection"/> (unity/Assets/Game/Net/Client, compiled into this project)
/// against the real server transport over localhost: handshake, reject codes, crypto on both channels (R-2.5-01, -02, -04).
/// </summary>
public sealed class UnityClientConnectionTests : IDisposable
{
    private const int Realm = 1;
    private const int LifetimeMs = 60_000;
    private static readonly byte[] Key = Enumerable.Repeat((byte)0x3C, 32).ToArray();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeClock _clock = new();
    private readonly ServerEvents _serverEvents = new();
    private readonly List<(ClientConnection Connection, ClientEvents Events)> _clients = [];
    private LiteNetLibTransport? _server;

    public void Dispose()
    {
        foreach (var client in _clients)
            client.Connection.Dispose();
        _server?.Dispose();
    }

    private void StartServer(NetSecurity? limits = null)
    {
        var validator = new TicketValidator(new TicketKeyRing().Add(1, Key), new InMemoryNonceCache(_clock), _clock, Realm, LifetimeMs);
        _server = new LiteNetLibTransport(new ConnectionApprover(validator), _serverEvents, limits ?? TestLimits.Create(), _clock);
        _server.Start(0);
    }

    private IssuedTicket IssueTicket() => new DevTicketIssuer(1, Key, _clock, LifetimeMs).Issue(accountId: 42, Realm, slot: 0);

    private (ClientConnection Connection, ClientEvents Events) Connect(IssuedTicket issued, int maxReliable = 4_096, int maxUnreliable = 1_200)
    {
        var events = new ClientEvents();
        var connection = new ClientConnection(events, new ClientNetLimits(maxReliable, maxUnreliable));
        _clients.Add((connection, events));
        connection.Connect("127.0.0.1", _server!.LocalPort, issued.Ticket, issued.SessionKey);
        return (connection, events);
    }

    private (ClientConnection Connection, ClientEvents Events) ConnectAndWait()
    {
        var client = Connect(IssueTicket());
        PumpUntil(() => client.Events.Connected && _serverEvents.Connected.Count == 1);
        return client;
    }

    private void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(watch.Elapsed < Timeout, "Timed out waiting for the network.");
            _server!.PollEvents();
            foreach (var client in _clients)
                client.Connection.PollEvents();
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Connects_with_a_valid_ticket()
    {
        StartServer();
        var (connection, _) = ConnectAndWait();

        Assert.Equal(ConnectionState.Connected, connection.State);
        Assert.Equal(42, _serverEvents.Connected[0].Ticket.AccountId);
    }

    [Fact]
    public void Reliable_messages_are_encrypted_both_ways_and_inputs_are_tagged()
    {
        StartServer();
        var (connection, events) = ConnectAndWait();
        var peerId = _serverEvents.Connected[0].PeerId;

        Assert.True(connection.SendReliable([1, 2, 3]));
        Assert.True(connection.SendInput([(byte)HotMessageId.Input, 5]));
        _server!.Send(peerId, Channel.ReliableOrdered, [9, 8, 7]);
        _server.Send(peerId, Channel.UnreliableSequenced, [(byte)HotMessageId.Snapshot, 6]);
        PumpUntil(() => _serverEvents.Received.Count == 2 && events.Reliable.Count == 1 && events.Unreliable.Count == 1);

        Assert.Contains(_serverEvents.Received, r => r.Channel == Channel.ReliableOrdered && r.Data.SequenceEqual(new byte[] { 1, 2, 3 }));
        Assert.Contains(_serverEvents.Received, r => r.Channel == Channel.UnreliableSequenced && r.Data.SequenceEqual(new byte[] { 1, 5 }));
        Assert.Equal(new byte[] { 9, 8, 7 }, events.Reliable[0]);
        Assert.Equal(new byte[] { 2, 6 }, events.Unreliable[0]);
    }

    [Fact]
    public void Unknown_server_hot_messages_are_dropped()
    {
        StartServer();
        var (_, events) = ConnectAndWait();
        var peerId = _serverEvents.Connected[0].PeerId;

        _server!.Send(peerId, Channel.UnreliableSequenced, [0xEE, 1]);
        _server.Send(peerId, Channel.ReliableOrdered, [1]);
        PumpUntil(() => events.Reliable.Count == 1);

        Assert.Empty(events.Unreliable);
    }

    [Fact]
    public void Messages_over_the_caps_are_not_sent()
    {
        StartServer();
        var (connection, _) = ConnectAndWait();

        Assert.False(connection.SendReliable(new byte[4_097]));
        Assert.False(connection.SendInput(new byte[1_201]));
    }

    [Fact]
    public void Nothing_is_sent_before_the_connection_is_up()
    {
        StartServer();
        var (connection, _) = Connect(IssueTicket());

        Assert.Equal(ConnectionState.Connecting, connection.State);
        Assert.False(connection.SendReliable([1]));
        Assert.False(connection.SendInput([1]));
    }

    [Fact]
    public void Wrong_session_key_cannot_talk_and_the_server_drops_the_peer()
    {
        StartServer();
        var issued = IssueTicket();
        var (connection, events) = Connect(issued with { SessionKey = new byte[32] });
        PumpUntil(() => events.Connected);

        connection.SendReliable([1, 2, 3]);
        PumpUntil(() => events.Disconnects.Count == 1);

        Assert.Empty(_serverEvents.Received);
        Assert.Equal(ClientDisconnectReason.ClosedByServer, events.Disconnects[0].Reason);
    }

    [Fact]
    public void Server_message_that_does_not_decrypt_makes_the_client_disconnect()
    {
        StartServer();
        var issued = IssueTicket();
        var (_, events) = Connect(issued with { SessionKey = new byte[32] });
        PumpUntil(() => events.Connected && _serverEvents.Connected.Count == 1);

        _server!.Send(_serverEvents.Connected[0].PeerId, Channel.ReliableOrdered, [1, 2, 3]);
        PumpUntil(() => events.Disconnects.Count == 1 && _serverEvents.Disconnected.Count == 1);

        Assert.Empty(events.Reliable);
        Assert.Equal(ClientDisconnectReason.SecurityViolation, events.Disconnects[0].Reason);
    }

    [Fact]
    public void Server_reliable_message_over_the_client_cap_makes_the_client_disconnect()
    {
        StartServer();
        var (_, events) = Connect(IssueTicket(), maxReliable: 64);
        PumpUntil(() => events.Connected && _serverEvents.Connected.Count == 1);

        _server!.Send(_serverEvents.Connected[0].PeerId, Channel.ReliableOrdered, new byte[65]);
        PumpUntil(() => events.Disconnects.Count == 1);

        Assert.Equal(ClientDisconnectReason.SecurityViolation, events.Disconnects[0].Reason);
    }

    [Fact]
    public void Tampered_ticket_is_rejected_with_its_reason_code()
    {
        StartServer();
        var issued = IssueTicket();
        issued.Ticket[^1] ^= 0xFF;
        var (connection, events) = Connect(issued);

        PumpUntil(() => events.Disconnects.Count == 1);

        var disconnect = events.Disconnects[0];
        Assert.Equal(ClientDisconnectReason.Rejected, disconnect.Reason);
        Assert.Equal(ConnectRejectReason.InvalidTicket, disconnect.RejectReason);
        Assert.Equal(ProtocolVersion.Current, disconnect.ServerVersion);
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public void Expired_ticket_is_rejected_as_expired()
    {
        StartServer();
        var issued = IssueTicket();
        _clock.Advance(LifetimeMs + 1);
        var (_, events) = Connect(issued);

        PumpUntil(() => events.Disconnects.Count == 1);

        Assert.Equal(ConnectRejectReason.TicketExpired, events.Disconnects[0].RejectReason);
    }

    [Fact]
    public void Full_server_is_reported_as_server_full()
    {
        StartServer(TestLimits.Create(maxPeers: 1));
        ConnectAndWait();
        var (_, events) = Connect(IssueTicket());

        PumpUntil(() => events.Disconnects.Count == 1);

        Assert.Equal(ConnectRejectReason.ServerFull, events.Disconnects[0].RejectReason);
    }

    [Fact]
    public void Client_disconnect_reaches_both_sides_and_the_client_can_reconnect()
    {
        StartServer();
        var (connection, events) = ConnectAndWait();

        connection.Disconnect();
        PumpUntil(() => events.Disconnects.Count == 1 && _serverEvents.Disconnected.Count == 1);
        Assert.Equal(ClientDisconnectReason.ClosedByClient, events.Disconnects[0].Reason);
        Assert.Equal(PeerDisconnectReason.ClosedByClient, _serverEvents.Disconnected[0].Reason);

        var issued = IssueTicket();
        connection.Connect("127.0.0.1", _server!.LocalPort, issued.Ticket, issued.SessionKey);
        PumpUntil(() => connection.State == ConnectionState.Connected && _serverEvents.Connected.Count == 2);
        connection.SendReliable([7]);
        PumpUntil(() => _serverEvents.Received.Count == 1);
    }

    [Fact]
    public void Server_drop_is_reported_as_closed_by_server()
    {
        StartServer();
        var (_, events) = ConnectAndWait();

        _server!.Disconnect(_serverEvents.Connected[0].PeerId);
        PumpUntil(() => events.Disconnects.Count == 1);

        Assert.Equal(ClientDisconnectReason.ClosedByServer, events.Disconnects[0].Reason);
    }

    private sealed class ClientEvents : IClientConnectionListener
    {
        public bool Connected { get; private set; }
        public List<(ClientDisconnectReason Reason, ConnectRejectReason RejectReason, ushort ServerVersion)> Disconnects { get; } = [];
        public List<byte[]> Reliable { get; } = [];
        public List<byte[]> Unreliable { get; } = [];

        public void OnConnected() => Connected = true;

        public void OnDisconnected(ClientDisconnectReason reason, ConnectRejectReason rejectReason, ushort serverProtocolVersion) =>
            Disconnects.Add((reason, rejectReason, serverProtocolVersion));

        public void OnReliableReceived(ReadOnlySpan<byte> message) => Reliable.Add(message.ToArray());

        public void OnUnreliableReceived(ReadOnlySpan<byte> message) => Unreliable.Add(message.ToArray());
    }

    private sealed class ServerEvents : ITransportListener
    {
        public List<(int PeerId, SessionTicket Ticket)> Connected { get; } = [];
        public List<(int PeerId, PeerDisconnectReason Reason)> Disconnected { get; } = [];
        public List<(int PeerId, Channel Channel, byte[] Data)> Received { get; } = [];

        public void OnPeerConnected(int peerId, SessionTicket ticket) => Connected.Add((peerId, ticket));

        public void OnPeerDisconnected(int peerId, PeerDisconnectReason reason) => Disconnected.Add((peerId, reason));

        public void OnDataReceived(int peerId, Channel channel, ReadOnlySpan<byte> data) => Received.Add((peerId, channel, data.ToArray()));
    }
}
