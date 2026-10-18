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
using Shared.World;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// Backlog D1 end to end: the Unity client's prediction core (unity/Assets/Game/{Prediction,Entities}/Core, compiled
/// into this project) and its <see cref="ClientConnection"/> against the real server loop over localhost, at the
/// configured 20 Hz. The client reads the tables with the portable reader the Unity client uses. The server is the full
/// one (sessions, PostgreSQL, Redis, the M1 world with collision); the character starts on the spawn marker. The client
/// predicts with the same world grid, loaded and verified with the shared <see cref="WorldPackage"/> like the Unity
/// client, so walls cost no corrections.
/// </summary>
public sealed class ClientPredictionEndToEndTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ConfigSet _tables = LoadTablesLikeTheClient();
    private ServerTestEnvironment _env = null!;
    private Bootstrap _server = null!;
    private PredictingClient? _client;

    public async Task InitializeAsync()
    {
        var config = ServerConfig.From(_tables);
        _env = await ServerTestEnvironment.CreateAsync();
        _server = _env.StartServer(config);
        await _env.CreateCharacterAsync(accountId: 77);

        var netSecurity = _tables.Single<NetSecurity>();
        var issued = _env.Ticket(config, accountId: 77);
        _client = new PredictingClient(PredictionSettings.From(_tables), LoadWorldLikeTheClient(),
            new ClientNetLimits(netSecurity.MaxReliableMessageBytes, netSecurity.MaxUnreliableMessageBytes));
        _client!.Connection.Connect("127.0.0.1", _server.Transport.LocalPort, issued.Ticket, issued.SessionKey);
    }

    public async Task DisposeAsync()
    {
        _client?.Connection.Dispose();
        await _env.DisposeAsync();
    }

    [Fact]
    public void Predicted_movement_matches_the_server_once_every_input_is_acknowledged()
    {
        PumpUntil(() => _client!.Predictor.HasState);
        var start = _client!.Predictor.State.Position;

        Run(TimeSpan.FromSeconds(1), MoveKeys.W, facing: 0);
        Run(TimeSpan.FromSeconds(1), MoveKeys.D | MoveKeys.S, facing: 24_000);
        PumpUntil(() => _client!.LastHeader.HasInputAck && _client!.LastHeader.LastInputSequence == _client!.Predictor.LastSequence
                        && _client!.Predictor.PendingCount == 0);

        // The server's own state, whole cm, from the newest snapshot.
        var server = SnapshotAxes.GroundState(_client!.LastOwn).Position;
        var error = _client!.Predictor.State.Position - server;
        long toleranceRaw = (long)_tables.Single<ClientPrediction>().CorrectionToleranceCm * FixedPoint.RawPerCm;
        Assert.True(error.LengthSquared <= toleranceRaw * toleranceRaw, $"prediction {_client!.Predictor.State} vs server {server}");
        Assert.NotEqual(start, server);
        // A frame lost to a timer burst past the server's input queue costs one correction; steady play costs none.
        Assert.InRange(_client!.Predictor.Corrections, 0, 3);
        Assert.Equal(0, _client!.UnknownAcks);
    }

    [Fact]
    public void Walking_into_the_city_wall_stops_on_both_sides_without_corrections()
    {
        PumpUntil(() => _client!.Predictor.HasState);
        Assert.Equal(FixVec2.FromCm(-3_000, 0), _client!.Predictor.State.Position); // marker m1.spawn.city

        // North out of the gate's row, then east into the city wall (blockers.json: x -1050..-950, y 200..3000, grown
        // by the 40 cm agent radius), pushing for about a second, then sliding north along it.
        Run(TimeSpan.FromSeconds(1), MoveKeys.W, facing: 0);
        Run(TimeSpan.FromSeconds(5.5), MoveKeys.D, facing: 16_384);
        Run(TimeSpan.FromSeconds(1), MoveKeys.D | MoveKeys.W, facing: 8_192);
        PumpUntil(() => _client!.LastHeader.HasInputAck && _client!.LastHeader.LastInputSequence == _client!.Predictor.LastSequence
                        && _client!.Predictor.PendingCount == 0);

        var server = SnapshotAxes.GroundState(_client!.LastOwn).Position;
        int serverXCm = FixedPoint.ToCmRound(server.X);
        Assert.InRange(serverXCm, -1_100, -1_080); // stopped at the grown wall, not in or past it
        Assert.True(FixedPoint.ToCmRound(server.Y) > 600, "the slide kept the free axis moving");
        var error = _client!.Predictor.State.Position - server;
        long toleranceRaw = (long)_tables.Single<ClientPrediction>().CorrectionToleranceCm * FixedPoint.RawPerCm;
        Assert.True(error.LengthSquared <= toleranceRaw * toleranceRaw, $"prediction {_client!.Predictor.State} vs server {server}");
        Assert.Equal(SnapshotAxes.HeightCm(_client!.LastOwn), _client!.Predictor.HeightCm);
        // Predicting without the grid corrects on every snapshot while pushing into the wall; only a frame lost to a
        // timer burst may still cost one.
        Assert.InRange(_client!.Predictor.Corrections, 0, 3);
        Assert.Equal(0, _client!.UnknownAcks);
    }

    private void Run(TimeSpan duration, MoveKeys keys, ushort facing)
    {
        var watch = Stopwatch.StartNew();
        double lastMs = 0;
        while (watch.Elapsed < duration)
        {
            Poll();
            double nowMs = watch.Elapsed.TotalMilliseconds;
            int ticks = _client!.Ticks.Advance(nowMs - lastMs);
            lastMs = nowMs;
            for (int i = 0; i < ticks; i++)
                _client!.SendTick(keys, facing);
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
        _client!.Connection.PollEvents();
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

    /// <summary>What ClientWorld does in the Unity editor: the repository's data/world/m1, verified by the shared code.</summary>
    private static WorldGrid LoadWorldLikeTheClient()
    {
        var directory = RepositoryPaths.M1World();
        Assert.True(WorldPackage.TryLoadGrid(name =>
        {
            var path = Path.Combine(directory, name);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }, out var grid, out var error), error);
        return grid!;
    }

    /// <summary>What the Unity dev client does per tick and per snapshot, without the rendering.</summary>
    private sealed class PredictingClient : IClientConnectionListener
    {
        private readonly InputPacketBuilder _packets;
        private readonly byte[] _buffer;
        private readonly EntityRegistry _registry;

        public PredictingClient(PredictionSettings settings, WorldGrid grid, ClientNetLimits limits)
        {
            Predictor = new LocalPlayerPredictor(settings, grid);
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
