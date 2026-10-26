using System.Diagnostics;
using System.Net;
using GameServer.Net;
using GameServer.Net.Security;
using Shared.Security;
using GameServer.Net.Tickets;
using GameServer.Tests.Support;
using LiteNetLib;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Tests.Net;

/// <summary>A real LiteNetLib client connects to the server transport over localhost (spec 2.5).</summary>
public sealed class TransportIntegrationTests : IDisposable
{
    private const int Realm = 1;
    private const int LifetimeMs = 60_000;
    private static readonly byte[] Key = Enumerable.Repeat((byte)0x5A, 32).ToArray();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeClock _clock = new();
    private readonly RecordingListener _serverEvents = new();
    private readonly RecordingSecurityEvents _securityEvents = new();
    private readonly List<TestClient> _clients = [];
    private LiteNetLibTransport? _server;

    public void Dispose()
    {
        foreach (var client in _clients)
            client.Dispose();
        _server?.Dispose();
    }

    private LiteNetLibTransport StartServer(NetSecurity? limits = null)
    {
        var validator = new TicketValidator(new TicketKeyRing().Add(1, Key), new InMemoryNonceCache(_clock), _clock, Realm, LifetimeMs);
        _server = new LiteNetLibTransport(new ConnectionApprover(validator), _serverEvents, limits ?? TestLimits.Create(), _clock, _securityEvents);
        _server.Start(0);
        return _server;
    }

    private IssuedTicket IssueTicket(long accountId = 7) => new DevTicketIssuer(1, Key, _clock, LifetimeMs).Issue(accountId, Realm, slot: 0);

    private TestClient Connect(IssuedTicket issued, ushort version = ProtocolVersion.Current, int? port = null)
    {
        var request = new byte[ConnectRequest.SizeFor(issued.Ticket.Length)];
        ConnectRequest.Write(request, version, issued.Ticket);
        var client = new TestClient(port ?? _server!.LocalPort, request, issued.SessionKey);
        _clients.Add(client);
        return client;
    }

    private TestClient ConnectAndWait(long accountId = 7, int? port = null)
    {
        var client = Connect(IssueTicket(accountId), port: port);
        var expected = _serverEvents.Connected.Count + 1;
        PumpUntil(() => client.Connected && _serverEvents.Connected.Count == expected);
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

    /// <summary>Gives in-flight packets time to arrive when the expected outcome is that nothing happens.</summary>
    private void PumpFor(TimeSpan duration)
    {
        var watch = Stopwatch.StartNew();
        PumpUntil(() => watch.Elapsed >= duration);
    }

    [Fact]
    public void Client_with_a_valid_ticket_connects_and_the_server_sees_its_account()
    {
        StartServer();
        var client = Connect(IssueTicket(accountId: 1234));

        PumpUntil(() => client.Connected && _serverEvents.Connected.Count == 1);

        Assert.Equal(1234, _serverEvents.Connected[0].Ticket.AccountId);
        Assert.Equal(1, _server!.ConnectedPeers);
        Assert.Empty(_securityEvents.Events);
    }

    [Fact]
    public void Encrypted_round_trip_on_both_channels()
    {
        StartServer();
        var client = ConnectAndWait();
        var peerId = _serverEvents.Connected[0].PeerId;

        client.SendReliable([1, 2, 3]);
        client.SendInput([4, 5]);
        _server!.Send(peerId, Channel.ReliableOrdered, [9, 8, 7]);
        _server.Send(peerId, Channel.UnreliableSequenced, [6]);
        PumpUntil(() => _serverEvents.Received.Count == 2 && client.Received.Count == 2);

        Assert.Contains(_serverEvents.Received, r => r.Channel == Channel.ReliableOrdered && r.Data.SequenceEqual(new byte[] { 1, 2, 3 }));
        Assert.Contains(_serverEvents.Received, r => r.Channel == Channel.UnreliableSequenced && r.Data.SequenceEqual(new byte[] { 4, 5 }));
        Assert.Contains(client.Received, r => r.Method == DeliveryMethod.ReliableOrdered && r.Data.SequenceEqual(new byte[] { 9, 8, 7 }));
        // R-2.5-02: server snapshots on the unreliable channel are neither encrypted nor tagged.
        Assert.Contains(client.Received, r => r.Method == DeliveryMethod.Sequenced && r.Data.SequenceEqual(new byte[] { 6 }));
        Assert.Empty(_securityEvents.Events);
    }

    [Fact]
    public void Reliable_messages_are_encrypted_on_the_wire()
    {
        StartServer();
        var client = ConnectAndWait();
        var secret = "whisper: meet at the docks"u8.ToArray();

        _server!.Send(_serverEvents.Connected[0].PeerId, Channel.ReliableOrdered, secret);
        PumpUntil(() => client.RawReceived.Count == 1);

        var wire = client.RawReceived[0];
        Assert.Equal(secret.Length + SessionCrypto.ReliableOverhead, wire.Length);
        Assert.Equal(-1, wire.AsSpan().IndexOf(secret.AsSpan(0, 8)));
    }

    [Fact]
    public void Tampered_reliable_message_drops_the_peer_and_is_reported()
    {
        StartServer();
        var client = ConnectAndWait(accountId: 55);
        var packet = client.Seal([1, 2, 3]);
        packet[^1] ^= 0x01;

        client.SendRaw(packet, DeliveryMethod.ReliableOrdered);
        PumpUntil(() => _serverEvents.Disconnected.Count == 1);

        Assert.Empty(_serverEvents.Received);
        Assert.Equal(PeerDisconnectReason.SecurityViolation, _serverEvents.Disconnected[0].Reason);
        var reported = Assert.Single(_securityEvents.Events);
        Assert.Equal(SecurityEventKind.ReliableDecryptFailed, reported.Kind);
        Assert.Equal(55, reported.AccountId);
        Assert.Equal(IPAddress.Loopback, reported.Address);
    }

    [Fact]
    public void Replayed_reliable_message_drops_the_peer()
    {
        StartServer();
        var client = ConnectAndWait();
        var packet = client.Seal([1, 2, 3]);

        client.SendRaw(packet, DeliveryMethod.ReliableOrdered);
        client.SendRaw(packet, DeliveryMethod.ReliableOrdered);
        PumpUntil(() => _serverEvents.Disconnected.Count == 1);

        Assert.Single(_serverEvents.Received);
        Assert.Equal(SecurityEventKind.ReliableReplayed, Assert.Single(_securityEvents.Events).Kind);
    }

    [Fact]
    public void Oversized_reliable_message_drops_the_peer()
    {
        StartServer(TestLimits.Create(maxReliableBytes: 1_024));
        var client = ConnectAndWait();

        client.SendReliable(new byte[1_025]);
        PumpUntil(() => _serverEvents.Disconnected.Count == 1);

        Assert.Empty(_serverEvents.Received);
        Assert.Equal(SecurityEventKind.MessageTooLarge, Assert.Single(_securityEvents.Events).Kind);
    }

    [Fact]
    public void Untagged_input_is_dropped_and_reported_but_the_peer_stays()
    {
        StartServer();
        var client = ConnectAndWait();

        client.SendRaw([4, 5], DeliveryMethod.Sequenced);
        PumpUntil(() => _securityEvents.Events.Count == 1);
        client.SendInput([6]);
        PumpUntil(() => _serverEvents.Received.Count == 1);

        Assert.Equal(SecurityEventKind.InputTagFailed, _securityEvents.Events[0].Kind);
        Assert.Equal(new byte[] { 6 }, _serverEvents.Received[0].Data);
        Assert.Empty(_serverEvents.Disconnected);
    }

    [Fact]
    public void Inputs_over_the_per_peer_rate_are_dropped_and_reported()
    {
        // The fake clock does not move, so the bucket never refills.
        StartServer(TestLimits.Create(messagesPerSecond: 1, messageBurst: 3));
        var client = ConnectAndWait();

        for (byte i = 1; i <= 5; i++)
        {
            client.SendInput([i]);
            client.Poll();
            Thread.Sleep(10); // sequenced delivery drops older packets that arrive late
        }
        PumpUntil(() => _serverEvents.Received.Count + _securityEvents.Count(SecurityEventKind.MessageRateLimited) == 5);

        Assert.Equal(3, _serverEvents.Received.Count);
        Assert.Equal(2, _securityEvents.Count(SecurityEventKind.MessageRateLimited));
        Assert.Empty(_serverEvents.Disconnected);
    }

    [Fact]
    public void Repeated_attempts_from_one_ip_are_refused_with_too_many_attempts()
    {
        StartServer(TestLimits.Create(attemptBurstPerIp: 2, attemptsPerIpPerMinute: 1));
        ConnectAndWait(accountId: 1);
        ConnectAndWait(accountId: 2);

        var third = Connect(IssueTicket(accountId: 3));
        PumpUntil(() => third.Rejected != null);

        Assert.Equal(ConnectRejectReason.TooManyAttempts, third.Rejected);
        Assert.Equal(SecurityEventKind.ConnectRateLimited, Assert.Single(_securityEvents.Events).Kind);
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
    public void Tampered_ticket_is_rejected_and_reported()
    {
        StartServer();
        var issued = IssueTicket();
        issued.Ticket[^1] ^= 0xFF;
        var client = Connect(issued);

        PumpUntil(() => client.Rejected != null);

        Assert.Equal(ConnectRejectReason.InvalidTicket, client.Rejected);
        var reported = Assert.Single(_securityEvents.Events);
        Assert.Equal(SecurityEventKind.ConnectRejected, reported.Kind);
        Assert.Equal(ConnectRejectReason.InvalidTicket, reported.RejectReason);
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
        StartServer(TestLimits.Create(maxPeers: 1));
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
        var client = ConnectAndWait();

        client.Disconnect();
        PumpUntil(() => _serverEvents.Disconnected.Count == 1);

        Assert.Equal(PeerDisconnectReason.ClosedByClient, _serverEvents.Disconnected[0].Reason);
    }

    [Fact]
    public void Packets_still_queued_from_a_peer_the_server_kicked_are_dropped_not_opened_with_its_closed_crypto()
    {
        // Found with the admin kick (WP18): a bot kept sending inputs, the kick disposed the session's keys, and LiteNetLib
        // then delivered the inputs it had already queued for that peer; the network thread threw and the process died.
        StartServer(TestLimits.Create(messagesPerSecond: 100_000, messageBurst: 100_000));
        // The client's packets reach the server 60 ms late (the bot's rtt100 link), so some arrive after the kick.
        using var relay = new DelayingRelay(_server!.LocalPort, TimeSpan.FromMilliseconds(60));
        var client = ConnectAndWait(port: relay.Port);
        var peerId = _serverEvents.Connected[0].PeerId;
        using var stop = new CancellationTokenSource();
        var sender = Task.Run(() =>
        {
            // The client keeps sending until it notices the kick, like a playing client.
            while (!stop.IsCancellationRequested)
            {
                client.SendInput([1]);
                Thread.Sleep(1);
            }
        });
        PumpUntil(() => _serverEvents.Received.Count > 5);

        _server!.Disconnect(peerId, ConnectRejectReason.Kicked);
        int receivedAtKick = -1;
        PumpUntil(() =>
        {
            if (_serverEvents.Disconnected.Count == 1 && receivedAtKick < 0)
                receivedAtKick = _serverEvents.Received.Count;
            return receivedAtKick >= 0;
        });
        PumpFor(TimeSpan.FromMilliseconds(300)); // inputs keep arriving for the closed peer: dropped, never opened
        stop.Cancel();
        sender.Wait(Timeout);

        Assert.Equal(PeerDisconnectReason.ClosedByServer, _serverEvents.Disconnected[0].Reason);
        Assert.Equal(receivedAtKick, _serverEvents.Received.Count); // nothing after the kick reached the game
    }

    [Fact]
    public void Sending_to_a_disconnected_peer_does_nothing()
    {
        StartServer();
        var client = ConnectAndWait();
        var peerId = _serverEvents.Connected[0].PeerId;
        client.Disconnect();
        PumpUntil(() => _serverEvents.Disconnected.Count == 1);

        _server!.Send(peerId, Channel.ReliableOrdered, [1]);
        PumpFor(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void The_unreliable_limit_is_the_peer_s_unfragmented_packet_size_and_0_for_a_peer_that_is_gone()
    {
        StartServer();
        ConnectAndWait();
        var peerId = _serverEvents.Connected[0].PeerId;

        int limit = _server!.MaxUnreliableBytes(peerId);

        // A new peer starts at LiteNetLib's initial MTU minus the sequenced header; NetSecurity.json's comment quotes 1020.
        Assert.Equal(1_020, LiteNetLibTransport.InitialUnreliableBytes);
        Assert.Equal(LiteNetLibTransport.InitialUnreliableBytes, limit);
        Assert.Equal(0, _server.MaxUnreliableBytes(peerId + 1000));
    }

    [Fact]
    public void A_table_limit_whose_tagged_input_does_not_fit_the_initial_datagram_refuses_to_start()
    {
        int fits = LiteNetLibTransport.InitialUnreliableBytes - SessionCrypto.InputOverhead;
        var validator = new TicketValidator(new TicketKeyRing().Add(1, Key), new InMemoryNonceCache(_clock), _clock, Realm, LifetimeMs);

        using (new LiteNetLibTransport(new ConnectionApprover(validator), _serverEvents, TestLimits.Create(maxUnreliableBytes: fits), _clock))
        {
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiteNetLibTransport(new ConnectionApprover(validator), _serverEvents,
            TestLimits.Create(maxUnreliableBytes: fits + 1), _clock));
    }

    [Fact]
    public void The_repository_s_net_limits_fit_the_transport()
    {
        var net = ServerTestEnvironment.LoadConfig().NetSecurity;

        Assert.InRange(net.MaxUnreliableMessageBytes + SessionCrypto.InputOverhead, 1, LiteNetLibTransport.InitialUnreliableBytes);
    }

    [Fact]
    public void An_unreliable_message_over_the_peer_limit_is_dropped_and_counted_never_thrown_and_the_peer_stays()
    {
        StartServer();
        var client = ConnectAndWait();
        var peerId = _serverEvents.Connected[0].PeerId;
        int limit = _server!.MaxUnreliableBytes(peerId);

        _server.Send(peerId, Channel.UnreliableSequenced, new byte[limit + 1]);
        _server.Send(peerId, Channel.UnreliableSequenced, new byte[limit]);
        PumpUntil(() => client.Received.Count == 1);
        PumpFor(TimeSpan.FromMilliseconds(50));

        Assert.Equal(1, _server.UnreliableTooLarge);
        Assert.Equal(new TooLargeSend(peerId, limit + 1, limit), _server.LastUnreliableTooLarge);
        Assert.Equal(limit, Assert.Single(client.Received).Data.Length);
        Assert.Equal(1, _server.ConnectedPeers);
        Assert.Empty(_serverEvents.Disconnected);
    }

    private sealed class RecordingListener : ITransportListener
    {
        public List<(int PeerId, SessionTicket Ticket)> Connected { get; } = [];
        public List<(int PeerId, PeerDisconnectReason Reason)> Disconnected { get; } = [];
        public List<(int PeerId, Channel Channel, byte[] Data)> Received { get; } = [];

        public void OnPeerConnected(int peerId, SessionTicket ticket) => Connected.Add((peerId, ticket));

        public void OnPeerDisconnected(int peerId, PeerDisconnectReason reason) => Disconnected.Add((peerId, reason));

        public void OnDataReceived(int peerId, Channel channel, ReadOnlySpan<byte> data) => Received.Add((peerId, channel, data.ToArray()));

        public void OnRoundTripTime(int peerId, int roundTripMs)
        {
        }
    }

    private sealed class RecordingSecurityEvents : ISecurityEventSink
    {
        public List<SecurityEvent> Events { get; } = [];

        public int Count(SecurityEventKind kind) => Events.Count(e => e.Kind == kind);

        public void Report(in SecurityEvent securityEvent) => Events.Add(securityEvent);
    }

    /// <summary>
    /// One client's UDP link to the server with its datagrams to the server delayed (the answers are not), so packets
    /// the client sent before it learned of a kick still arrive after the server closed the peer.
    /// </summary>
    private sealed class DelayingRelay : IDisposable
    {
        private readonly System.Net.Sockets.UdpClient _clientSide = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly System.Net.Sockets.UdpClient _serverSide = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly IPEndPoint _server;
        private readonly TimeSpan _delay;
        private readonly CancellationTokenSource _stop = new();
        private IPEndPoint? _client;

        public DelayingRelay(int serverPort, TimeSpan delay)
        {
            _server = new IPEndPoint(IPAddress.Loopback, serverPort);
            _delay = delay;
            _ = Task.Run(ToServerAsync);
            _ = Task.Run(ToClientAsync);
        }

        public int Port => ((IPEndPoint)_clientSide.Client.LocalEndPoint!).Port;

        private async Task ToServerAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var received = await _clientSide.ReceiveAsync(_stop.Token);
                _client = received.RemoteEndPoint;
                _ = Task.Delay(_delay, _stop.Token).ContinueWith(_ => _serverSide.Send(received.Buffer, _server),
                    TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }

        private async Task ToClientAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var received = await _serverSide.ReceiveAsync(_stop.Token);
                if (_client != null)
                    await _clientSide.SendAsync(received.Buffer, _client);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _clientSide.Dispose();
            _serverSide.Dispose();
        }
    }

    /// <summary>Plays the game client's side of R-2.5-02 with the same <see cref="SessionCrypto"/>.</summary>
    private sealed class TestClient : IDisposable
    {
        private readonly NetManager _manager;
        private readonly SessionCrypto _crypto;
        private NetPeer? _peer;

        public TestClient(int port, byte[] request, byte[] sessionKey)
        {
            _crypto = new SessionCrypto(sessionKey, SessionRole.Client, SystemChaCha20Poly1305.Create);
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
            events.NetworkReceiveEvent += (_, reader, _, method) => OnReceive(reader.GetRemainingBytes(), method);

            _manager = new NetManager(events) { AutoRecycle = true };
            _manager.Start();
            _manager.Connect(new IPEndPoint(IPAddress.Loopback, port), request);
        }

        public bool Connected { get; private set; }
        public ConnectRejectReason? Rejected { get; private set; }
        public ushort ServerVersion { get; private set; }
        public List<byte[]> RawReceived { get; } = [];
        public List<(DeliveryMethod Method, byte[] Data)> Received { get; } = [];

        public void Poll() => _manager.PollEvents();

        public byte[] Seal(byte[] data)
        {
            var packet = new byte[data.Length + SessionCrypto.ReliableOverhead];
            _crypto.SealReliable(data, packet);
            return packet;
        }

        public void SendReliable(byte[] data) => SendRaw(Seal(data), DeliveryMethod.ReliableOrdered);

        public void SendInput(byte[] data)
        {
            var packet = new byte[data.Length + SessionCrypto.InputOverhead];
            _crypto.TagInput(data, packet);
            SendRaw(packet, DeliveryMethod.Sequenced);
        }

        public void SendRaw(byte[] data, DeliveryMethod method) => _peer!.Send(data, method);

        public void Disconnect() => _peer!.Disconnect();

        public void Dispose()
        {
            _manager.Stop();
            _crypto.Dispose();
        }

        private void OnReceive(byte[] data, DeliveryMethod method)
        {
            RawReceived.Add(data);
            if (method != DeliveryMethod.ReliableOrdered)
            {
                Received.Add((method, data));
                return;
            }
            var plaintext = new byte[data.Length];
            Assert.Equal(OpenResult.Ok, _crypto.OpenReliable(data, plaintext, out var length));
            Received.Add((method, plaintext[..length]));
        }
    }
}
