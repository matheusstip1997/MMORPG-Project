using System.Diagnostics;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Net;
using GameServer.Host;
using GameServer.Net.Tickets;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// Backlog D1 end to end: the Unity client's prediction core (unity/Assets/Game/{Prediction,Entities}/Core, compiled
/// into this project) and its <see cref="ClientConnection"/> against the real server loop over localhost, at the
/// configured 20 Hz. The client reads the tables with the portable reader the Unity client uses.
/// </summary>
public sealed class ClientPredictionEndToEndTests : IDisposable
{
    private const int Realm = 1;
    private static readonly byte[] Secret = Enumerable.Repeat((byte)0x5A, 32).ToArray();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ConfigSet _tables = LoadTablesLikeTheClient();
    private readonly TempDirectory _telemetry = new();
    private readonly Bootstrap _server;
    private readonly PredictingClient _client;

    public ClientPredictionEndToEndTests()
    {
        var settings = new ServerSettings(Secret, TicketKeyId: 1, Realm, Port: 0, DataDirectory: "unused", _telemetry.Path);
        var clock = new SystemClock();
        _server = new Bootstrap(ServerConfig.From(_tables), settings, clock, new StopwatchTickClock());
        _server.Start();

        var netSecurity = _tables.Single<NetSecurity>();
        var issued = new DevTicketIssuer(1, Secret, clock, netSecurity.TicketLifetimeMs).Issue(accountId: 77, Realm, slot: 0);
        _client = new PredictingClient(PredictionSettings.From(_tables),
            new ClientNetLimits(netSecurity.MaxReliableMessageBytes, netSecurity.MaxUnreliableMessageBytes));
        _client.Connection.Connect("127.0.0.1", _server.Transport.LocalPort, issued.Ticket, issued.SessionKey);
    }

    public void Dispose()
    {
        _client.Connection.Dispose();
        _server.Dispose();
        _telemetry.Dispose();
    }

    [Fact]
    public void Predicted_movement_matches_the_server_once_every_input_is_acknowledged()
    {
        PumpUntil(() => _client.Predictor.HasState);
        var start = _client.Predictor.State.Position;

        Run(TimeSpan.FromSeconds(1), MoveKeys.W, facing: 0);
        Run(TimeSpan.FromSeconds(1), MoveKeys.D | MoveKeys.S, facing: 24_000);
        PumpUntil(() => _client.LastHeader.HasInputAck && _client.LastHeader.LastInputSequence == _client.Predictor.LastSequence
                        && _client.Predictor.PendingCount == 0);

        // The server's own state, whole cm, from the newest snapshot.
        var server = SnapshotAxes.GroundState(_client.LastOwn).Position;
        var error = _client.Predictor.State.Position - server;
        long toleranceRaw = (long)_tables.Single<ClientPrediction>().CorrectionToleranceCm * FixedPoint.RawPerCm;
        Assert.True(error.LengthSquared <= toleranceRaw * toleranceRaw, $"prediction {_client.Predictor.State} vs server {server}");
        Assert.NotEqual(start, server);
        // A frame lost to a timer burst past the server's input queue costs one correction; steady play costs none.
        Assert.InRange(_client.Predictor.Corrections, 0, 3);
        Assert.Equal(0, _client.UnknownAcks);
    }

    private void Run(TimeSpan duration, MoveKeys keys, ushort facing)
    {
        var watch = Stopwatch.StartNew();
        double lastMs = 0;
        while (watch.Elapsed < duration)
        {
            Poll();
            double nowMs = watch.Elapsed.TotalMilliseconds;
            int ticks = _client.Ticks.Advance(nowMs - lastMs);
            lastMs = nowMs;
            for (int i = 0; i < ticks; i++)
                _client.SendTick(keys, facing);
            Thread.Sleep(1);
        }
    }

    private void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(watch.Elapsed < Timeout, "Timed out waiting for the server.");
            Poll();
            Thread.Sleep(5);
        }
    }

    private void Poll()
    {
        Assert.Null(_server.WorldThread.Failure);
        _client.Connection.PollEvents();
    }

    private static ConfigSet LoadTablesLikeTheClient()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var tables = Path.Combine(directory.FullName, "data", "tables");
        var errors = new List<ValidationError>();
        var set = ConfigTableReader.LoadAndValidate(ConfigTables.All, name =>
        {
            var path = Path.Combine(tables, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, errors);
        Assert.Empty(errors);
        return set;
    }

    /// <summary>What the Unity dev client does per tick and per snapshot, without the rendering.</summary>
    private sealed class PredictingClient : IClientConnectionListener
    {
        private readonly InputPacketBuilder _packets;
        private readonly byte[] _buffer;
        private readonly EntityRegistry _registry;

        public PredictingClient(PredictionSettings settings, ClientNetLimits limits)
        {
            Predictor = new LocalPlayerPredictor(settings);
            Ticks = new TickClock(settings);
            _packets = new InputPacketBuilder(settings.InputRedundancy);
            _buffer = new byte[_packets.MaxPacketSize];
            _registry = new EntityRegistry(settings);
            Connection = new ClientConnection(this, limits);
        }

        public ClientConnection Connection { get; }
        public LocalPlayerPredictor Predictor { get; }
        public TickClock Ticks { get; }
        public SnapshotHeader LastHeader { get; private set; }
        public EntitySnapshot LastOwn { get; private set; }
        public int UnknownAcks { get; private set; }

        public void SendTick(MoveKeys keys, ushort facing)
        {
            if (!Predictor.HasState)
                return;
            var frame = Predictor.Predict(keys, facing);
            int size = _packets.Write(frame, _buffer);
            Assert.True(Connection.SendInput(_buffer.AsSpan(0, size)));
        }

        public void OnConnected()
        {
        }

        public void OnDisconnected(ClientDisconnectReason reason, ConnectRejectReason rejectReason, ushort serverProtocolVersion) =>
            Assert.Fail($"disconnected: {reason} {rejectReason}");

        public void OnReliableReceived(ReadOnlySpan<byte> message)
        {
        }

        public void OnUnreliableReceived(ReadOnlySpan<byte> message)
        {
            Assert.True(_registry.ApplySnapshot(message, out var header, out var own, out bool hasOwn));
            if (!hasOwn)
                return;
            var result = Predictor.Reconcile(header, SnapshotAxes.GroundState(own));
            if (result.Outcome == ReconcileOutcome.UnknownAck)
                UnknownAcks++;
            if (result.Outcome != ReconcileOutcome.Stale)
            {
                LastHeader = header;
                LastOwn = own;
            }
        }
    }
}
