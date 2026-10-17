using GameServer.Entities;
using GameServer.Events;
using GameServer.Net;
using GameServer.Sessions;
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
/// Not here yet (backlog C3): collision, world bounds, and the speed check. The server already runs MovementSim
/// itself and applies at most one client frame per tick, so a client cannot move faster or teleport by what it sends.
/// </para>
/// </summary>
public sealed class GameWorld
{
    private readonly WorldSettings _settings;
    private readonly WorldInbox _inbox;
    private readonly List<Entity> _entities = new();
    private readonly Dictionary<int, Player> _playersByPeer = new();
    private readonly SpatialGrid _grid;
    private readonly InterestManager _interest;
    private readonly SnapshotSender _snapshots;
    private readonly ITickHook _eventDispatch;
    private readonly ITickHook _telemetryFlush;
    private readonly EventBus? _events;
    /// <summary>Inputs of unbound (lingering) players: nobody ever enqueues here, so they stand still.</summary>
    private readonly InputQueue _detachedInputs = new(1);
    private SessionManager? _sessions;
    private uint _nextEntityId = 1;

    /// <param name="events">
    /// The world's bus; the world publishes its session events there (spec 8.1). <paramref name="eventDispatch"/> is what
    /// delivers them in phase 11.
    /// </param>
    public GameWorld(
        WorldSettings settings,
        WorldInbox inbox,
        ITransport transport,
        IVisibilityQuery? visibility = null,
        ITickHook? eventDispatch = null,
        ITickHook? telemetryFlush = null,
        EventBus? events = null)
    {
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

    public long SnapshotsTruncated => _snapshots.Truncated;

    /// <summary>Test hook: called at the start of every phase.</summary>
    internal Action<TickPhase>? PhaseStarted { get; set; }

    /// <summary>The world's bus, if any; the <see cref="SessionManager"/> publishes the session events there.</summary>
    internal EventBus? Events => _events;

    /// <summary>Logins, linger and saves of this world's players (backlog B4). Set once by the SessionManager.</summary>
    public SessionManager? Sessions => _sessions;

    /// <summary>The connected player of a peer; lingering players have no peer.</summary>
    public bool TryGetPlayer(int peerId, out Player player) => _playersByPeer.TryGetValue(peerId, out player!);

    internal void AttachSessions(SessionManager sessions)
    {
        if (_sessions != null)
            throw new InvalidOperationException("This world already has a SessionManager.");
        _sessions = sessions;
    }

    /// <summary>Adds a loaded character to the world, not yet bound to a peer. Simulation thread only.</summary>
    internal Player SpawnPlayer(long accountId, long characterId, MovementState movement, int heightCm)
    {
        var player = new Player(_nextEntityId++, Player.NoPeer, accountId, _detachedInputs, movement, heightCm, characterId);
        _entities.Add(player); // ids only grow, so the list stays in id order
        _grid.Add(player);
        return player;
    }

    /// <summary>Binds a (re)connected client to its player (R-2.1-08 reattach keeps the same entity).</summary>
    internal void BindPeer(Player player, int peerId, InputQueue inputs)
    {
        UnbindPeer(player);
        player.Bind(peerId, inputs);
        _playersByPeer.Add(peerId, player);
    }

    /// <summary>The client is gone: the player stays in the world without inputs or snapshots (R-2.1-08).</summary>
    internal void UnbindPeer(Player player)
    {
        if (!player.IsConnected)
            return;
        _playersByPeer.Remove(player.PeerId);
        player.Unbind(_detachedInputs);
    }

    internal void RemovePlayer(Player player)
    {
        UnbindPeer(player);
        _grid.Remove(player);
        _entities.Remove(player);
    }

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
        MoveEntities();

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
    /// Graceful shutdown of the world's sessions (R-2.1-09): the <see cref="SessionManager"/> ends every session with
    /// <see cref="SessionEndReason.ServerShutdown"/>, saves every character and releases its locks; a last phase-11
    /// dispatch then delivers the session events. Call once, after the simulation thread has stopped, from the thread
    /// that stopped it. Blocks that thread (never the tick) until the saves finish or time out.
    /// </summary>
    public void Shutdown()
    {
        _sessions?.Shutdown();
        _eventDispatch.Run(ServerTick);
    }

    private void Begin(TickPhase phase) => PhaseStarted?.Invoke(phase);

    /// <summary>
    /// Connects and disconnects go to the <see cref="SessionManager"/> (backlog B4): it loads the character off the tick,
    /// spawns or reattaches it, and keeps disconnected characters lingering (R-2.1-08).
    /// </summary>
    private void ApplyCommands()
    {
        while (_inbox.TryTake(out var command))
        {
            var sessions = _sessions ?? throw new InvalidOperationException("The world has no SessionManager.");
            switch (command.Kind)
            {
                case WorldCommandKind.Join:
                    sessions.OnConnected(command.PeerId, command.AccountId, command.Slot, command.Inputs!);
                    break;
                case WorldCommandKind.Leave:
                    sessions.OnDisconnected(command.PeerId);
                    break;
            }
        }
        _sessions?.Tick();
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

    /// <summary>Phase 2: the same MovementSim the client predicts with (spec 2.2).</summary>
    private void MoveEntities()
    {
        foreach (var entity in _entities)
        {
            if (entity is not Player { HasInputThisTick: true } player)
                continue;
            // TODO(D2): actionActive from ActionMachine. TODO(D5): speed from the movement-speed stat (R-2.2-03).
            player.Movement = MovementSim.Step(player.Movement, player.CurrentInput, actionActive: false,
                _settings.MoveSpeedCmPerSec, _settings.TickRateHz);
            _grid.Update(player);
        }
    }

    private void UpdateInterest()
    {
        foreach (var entity in _entities)
        {
            if (entity is Player { IsConnected: true } player) // lingering players get no snapshots
                _interest.Update(player);
        }
    }

    private void SendSnapshots(uint tick)
    {
        foreach (var entity in _entities)
        {
            if (entity is Player { IsConnected: true } player) // lingering players get no snapshots
                _snapshots.Send(player, tick);
        }
    }
}
