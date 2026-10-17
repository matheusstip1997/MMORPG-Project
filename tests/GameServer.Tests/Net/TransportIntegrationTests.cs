using System.Diagnostics;
using System.Net;
using GameServer.Net;
using GameServer.Net.Tickets;
using GameServer.Tests.Support;
using LiteNetLib;
using Shared.Protocol;

namespace GameServer.Tests.Net;

/// <summary>A real LiteNetLib client connects to the server transport over localhost.</summary>
public sealed class TransportIntegrationTests : IDisposable
{
    private const int Realm = 1;
    private const int LifetimeMs = 60_000;
    private static readonly byte[] Key = Enumerable.Repeat((byte)0x5A, 32).ToArray();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeClock _clock = new();
    private readonly RecordingListener _serverEvents = new();
    private readonly List<TestClient> _clients = [];
    private LiteNetLibTransport? _server;

    public void Dispose()
    {
        foreach (var client in _clients)
            client.Dispose();
        _server?.Dispose();
    }

    private LiteNetLibTransport StartServer(int maxPeers = 10)
    {
        var validator = new TicketValidator(new TicketKeyRing().Add(1, Key), new InMemoryNonceCache(_clock), _clock, Realm, LifetimeMs);
        _server = new LiteNetLibTransport(new ConnectionApprover(validator), _serverEvents, maxPeers);
        _server.Start(0);
        return _server;
    }

    private byte[] IssueTicket(long accountId = 7) => new DevTicketIssuer(1, Key, _clock, LifetimeMs).Issue(accountId, Realm, slot: 0);

    private TestClient Connect(byte[] ticket, ushort version = ProtocolVersion.Current)
    {
        var request = new byte[ConnectRequest.SizeFor(ticket.Length)];
        ConnectRequest.Write(request, version, ticket);
        var client = new TestClient(_server!.LocalPort, request);
        _clients.Add(client);
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
                client.Poll();
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Client_with_a_valid_ticket_connects_and_the_server_sees_its_account()
    {
        StartServer();
        var client = Connect(IssueTicket(accountId: 1234));

        PumpUntil(() => client.Connected && _serverEvents.Connected.Count == 1);

        Assert.Equal(1234, _serverEvents.Connected[0].Ticket.AccountId);
        Assert.Equal(1, _server!.ConnectedPeers);
    }

    [Fact]
    public void Data_flows_both_ways_on_both_channels()
    {
        StartServer();
        var client = Connect(IssueTicket());
        PumpUntil(() => client.Connected && _serverEvents.Connected.Count == 1);
        var peerId = _serverEvents.Connected[0].PeerId;

        client.Send([1, 2, 3], DeliveryMethod.ReliableOrdered);
        client.Send([4, 5], DeliveryMethod.Sequenced);
        _server!.Send(peerId, Channel.ReliableOrdered, [9, 8, 7]);
        PumpUntil(() => _serverEvents.Received.Count == 2 && client.Received.Count == 1);

        Assert.Contains(_serverEvents.Received, r => r.Channel == Channel.ReliableOrdered && r.Data.SequenceEqual(new byte[] { 1, 2, 3 }));
        Assert.Contains(_serverEvents.Received, r => r.Channel == Channel.UnreliableSequenced && r.Data.SequenceEqual(new byte[] { 4, 5 }));
        Assert.Equal(new byte[] { 9, 8, 7 }, client.Received[0]);
    }

    [Fact]
    public void Client_with_another_protocol_version_is_told_the_server_version()
    {
        StartServer();
        var client = Connect(IssueTicket(), version: ProtocolVersion.Current + 1);

        PumpUntil(() => client.Rejected != null);

        Assert.Equal(ConnectRejectReason.VersionMismatch, client.Rejected);
        Assert.Equal(ProtocolVersion.Current, client.ServerVersion);
        Assert.Empty(_serverEvents.Connected);
    }

    [Fact]
    public void Tampered_ticket_is_rejected()
    {
        StartServer();
        var ticket = IssueTicket();
        ticket[^1] ^= 0xFF;
        var client = Connect(ticket);

        PumpUntil(() => client.Rejected != null);

        Assert.Equal(ConnectRejectReason.InvalidTicket, client.Rejected);
    }

    [Fact]
    public void Expired_ticket_is_rejected_as_expired()
    {
        StartServer();
        var ticket = IssueTicket();
        _clock.Advance(LifetimeMs + 1);
        var client = Connect(ticket);

        PumpUntil(() => client.Rejected != null);

        Assert.Equal(ConnectRejectReason.TicketExpired, client.Rejected);
    }

    [Fact]
    public void Reused_ticket_is_rejected()
    {
        StartServer();
        var ticket = IssueTicket();
        var first = Connect(ticket);
        PumpUntil(() => first.Connected);

        var second = Connect(ticket);
        PumpUntil(() => second.Rejected != null);

        Assert.Equal(ConnectRejectReason.InvalidTicket, second.Rejected);
        Assert.Single(_serverEvents.Connected);
    }

    [Fact]
    public void Server_at_max_peers_rejects_with_server_full()
    {
        StartServer(maxPeers: 1);
        var first = Connect(IssueTicket(accountId: 1));
        PumpUntil(() => first.Connected && _server!.ConnectedPeers == 1);

        var second = Connect(IssueTicket(accountId: 2));
        PumpUntil(() => second.Rejected != null);

        Assert.Equal(ConnectRejectReason.ServerFull, second.Rejected);
    }

    [Fact]
    public void Client_disconnect_reaches_the_server()
    {
        StartServer();
        var client = Connect(IssueTicket());
        PumpUntil(() => client.Connected && _serverEvents.Connected.Count == 1);

        client.Disconnect();
        PumpUntil(() => _serverEvents.Disconnected.Count == 1);

        Assert.Equal(PeerDisconnectReason.ClosedByClient, _serverEvents.Disconnected[0].Reason);
    }

    private sealed class RecordingListener : ITransportListener
    {
        public List<(int PeerId, SessionTicket Ticket)> Connected { get; } = [];
        public List<(int PeerId, PeerDisconnectReason Reason)> Disconnected { get; } = [];
        public List<(int PeerId, Channel Channel, byte[] Data)> Received { get; } = [];

        public void OnPeerConnected(int peerId, SessionTicket ticket) => Connected.Add((peerId, ticket));

        public void OnPeerDisconnected(int peerId, PeerDisconnectReason reason) => Disconnected.Add((peerId, reason));

        public void OnDataReceived(int peerId, Channel channel, ReadOnlySpan<byte> data) => Received.Add((peerId, channel, data.ToArray()));
    }

    private sealed class TestClient : IDisposable
    {
        private readonly NetManager _manager;
        private NetPeer? _peer;

        public TestClient(int port, byte[] request)
        {
            var events = new EventBasedNetListener();
            events.PeerConnectedEvent += peer => { _peer = peer; Connected = true; };
            events.PeerDisconnectedEvent += (_, info) =>
            {
                if (info.Reason == DisconnectReason.ConnectionRejected
                    && ConnectReject.TryRead(info.AdditionalData.GetRemainingBytesSpan(), out var reason, out var version))
                {
                    Rejected = reason;
                    ServerVersion = version;
                }
            };
            events.NetworkReceiveEvent += (_, reader, _, _) => Received.Add(reader.GetRemainingBytes());

            _manager = new NetManager(events) { AutoRecycle = true };
            _manager.Start();
            _manager.Connect(new IPEndPoint(IPAddress.Loopback, port), request);
        }

        public bool Connected { get; private set; }
        public ConnectRejectReason? Rejected { get; private set; }
        public ushort ServerVersion { get; private set; }
        public List<byte[]> Received { get; } = [];

        public void Poll() => _manager.PollEvents();

        public void Send(byte[] data, DeliveryMethod method) => _peer!.Send(data, method);

        public void Disconnect() => _peer!.Disconnect();

        public void Dispose() => _manager.Stop();
    }
}
