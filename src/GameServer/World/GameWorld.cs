using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using GameServer.World.Map;
using Shared.Simulation;
using SimMoveKeys = Shared.Simulation.MoveKeys;

namespace GameServer.World;

/// <summary>
/// One world: the main map or an instance (spec 2.3 <c>World</c>). Owns its entities, grid and interest, and runs the
/// tick phases of spec 1.6 in fixed order, with entities in id order inside each phase (architecture rule 6).
/// <para>
/// Threading: <see cref="Tick"/> runs on the world's simulation thread only (see <see cref="WorldThread"/>).
/// Other threads talk to it through its <see cref="WorldInbox"/> and the per-session <see cref="InputQueue"/>s.
/// </para>
/// <para>
/// Movement (phase 2) runs the shared MovementSim with collision against the exported <see cref="WorldMap"/> grid, the
/// same code client prediction runs, then sets height and region from the grid and runs the spec 2.2 speed check.
/// </para>
/// </summary>
public sealed class GameWorld
{
    private readonly WorldSettings _settings;
    private readonly WorldMap _map;
    private readonly WorldMarker _spawn;
    private readonly long _allowedRawPerTick;
    private readonly IMovementViolationSink _violations;
    private readonly WorldInbox _inbox;
    private readonly List<Entity> _entities = new();
    private readonly Dictionary<int, Player> _playersByPeer = new();
    private readonly SpatialGrid _grid;
    private readonly InterestManager _interest;
    private readonly SnapshotSender _snapshots;
    private readonly ITickHook _eventDispatch;
    private readonly ITickHook _telemetryFlush;
    private readonly EventBus? _events;
    private uint _nextEntityId = 1;

    /// <param name="events">
    /// The world's bus; the world publishes its session events there (spec 8.1). <paramref name="eventDispatch"/> is what
    /// delivers them in phase 11.
    /// </param>
    /// <param name="map">The loaded world; it must hold the <see cref="WorldSettings.PlayerSpawnMarkerId"/> spawn.</param>
    /// <param name="violations">Where speed-check violations are reported (telemetry in the server).</param>
    public GameWorld(
        WorldSettings settings,
        WorldMap map,
        WorldInbox inbox,
        ITransport transport,
        IVisibilityQuery? visibility = null,
        ITickHook? eventDispatch = null,
        ITickHook? telemetryFlush = null,
        EventBus? events = null,
        IMovementViolationSink? violations = null)
    {
        var spawnError = WorldLoader.CheckPlayerSpawn(map, settings.PlayerSpawnMarkerId);
        if (spawnError != null)
            throw new ArgumentException(spawnError.Message, nameof(map));
        map.TryGetMarker(settings.PlayerSpawnMarkerId, out _spawn);
        _map = map;
        // TODO(D5): maxSpeed from the movement-speed stat plus the mount bonus (R-2.2-03); M1 has one speed and no mounts.
        _allowedRawPerTick = SpeedCheck.AllowedRawPerTick(settings.MoveSpeedCmPerSec, settings.TickRateHz,
            settings.SpeedTolerancePercent);
        _violations = violations ?? NoMovementViolationSink.Instance;
        _events = events;
        _settings = settings;
        _inbox = inbox;
        _grid = new SpatialGrid(settings.GridCellCm);
        _interest = new InterestManager(_grid, settings.InterestRadiusCm, visibility ?? EveryoneVisible.Instance);
        _snapshots = new SnapshotSender(transport, settings.MaxSnapshotBytes);
        _eventDispatch = eventDispatch ?? NoTickHook.Instance;
        _telemetryFlush = telemetryFlush ?? NoTickHook.Instance;
    }

    public WorldSettings Settings => _settings;

    /// <summary>Number of the next tick to run; snapshots carry the tick they were built in.</summary>
    public uint ServerTick { get; private set; }

    /// <summary>Every entity, in id order. Simulation thread only.</summary>
    public IReadOnlyList<Entity> Entities => _entities;

    public SpatialGrid Grid => _grid;

    /// <summary>The world's collision grid, regions and markers. Region flags for other systems: <see cref="WorldMap.TryGetFlags"/>.</summary>
    public WorldMap Map => _map;

    public long SnapshotsTruncated => _snapshots.Truncated;

    /// <summary>Spec 2.2 speed-check violations since the world started (each one is also reported to the sink).</summary>
    public long SpeedViolations { get; private set; }

    /// <summary>Test hook: called at the start of every phase.</summary>
    internal Action<TickPhase>? PhaseStarted { get; set; }

    public bool TryGetPlayer(int peerId, out Player player) => _playersByPeer.TryGetValue(peerId, out player!);

    /// <summary>Runs one tick: phases 1 to 12 of spec 1.6.</summary>
    public void Tick()
    {
        uint tick = ServerTick;
        // Interest only feeds snapshots so far, so phases 9 and 10 run on snapshot ticks: enter/leave deltas then
        // always reach the snapshot that carries them.
        bool snapshotTick = tick % (uint)_settings.SnapshotIntervalTicks == 0;

        Begin(TickPhase.ApplyInputs);
        ApplyCommands();
        ApplyInputs();

        Begin(TickPhase.Movement);
        MoveEntities(tick);

        Begin(TickPhase.ActionStateMachines);
        // TODO(D2): ActionMachine; while an action is active MovementSim ignores move keys and keeps the facing (R-2.2-02).

        Begin(TickPhase.ProjectileMovement);
        // TODO(D3): ProjectileSystem.

        Begin(TickPhase.HitResolution);
        // TODO(D3, D4): HitResolver with dodge, parry, damage and poise.

        Begin(TickPhase.StatusAndGauges);
        // TODO(D6): status effects, build-ups, resource meters.

        Begin(TickPhase.AiAndSpawns);
        // TODO(F): monster AI and SpawnController.

        Begin(TickPhase.DeathAndRespawn);
        // TODO(D, F): death and respawn handling.

        Begin(TickPhase.VisibilityAndInterest);
        if (snapshotTick)
            UpdateInterest();

        Begin(TickPhase.SnapshotsAndSends);
        if (snapshotTick)
            SendSnapshots(tick);

        Begin(TickPhase.EventDispatch);
        _eventDispatch.Run(tick);

        Begin(TickPhase.TelemetryFlush);
        // Only enqueues and signals the writer thread: telemetry never blocks the tick (architecture rule 10).
        _telemetryFlush.Run(tick);

        ServerTick = tick + 1;
    }

    /// <summary>
    /// Ends every session with <see cref="SessionEndReason.ServerShutdown"/> (R-2.1-09) and delivers those events with a
    /// last phase-11 dispatch. Call once, after the simulation thread has stopped, from the thread that stopped it.
    /// TODO(H1): the shutdown saves go with this.
    /// </summary>
    public void Shutdown()
    {
        var peers = _playersByPeer.Values.OrderBy(p => p.Id).Select(p => p.PeerId).ToList();
        foreach (var peerId in peers)
            Leave(peerId, SessionEndReason.ServerShutdown);
        _eventDispatch.Run(ServerTick);
    }

    private void Begin(TickPhase phase) => PhaseStarted?.Invoke(phase);

    private void ApplyCommands()
    {
        while (_inbox.TryTake(out var command))
        {
            switch (command.Kind)
            {
                case WorldCommandKind.Join:
                    Join(command.PeerId, command.AccountId, command.Inputs!);
                    break;
                case WorldCommandKind.Leave:
                    // TODO(B4): linger (StartingValuesM1.LingerOutOfCombatMs / LingerInCombatMs) instead of removing at once.
                    Leave(command.PeerId, SessionEndReason.Disconnected);
                    break;
            }
        }
    }

    private void Join(int peerId, long accountId, InputQueue inputs)
    {
        // A peer id is only reused after its Leave, which was posted first; this guards against a lost Leave.
        Leave(peerId, SessionEndReason.Disconnected);

        // TODO(H1): load the character's saved world and position.
        var spawn = SpawnPoint();
        int height = _map.HeightCmAt(spawn.Position, _map.Grid.BaseHeightCm);
        var player = new Player(_nextEntityId++, peerId, accountId, inputs, spawn, height);
        player.RegionIndex = _map.Grid.RegionIndexAt(spawn.Position);
        player.SpeedCheckedPosition = spawn.Position;
        _entities.Add(player); // ids only grow, so the list stays in id order
        _playersByPeer.Add(peerId, player);
        _grid.Add(player);
        // TODO(H1): actor = the loaded character's id. The region id is the grid index (per world). TODO(B4): Reattached
        // when it rejoins a lingering entity (R-2.1-08).
        _events?.Publish(new SessionStarted(accountId, player.Id, Reattached: false), regionId: player.RegionIndex);
    }

    /// <summary>Where a new player appears: the exported <c>ServerLoop.PlayerSpawnMarkerId</c> marker (backlog C2).</summary>
    private MovementState SpawnPoint() => new(_spawn.Position, _spawn.Facing);

    private void Leave(int peerId, SessionEndReason reason)
    {
        if (!_playersByPeer.Remove(peerId, out var player))
            return;
        _grid.Remove(player);
        _entities.Remove(player);
        _events?.Publish(new SessionEnded(player.AccountId, player.Id, reason), regionId: player.RegionIndex);
    }

    /// <summary>Phase 1: one queued input frame per player per tick, the way the client predicted it (spec 2.2).</summary>
    private void ApplyInputs()
    {
        foreach (var entity in _entities)
        {
            if (entity is not Player player)
                continue;
            if (!player.Inputs.TryDequeue(out var frame))
            {
                // Nothing arrived: the player stands still this tick. Frames are never invented, so every frame the
                // client predicted is applied exactly once or (dropped by loss or a full queue) not at all.
                player.HasInputThisTick = false;
                continue;
            }
            // The wire and the simulation use the same key bits (W 1, A 2, S 4, D 8); facing is already the binary
            // angle decoded by Quantize.AngleFromWire.
            player.CurrentInput = new MovementInput((SimMoveKeys)(byte)frame.Keys, frame.Facing);
            player.HasInputThisTick = true;
            player.HasInputAck = true;
            player.LastInputSequence = frame.Sequence;
        }
    }

    /// <summary>
    /// Phase 2: the same MovementSim and grid collision the client predicts with (spec 2.2, backlog C3), then height and
    /// region from the grid, then the speed check for every player.
    /// </summary>
    private void MoveEntities(uint tick)
    {
        foreach (var entity in _entities)
        {
            if (entity is not Player player)
                continue;
            if (player.HasInputThisTick)
            {
                var before = player.Movement.Position;
                // TODO(D2): actionActive from ActionMachine. TODO(D5): speed from the movement-speed stat (R-2.2-03).
                player.Movement = MovementSim.Step(player.Movement, player.CurrentInput, actionActive: false,
                    _settings.MoveSpeedCmPerSec, _settings.TickRateHz, _map.Grid);
                if (player.Movement.Position != before)
                {
                    player.HeightCm = _map.HeightCmAt(player.Movement.Position, player.HeightCm);
                    // TODO(open): EnteredRegion / LeftRegion (spec 2.3, 8.1) when the index changes; payload not defined yet.
                    player.RegionIndex = _map.Grid.RegionIndexAt(player.Movement.Position);
                    _grid.Update(player);
                }
            }
            CheckSpeed(player, tick);
        }
    }

    /// <summary>
    /// Spec 2.2: the distance since the last check must stay within maxSpeed per tick plus the tolerance. It measures from
    /// where the previous phase 2 left the entity, so a move made outside MovementSim (a bug, or later knockback without
    /// its own rule) is caught too. Violations are counted and reported, not corrected: the spec only asks to count them.
    /// </summary>
    private void CheckSpeed(Player player, uint tick)
    {
        var position = player.Movement.Position;
        if (SpeedCheck.Exceeds(player.SpeedCheckedPosition, position, _allowedRawPerTick))
        {
            SpeedViolations++;
            _violations.SpeedViolation(tick, player.Id, player.AccountId,
                SpeedCheck.DistanceCm(player.SpeedCheckedPosition, position), _allowedRawPerTick >> FixedPoint.FractionBits);
        }
        player.SpeedCheckedPosition = position;
    }

    private void UpdateInterest()
    {
        foreach (var entity in _entities)
        {
            if (entity is Player player)
                _interest.Update(player);
        }
    }

    private void SendSnapshots(uint tick)
    {
        foreach (var entity in _entities)
        {
            if (entity is Player player)
                _snapshots.Send(player, tick);
        }
    }
}
