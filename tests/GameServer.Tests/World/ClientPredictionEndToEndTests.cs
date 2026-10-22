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
using Shared.Simulation.Actions;
using Shared.World;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// Backlog D1 end to end: the Unity client's prediction core (unity/Assets/Game/{Prediction,Entities}/Core, compiled
/// into this project) and its <see cref="ClientConnection"/> against the real server loop over localhost, at the
/// configured 20 Hz. The client reads the tables with the portable reader the Unity client uses. The server is the full
/// one (sessions, PostgreSQL, Redis, the M1 world with collision); the character starts on the spawn marker. The client
/// predicts with the same world grid, loaded and verified with the shared <see cref="WorldPackage"/> like the Unity
/// client, so walls cost no corrections. Actions (backlog D2) are predicted with the same tables and replayed with movement.
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
        var grid = LoadWorldLikeTheClient();
        _client = new PredictingClient(PredictionSettings.From(_tables), grid, ActionCatalog.From(_tables),
            ProjectileTracker.From(_tables, _tables.Single<StartingValuesM1>().TickRateHz, grid),
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
    public void The_server_measures_how_far_behind_the_client_renders_from_the_view_ticks_it_sends()
    {
        PumpUntil(() => _client!.Predictor.HasState);
        Run(TimeSpan.FromSeconds(1), MoveKeys.None, facing: 0);

        // At least the 2 ticks the client stays behind; the round trip on localhost adds little, the cap bounds the rest.
        var player = Assert.Single(_server.World.Entities.OfType<GameServer.Entities.Player>());
        Assert.InRange(player.ViewLagTicks, 2, _server.World.Combat!.LagCompensationCapTicks);
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

    [Fact]
    public void Predicted_actions_match_the_server_and_refusals_arrive_with_their_reason()
    {
        PumpUntil(() => _client!.Predictor.HasState);
        var bindings = _tables.Single<ActionBindings>();
        byte emptySlot = (byte)(bindings.SkillSlots.Length + 1);
        byte dashSlot = (byte)(Array.IndexOf(bindings.SkillSlots, "skill.charge") + 1);
        Assert.True(dashSlot > 0, "the placeholder dash skill is bound");

        Run(TimeSpan.FromSeconds(0.5), MoveKeys.W, facing: 0);
        _client!.PressNext(InputActions.Attack, 0);
        Run(TimeSpan.FromSeconds(1.5), MoveKeys.W, facing: 0);    // the attack stops the walk, then it resumes
        _client!.PressNext(InputActions.None, dashSlot);
        Run(TimeSpan.FromSeconds(1.5), MoveKeys.None, facing: 0); // dash north
        _client!.PressNext(InputActions.None, emptySlot);
        Run(TimeSpan.FromSeconds(0.5), MoveKeys.S, facing: 32_768);
        PumpUntil(() => _client!.LastHeader.HasInputAck && _client!.LastHeader.LastInputSequence == _client!.Predictor.LastSequence
                        && _client!.Predictor.PendingCount == 0 && _client!.Refusals.Count > 0);

        var server = SnapshotAxes.GroundState(_client!.LastOwn).Position;
        var error = _client!.Predictor.State.Position - server;
        long toleranceRaw = (long)_tables.Single<ClientPrediction>().CorrectionToleranceCm * FixedPoint.RawPerCm;
        Assert.True(error.LengthSquared <= toleranceRaw * toleranceRaw, $"prediction {_client!.Predictor.State} vs server {server}");
        Assert.Equal(ActionWireState.From(_client!.LastOwn), ActionMachine.ToWire(_client!.Predictor.Actions));
        Assert.Equal([(emptySlot, ActionReason.SkillNotLearned)], _client!.Refusals);
        Assert.Contains(_client!.ServerStates, s => s == (byte)ActionState.Active); // the server ran the actions
        Assert.InRange(_client!.Predictor.Corrections, 0, 3); // as for movement: only a frame lost to a timer burst
    }

    [Fact]
    public void Arrow_shot_reaches_the_client_and_stops_at_the_wall()
    {
        // Backlog D3, spec 3.2: from the city spawn (-3000, 0) facing south, the blocker at x -3200..-2800, y -1700..-700
        // (grown by the 40 cm agent radius) is in range of the 20 m arrow.
        PumpUntil(() => _client!.Predictor.HasState);
        Assert.Equal(FixVec2.FromCm(-3_000, 0), _client!.Predictor.State.Position);
        var bindings = _tables.Single<ActionBindings>();
        byte arrowSlot = (byte)(Array.IndexOf(bindings.SkillSlots, "skill.arrowShot") + 1);
        Assert.True(arrowSlot > 0, "the placeholder Arrow Shot is bound");

        _client!.PressNext(InputActions.None, arrowSlot);
        Run(TimeSpan.FromSeconds(1.5), MoveKeys.None, facing: 32_768);
        PumpUntil(() => _client!.SpawnedProjectiles.Count > 0 && _client!.SpawnedProjectiles[0].DespawnTick != null);

        // The spawn: the arrow of ProjectileDef, from the shooter, along the locked facing.
        var arrow = Assert.Single(_client!.SpawnedProjectiles);
        Assert.True(_tables.TryGet(typeof(ProjectileDef), out var defs));
        Assert.Equal(Assert.Single(defs.Rows.Cast<ProjectileDef>(), d => d.Id == "arrow").NetId, arrow.Def.NetId);
        Assert.Equal(FixVec2.FromCm(-3_000, 0), arrow.Path.Origin);
        Assert.Equal((ushort)32_768, arrow.Path.Facing);

        // The despawn: stopped by the wall, on the tick the shared path (cast on the client's own grid) reaches it.
        Assert.Equal(ProjectileDespawnReason.World, arrow.Reason);
        Assert.True(arrow.Path.StoppedByWorld);
        Assert.Equal(arrow.Path.LastTick, arrow.DespawnTick);
        var end = arrow.Path.End;
        Assert.Equal(-3_000, FixedPoint.ToCmRound(end.X));
        Assert.InRange(FixedPoint.ToCmRound(end.Y), -700, -600); // in front of the grown wall, not in it

        _client!.Projectiles.Update(arrow.EndTick);
        Assert.Empty(_client!.Projectiles.Projectiles);
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

        private InputActions _nextActions;
        private byte _nextSlot;

        public PredictingClient(PredictionSettings settings, WorldGrid grid, ActionCatalog actions, ProjectileTracker projectiles,
            ClientNetLimits limits)
        {
            Predictor = new LocalPlayerPredictor(settings, grid, actions);
            Ticks = new TickClock(settings);
            _packets = new InputPacketBuilder(settings.InputRedundancy);
            _buffer = new byte[_packets.MaxPacketSize];
            _registry = new EntityRegistry(settings);
            Projectiles = projectiles;
            projectiles.Spawned += projectile => SpawnedProjectiles.Add(projectile);
            Connection = new ClientConnection(this, limits);
        }

        public ClientConnection Connection { get; }
        public LocalPlayerPredictor Predictor { get; }
        public TickClock Ticks { get; }
        public SnapshotHeader LastHeader { get; private set; }
        public EntitySnapshot LastOwn { get; private set; }
        public int UnknownAcks { get; private set; }
        public List<(byte Slot, ActionReason Reason)> Refusals { get; } = [];
        public HashSet<byte> ServerStates { get; } = [];
        public ProjectileTracker Projectiles { get; }
        public List<ClientProjectile> SpawnedProjectiles { get; } = [];

        /// <summary>The buttons and slot of the next frame only: a press and its release.</summary>
        public void PressNext(InputActions actions, byte slot)
        {
            _nextActions = actions;
            _nextSlot = slot;
        }

        public void SendTick(MoveKeys keys, ushort facing)
        {
            if (!Predictor.HasState)
                return;
            // R-3.1-03: the tick it renders others at; this client has no interpolation clock, so 2 ticks behind its newest snapshot.
            uint viewTick = LastHeader.ServerTick > 2 ? LastHeader.ServerTick - 2 : 0;
            var frame = Predictor.Predict(keys, facing, _nextActions, _nextSlot, viewTick);
            Assert.Equal(viewTick, frame.ViewTick);
            _nextActions = InputActions.None;
            _nextSlot = 0;
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
            Assert.True(ActionResult.TryRead(message, out _, out _, out byte slot, out ActionReason reason));
            Refusals.Add((slot, reason));
        }

        public void OnUnreliableReceived(ReadOnlySpan<byte> message)
        {
            // What PredictionDevClient does: projectile messages to the tracker, hits aside, snapshots to the registry.
            HotMessage.TryPeekId(message, out var id);
            if (id == HotMessageId.ProjectileSpawn || id == HotMessageId.ProjectileDespawn)
            {
                Assert.True(Projectiles.Apply(message));
                return;
            }
            if (id == HotMessageId.Hit)
                return; // damage numbers only (backlog I)
            Assert.True(_registry.ApplySnapshot(message, out var header, out var own, out bool hasOwn));
            if (!hasOwn)
                return;
            ServerStates.Add(own.State);
            var result = Predictor.Reconcile(header, SnapshotAxes.GroundState(own), ActionWireState.From(own));
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
