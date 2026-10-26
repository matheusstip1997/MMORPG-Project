using GameServer.AI;
using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Net;
using GameServer.Sessions;
using GameServer.World.Map;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Util;
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
    private readonly ActionCatalog _actions;
    private readonly ActionRunner _actionRunner;
    private readonly ActionResultSender _actionResults;
    private readonly IdentitySender _identities;
    private readonly StrikeQueue _strikeQueue = new();
    private readonly EffectRouter _effects;
    private readonly HitResolver? _hits;
    private readonly StrikeResolver? _strikes;
    private readonly StatusSystem? _statuses;
    private readonly HitSender _hitSender;
    private readonly ITickHook _eventDispatch;
    private readonly ITickHook _telemetryFlush;
    private readonly EventBus? _events;
    private readonly CombatRules? _combat;
    private readonly SpawnController? _spawner;
    private readonly ProjectileSystem? _projectiles;
    private readonly ProjectileSender? _projectileSender;
    private readonly BuildUpSystem? _buildUps;
    private readonly GroundAreaSystem? _groundAreas;
    private readonly GroundAreaSender? _groundAreaSender;
    // Cached so phases 7 and 8 allocate no delegate per tick.
    private readonly Func<MonsterDef, WorldMarker, string, Monster> _spawnMonster;
    private readonly Action<Monster> _removeMonster;
    private readonly Func<uint, Entity?> _findEntity;
    private readonly MonsterAi? _monsterAi;
    private readonly InventoryService? _inventory;
    private readonly LootService? _loot;
    private readonly InventoryMoveHandler? _moves;
    private readonly InventorySender _inventorySender;
    private readonly Dictionary<long, Player> _playersByCharacter = new();
    /// <summary>Inputs of unbound (lingering) players: nobody ever enqueues here, so they stand still.</summary>
    private readonly InputQueue _detachedInputs = new(1);
    private SessionManager? _sessions;
    private uint _nextEntityId = 1;

    /// <param name="events">
    /// The world's bus; the world publishes its session events there (spec 8.1). <paramref name="eventDispatch"/> is what
    /// delivers them in phase 11.
    /// </param>
    /// <param name="map">The loaded world; it must hold the <see cref="WorldSettings.PlayerSpawnMarkerId"/> spawn.</param>
    /// <param name="violations">Where speed-check violations are reported (telemetry in the server).</param>
    /// <param name="actions">The action tables (backlog D2); without them players have no actions.</param>
    /// <param name="combat">
    /// Classes, monsters and spawn points (backlog D5 wiring, F); without them entities have no combatant and no monster
    /// spawns. Its <see cref="SpawnPoints"/> markers must be monsterSpawn markers of <paramref name="map"/>.
    /// </param>
    /// <param name="projectiles">The projectile tables (spec 3.2, backlog D3); without them projectile effects do nothing.</param>
    /// <param name="items">
    /// Items, the Normal bag and loot tables (backlog G); without them there is no inventory and monsters drop nothing.
    /// Loot needs <paramref name="combat"/> too (monsters and world time).
    /// </param>
    /// <param name="runId">Unique per world run (drop keys, so pickup operation keys never repeat); a new GUID if null.</param>
    public GameWorld(
        WorldSettings settings,
        WorldMap map,
        WorldInbox inbox,
        ITransport transport,
        IVisibilityQuery? visibility = null,
        ITickHook? eventDispatch = null,
        ITickHook? telemetryFlush = null,
        EventBus? events = null,
        IMovementViolationSink? violations = null,
        ActionCatalog? actions = null,
        CombatRules? combat = null,
        IClock? clock = null,
        ulong rngSeed = 0,
        ulong rngStream = 0,
        ProjectileRules? projectiles = null,
        ItemRules? items = null,
        string? runId = null)
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
        _actions = actions ?? ActionCatalog.Empty;
        if (_actions.Count > 0 && _actions.TickRateHz != settings.TickRateHz)
            throw new ArgumentException("The action catalog was built for another tick rate.", nameof(actions));
        _actionRunner = new ActionRunner(map, _grid, events, combat);
        _actionResults = new ActionResultSender(transport);
        _identities = new IdentitySender(transport);
        if (combat != null && combat.TickRateHz != settings.TickRateHz)
            throw new ArgumentException("The combat rules were built for another tick rate.", nameof(combat));
        _combat = combat;
        _spawner = combat == null ? null : new SpawnController(combat, map);
        _effects = new EffectRouter(_strikeQueue);
        _actionRunner.Effects = _effects;
        if (combat != null)
        {
            // Rule 6: one seeded generator per world for the crit rolls; the host logs the seed and stream.
            RngSeed = rngSeed;
            RngStream = rngStream;
            _hits = new HitResolver(combat, _actions, events, clock ?? new GameServer.Host.SystemClock(), new Pcg32Rng(rngSeed, rngStream));
            _strikes = new StrikeResolver(_grid, combat, _hits);
            // Phase 6 (spec 3.7): statuses, and the heals and status effects phase 3 fires.
            _statuses = new StatusSystem(_grid, combat, _hits, events);
            _effects.Statuses = _statuses;
            // Spec 3.8: build-up changes of landed hits, triggers through the statuses, resets when those end.
            _buildUps = new BuildUpSystem(combat, _statuses, events);
            _hits.BuildUps = _buildUps;
            _statuses.BuildUps = _buildUps;
            _snapshots.BuildUps = combat.BuildUps;
            if (combat.GroundAreas is { } areaSettings)
            {
                // Spec 3.1: ground areas, placed in phase 3, ticking in phase 5 through the same hit pipeline.
                _groundAreas = new GroundAreaSystem(combat, areaSettings, _grid, _hits, _statuses, events);
                _effects.GroundAreas = _groundAreas;
                _groundAreaSender = new GroundAreaSender(transport, settings.MaxSnapshotBytes, settings.InterestRadiusCm,
                    areaSettings.EventRepeatTicks);
            }
        }
        _hitSender = new HitSender(transport, settings.MaxSnapshotBytes);
        if (projectiles != null)
        {
            if (projectiles.TickRateHz != settings.TickRateHz)
                throw new ArgumentException("The projectile rules were built for another tick rate.", nameof(projectiles));
            // Phase 4 (spec 3.2): every hit through the same per-target pipeline as strikes (R-3.2-02). Without combat
            // rules nothing can be hit, so projectiles fly through everyone.
            _projectiles = new ProjectileSystem(projectiles, map, _grid, events, _hits, combat?.MaxBodyRadiusCm ?? 0);
            _effects.Projectiles = _projectiles;
            _projectileSender = new ProjectileSender(transport, settings.MaxSnapshotBytes, settings.InterestRadiusCm,
                projectiles.Settings.EventRepeatTicks);
        }
        _spawnMonster = SpawnMonster;
        _removeMonster = RemoveMonster;
        _findEntity = FindEntity;
        if (combat?.MonsterAi is { } aiSettings)
        {
            // Backlog F: the generic monster AI, phases 7 (think), 2 (walk) and 3 (attack). Without its settings table
            // monsters stand where they spawned.
            var aiRules = new MonsterAiRules(aiSettings, combat, _actions, map, projectiles);
            _monsterAi = new MonsterAi(aiRules, map, _grid, visibility ?? EveryoneVisible.Instance);
            _actionRunner.MonsterAi = _monsterAi;
            if (_statuses != null)
                _monsterAi.LeashReset = (monster, tick) => _statuses.EndAll(monster, StatusEndReason.Reset, tick);
            if (events != null)
                new MonsterAiEvents(_findEntity).Subscribe(events);
        }
        _inventorySender = new InventorySender(transport);
        if (items != null)
        {
            // Backlog G: the only writer of items (rule 4), and monster loot. Loot rolls come from their own seeded stream
            // of the world seed (rule 6), so they do not shift the crit rolls; the host logs it.
            _inventory = new InventoryService(items, events) { ContentsCommitted = OnInventoryCommitted };
            _moves = new InventoryMoveHandler(_inventory, runId ??= Guid.NewGuid().ToString("N"));
            if (combat != null)
            {
                LootRngStream = rngStream + LootStreamOffset;
                _loot = new LootService(items, _inventory, new LootWorld(this), events, new Pcg32Rng(rngSeed, LootRngStream),
                    visibility ?? EveryoneVisible.Instance, runId, () => NowMs);
                if (events != null)
                    _loot.Subscribe(events);
            }
        }
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

    public ActionCatalog Actions => _actions;

    /// <summary>Classes, monsters and spawn points; null when the world was built without them.</summary>
    public CombatRules? Combat => _combat;

    /// <summary>The simulation time of the running tick in ms (<see cref="CombatRules.MsAt"/>); 0 without combat rules.</summary>
    public long NowMs => _combat?.MsAt(ServerTick) ?? 0;

    /// <summary>Where phase 3 hands the effects actions fire (strikes to phase 5, heals and statuses to phase 6).</summary>
    internal IActionEffectSink ActionEffects
    {
        get => _actionRunner.Effects;
        set => _actionRunner.Effects = value;
    }

    /// <summary>Routes fired effects: strikes to phase 5, projectiles to <see cref="EffectRouter.Projectiles"/>.</summary>
    internal EffectRouter EffectRouter => _effects;

    /// <summary>The per-target hit pipeline (spec 3.1); null when the world was built without combat rules.</summary>
    internal HitResolver? Hits => _hits;

    /// <summary>Phase 5 strike resolution; null without combat rules.</summary>
    internal StrikeResolver? Strikes => _strikes;

    /// <summary>Phase 6 statuses, heals and status effects (spec 3.7); null without combat rules.</summary>
    internal StatusSystem? Statuses => _statuses;

    /// <summary>Phase 4 (spec 3.2); null when the world was built without projectile tables.</summary>
    internal ProjectileSystem? Projectiles => _projectiles;

    /// <summary>Build-up meters (spec 3.8); null without combat rules.</summary>
    internal BuildUpSystem? BuildUps => _buildUps;

    /// <summary>Ground areas (spec 3.1); null without combat rules or their <c>GroundAreaSettings</c>.</summary>
    internal GroundAreaSystem? GroundAreas => _groundAreas;

    /// <summary>The monster AI (backlog F); null without combat rules or their <c>MonsterAiSettings</c>.</summary>
    internal MonsterAi? MonsterAi => _monsterAi;

    /// <summary>The entity with this id, or null (binary search: the list is in id order). Simulation thread only.</summary>
    internal Entity? FindEntity(uint id)
    {
        int low = 0, high = _entities.Count - 1;
        while (low <= high)
        {
            int mid = (low + high) >> 1;
            uint midId = _entities[mid].Id;
            if (midId == id)
                return _entities[mid];
            if (midId < id)
                low = mid + 1;
            else
                high = mid - 1;
        }
        return null;
    }

    /// <summary>Added to the crit stream for the loot generator's stream (same seed, independent sequence).</summary>
    public const ulong LootStreamOffset = 0x4C4F4F54; // "LOOT"

    /// <summary>Stream of the loot generator (seed <see cref="RngSeed"/>); logged by the host. 0 without loot.</summary>
    public ulong LootRngStream { get; }

    /// <summary>The only writer of items (backlog G); null when the world was built without item tables.</summary>
    public InventoryService? Inventory => _inventory;

    /// <summary>Monster loot and ground drops (backlog G); null without item tables or combat rules.</summary>
    internal LootService? Loot => _loot;

    /// <summary>Seed and stream of the world's crit generator (architecture rule 6: logged by the host).</summary>
    public ulong RngSeed { get; }

    public ulong RngStream { get; }

    /// <summary>Spec 2.2 speed-check violations since the world started (each one is also reported to the sink).</summary>
    public long SpeedViolations { get; private set; }

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

    /// <summary>
    /// Where a character without a usable saved position appears: the exported <c>ServerLoop.PlayerSpawnMarkerId</c>
    /// marker (backlog C2), at the grid's height there. The <see cref="SessionManager"/>'s fallback spawn reads this.
    /// </summary>
    internal (MovementState Movement, int HeightCm) DefaultSpawn() =>
        (new MovementState(_spawn.Position, _spawn.Facing), _map.HeightCmAt(_spawn.Position, _map.Grid.BaseHeightCm));

    /// <summary>
    /// Adds a loaded character to the world, not yet bound to a peer. Simulation thread only. Height and region come
    /// from the world grid (the saved height is only the fallback), and the speed check starts from here (spec 2.2).
    /// TODO(C3): a saved position that the current world marks blocked should move to the nearest walkable cell.
    /// With <paramref name="classDef"/> (and combat rules) the player gets its combatant: full meters, or the
    /// <paramref name="saved"/> ones (clamped to the class maxima when read). A character saved at 0 HP died: it comes back
    /// at the city spawn with full meters, like a respawn (user decision 2026-10-22).
    /// </summary>
    internal Player SpawnPlayer(long accountId, long characterId, MovementState movement, int heightCm,
        ClassDef? classDef = null, SavedMeters? saved = null, string name = "")
    {
        if (_combat != null && classDef != null && saved is { Hp: <= 0 })
        {
            (movement, heightCm) = DefaultSpawn();
            saved = null;
        }
        int height = _map.HeightCmAt(movement.Position, heightCm);
        var player = new Player(_nextEntityId++, Player.NoPeer, accountId, _detachedInputs, movement, height, characterId, name);
        player.RegionIndex = _map.Grid.RegionIndexAt(movement.Position);
        player.SpeedCheckedPosition = movement.Position;
        // Backlog E: the class's own normal attack and kit (its ActionBindings row, spec 9.6).
        player.Actions = new ActionMachineState(_actions, _actions.Loadout(classDef?.Id));
        if (_combat != null && classDef != null)
        {
            long now = NowMs;
            var combatant = _combat.ForPlayer(classDef, now);
            if (saved is { } meters)
                CombatRules.Restore(combatant, meters, now);
            combatant.History.Record(ServerTick, movement.Position);
            player.Combatant = combatant;
            player.DefId = (ushort)classDef.NetId;
        }
        if (characterId != 0)
            _playersByCharacter[characterId] = player;
        _entities.Add(player); // ids only grow, so the list stays in id order
        _grid.Add(player);
        return player;
    }

    /// <summary>Phase 7, from the <see cref="SpawnController"/>: a monster at its spawn marker, with full meters.</summary>
    private Monster SpawnMonster(MonsterDef def, WorldMarker marker, string spawnPointId)
    {
        var movement = new MovementState(marker.Position, marker.Facing);
        int height = _map.HeightCmAt(marker.Position, _map.Grid.BaseHeightCm);
        var combatant = _combat!.ForMonster(def, NowMs);
        combatant.History.Record(ServerTick, marker.Position);
        var monster = new Monster(_nextEntityId++, def, spawnPointId, movement, height, combatant)
        {
            Actions = new ActionMachineState(_actions, ActionLoadout.None), // monsters start actions through their AI's request
            RegionIndex = _map.Grid.RegionIndexAt(marker.Position),
            SpeedCheckedPosition = marker.Position,
        };
        _entities.Add(monster); // ids only grow, so the list stays in id order
        _grid.Add(monster);
        return monster;
    }

    /// <summary>Phase 8, from the <see cref="SpawnController"/>: a dead monster leaves the world (observers get a despawn).</summary>
    private void RemoveMonster(Monster monster)
    {
        _projectiles?.OwnerRemoved(monster, ServerTick);
        _groundAreas?.OwnerRemoved(monster, ServerTick);
        _grid.Remove(monster);
        _entities.Remove(monster);
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
        if (_playersByCharacter.TryGetValue(player.CharacterId, out var byCharacter) && byCharacter == player)
        {
            _playersByCharacter.Remove(player.CharacterId);
            _inventory?.Detach(player.CharacterId); // its last save (logout) is in, or it faulted
        }
        _projectiles?.OwnerRemoved(player, ServerTick);
        _groundAreas?.OwnerRemoved(player, ServerTick);
        _hits?.TargetRemoved(player);
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
        ApplyInputs(tick);
        HandleRequests();

        Begin(TickPhase.Movement);
        MoveEntities(tick);

        Begin(TickPhase.ActionStateMachines);
        RunActions(tick);

        Begin(TickPhase.ProjectileMovement);
        _projectiles?.Step(tick);

        Begin(TickPhase.HitResolution);
        // Strikes fired in phase 3 (spec 3.1), then the ground areas' ticks, then the monsters' pending hits on players that
        // are due (R-3.1-03). Build-up changes ride on the hits (spec 3.8).
        if (_strikes != null)
            _strikes.Resolve(_strikeQueue);
        else
            _strikeQueue.Clear();
        _groundAreas?.Step(tick);
        _hits?.ResolveDue(tick);

        Begin(TickPhase.StatusAndGauges);
        // Statuses (deaths, periodic ticks, expiry; an ended status resets the build-up meter it locked), then the heals,
        // status effects and build-up reductions of phase 3 (spec 3.7, 3.8). Resource and build-up meters are lazy.
        _statuses?.Run(tick, _entities);

        Begin(TickPhase.AiAndSpawns);
        ThinkMonsters(tick);
        _spawner?.SpawnDue(tick, _spawnMonster);

        Begin(TickPhase.DeathAndRespawn);
        // Deaths happen in phase 5 (HitResolver).
        _spawner?.RemoveDead(tick, _removeMonster);
        RespawnPlayers(tick);
        _loot?.Expire();

        Begin(TickPhase.VisibilityAndInterest);
        if (snapshotTick)
            UpdateInterest();

        Begin(TickPhase.SnapshotsAndSends);
        if (snapshotTick)
        {
            SendIdentities();
            SendSnapshots(tick);
        }
        SendHits(tick);
        SendActionResults();
        SendInventory();
        SendProjectiles(tick);
        SendGroundAreas(tick);

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

    /// <summary>
    /// Phase 1: one queued input frame per player per tick, the way the client predicted it (spec 2.2). Each frame's view
    /// clock sets the player's view lag under the clock-sync limits (R-3.1-03, <see cref="DefenceTimeline.OnFrame"/>):
    /// what its hits on monsters are rewound by and how long monsters' hits on it wait.
    /// </summary>
    private void ApplyInputs(uint tick)
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
            player.CurrentActions = frame.Actions;
            player.CurrentSkillSlot = frame.SkillSlot;
            player.HasInputThisTick = true;
            player.HasInputAck = true;
            player.LastInputSequence = frame.Sequence;
            if (_combat != null)
                player.Defence.OnFrame(tick, frame.ViewTick, frame.SubTick, player.Inputs.RoundTripMs, _combat.ClockSync);
        }
    }

    /// <summary>
    /// Phase 2: the same MovementSim and grid collision the client predicts with (spec 2.2, backlog C3), then height and
    /// region from the grid, then the speed check for every player. Last, every combatant records where it stands in its
    /// position history (R-3.1-03 rewind); monsters walk where their AI decided in the last phase 7 (backlog F) before it.
    /// </summary>
    private void MoveEntities(uint tick)
    {
        foreach (var entity in _entities)
        {
            if (entity is Player player)
                MovePlayer(player, tick);
            else if (entity is Monster monster)
                _monsterAi?.Move(monster);
            entity.Combatant?.History.Record(tick, entity.Movement.Position);
        }
    }

    private void MovePlayer(Player player, uint tick)
    {
        if (player.HasInputThisTick)
        {
            var before = player.Movement.Position;
            // R-2.2-02: while an action (or a flinch, a stagger) runs, move keys and facing are ignored. The machine
            // state is the one phase 3 left last tick, so an action started this tick stops movement from the next one.
            // TODO(D5): speed from the movement-speed stat (R-2.2-03).
            player.Movement = MovementSim.Step(player.Movement, player.CurrentInput, player.Actions.IsBusy,
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

    /// <summary>
    /// Phase 3: every player's action machine on the input of phase 1, in entity-id order (spec 1.6, backlog D2); lingering
    /// players have no input, so their running action just plays out. Monsters start the attack their AI requested.
    /// </summary>
    private void RunActions(uint tick)
    {
        foreach (var entity in _entities)
        {
            if (entity is Player player)
                _actionRunner.Step(player, tick);
            else if (entity is Monster monster)
                _actionRunner.Step(monster, tick);
        }
    }

    /// <summary>Phase 7 (backlog F): every monster's AI in entity-id order decides what it does next tick.</summary>
    private void ThinkMonsters(uint tick)
    {
        if (_monsterAi == null)
            return;
        long now = NowMs;
        foreach (var entity in _entities)
        {
            if (entity is Monster monster)
                _monsterAi.Think(monster, tick, now, _findEntity);
        }
    }

    /// <summary>
    /// Phase 8 (spec 3.1, user decision 2026-10-22): a player dead for <c>playerRespawnDelayMs</c> comes back at the city
    /// spawn (<see cref="DefaultSpawn"/>) with full HP, MP, stamina and poise, an idle action machine and a fresh position
    /// history; the speed check restarts there, and <see cref="Respawned"/> is published. Lingering players too. No prompt
    /// yet: the spec 9 respawn prompt comes with the HUD (backlog I). TODO(M5, R-6.3-03): no self-respawn for bad honor.
    /// </summary>
    private void RespawnPlayers(uint tick)
    {
        if (_combat == null)
            return;
        foreach (var entity in _entities)
        {
            if (entity is Player { Combatant: { IsAlive: false } combatant } player
                && (int)(tick - combatant.DiedAtTick) >= _combat.PlayerRespawnTicks)
            {
                Respawn(player, combatant, tick);
            }
        }
    }

    private void Respawn(Player player, Combatant combatant, uint tick)
    {
        var (movement, heightCm) = DefaultSpawn();
        player.Movement = movement;
        player.HeightCm = heightCm;
        player.RegionIndex = _map.Grid.RegionIndexAt(movement.Position);
        player.SpeedCheckedPosition = movement.Position;
        _grid.Update(player);
        combatant.Revive(NowMs);
        combatant.History.Clear();
        combatant.History.Record(tick, movement.Position);
        ActionMachine.Revive(player.Actions);
        _events?.Publish(new Respawned(player.Id), player.CharacterId);
    }

    /// <summary>
    /// Phase 1, after the inputs (backlog G): every player's queued reliable requests, players in entity ID order and each
    /// player's in arrival order (rule 6), so of two players asking for one drop in the same tick the lower id gets it, and
    /// a player's bag moves run one after the other (WP16, <see cref="InventoryMoveHandler"/>). Lingering players have no
    /// requests.
    /// </summary>
    private void HandleRequests()
    {
        foreach (var entity in _entities)
        {
            if (entity is not Player player)
                continue;
            var requests = player.Inputs.Requests;
            while (requests.TryDequeue(out var request))
            {
                if (request.Kind == Shared.Protocol.ReliableMessageId.InventoryMoveRequest)
                {
                    if (_moves != null)
                        _moves.Handle(player, request);
                    else
                        player.MoveRefusals.Add(new Shared.Protocol.InventoryMoveResult
                        {
                            RequestId = request.RequestId,
                            Reason = Shared.Protocol.InventoryMoveReason.InventoryUnavailable,
                        });
                    continue;
                }
                if (request.Kind != Shared.Protocol.ReliableMessageId.PickupRequest)
                    continue;
                int taken = 0;
                var reason = _loot?.Pickup(player, request.DropEntityId, out taken) ?? Shared.Protocol.PickupReason.LootNotFound;
                player.PickupResults.Add(new Shared.Protocol.PickupResult
                {
                    DropEntityId = request.DropEntityId,
                    Reason = reason,
                    Taken = (ushort)taken,
                });
            }
        }
        _loot?.RemoveTaken();
    }

    /// <summary>Phase 10: pickup and bag move answers and committed bags to connected players (backlog G).</summary>
    private void SendInventory()
    {
        foreach (var entity in _entities)
        {
            if (entity is not Player player)
                continue;
            if (player.IsConnected)
                _inventorySender.Send(player, _inventory);
            else
                player.ClearInventoryAnswers();
        }
    }

    /// <summary>A save committed a character's inventory changes (rule 5): now its client may see them.</summary>
    private void OnInventoryCommitted(long characterId)
    {
        if (_playersByCharacter.TryGetValue(characterId, out var player))
            player.InventoryDirty = true;
    }

    /// <summary>Adds and removes ground drops for <see cref="LootService"/> like monsters: entity list and grid.</summary>
    private sealed class LootWorld(GameWorld world) : ILootWorld
    {
        public (MovementState Movement, int HeightCm) PlaceDrop(FixVec2 wanted, FixVec2 fallback)
        {
            var position = world._map.Grid.IsWalkable(wanted) ? wanted : fallback;
            return (new MovementState(position, 0), world._map.HeightCmAt(position, world._map.Grid.BaseHeightCm));
        }

        public LootDrop AddDrop(Func<uint, LootDrop> create)
        {
            var drop = create(world._nextEntityId++);
            drop.RegionIndex = world._map.Grid.RegionIndexAt(drop.Movement.Position);
            drop.SpeedCheckedPosition = drop.Movement.Position;
            world._entities.Add(drop); // ids only grow, so the list stays in id order
            world._grid.Add(drop);
            return drop;
        }

        public void RemoveDrop(LootDrop drop)
        {
            world._grid.Remove(drop);
            world._entities.Remove(drop);
        }

        public Entity? FindEntity(uint id) => world.FindEntity(id);
    }

    private void SendActionResults()
    {
        foreach (var entity in _entities)
        {
            if (entity is Player player)
            {
                if (player.IsConnected)
                    _actionResults.Send(player);
                else
                    player.ActionResults.Clear();
            }
        }
    }

    /// <summary>
    /// Phase 10, every tick (spec 3.2): projectile spawns and despawns to the players they concern, then projectiles whose
    /// despawn went out often enough go back to the pool.
    /// </summary>
    private void SendProjectiles(uint tick)
    {
        if (_projectiles == null || _projectileSender == null)
            return;
        var active = _projectiles.Active;
        if (active.Count > 0)
        {
            foreach (var entity in _entities)
            {
                if (entity is Player { IsConnected: true } player) // lingering players get nothing
                    _projectileSender.Send(player, active, tick);
            }
        }
        _projectiles.ReleaseEnded(tick);
    }

    /// <summary>
    /// Phase 10, every tick (spec 3.1): ground area spawns and despawns to the players they concern, then areas whose
    /// despawn went out often enough go back to the pool.
    /// </summary>
    private void SendGroundAreas(uint tick)
    {
        if (_groundAreas == null || _groundAreaSender == null)
            return;
        var active = _groundAreas.Active;
        if (active.Count > 0)
        {
            foreach (var entity in _entities)
            {
                if (entity is Player { IsConnected: true } player) // lingering players get nothing
                    _groundAreaSender.Send(player, active, tick);
            }
        }
        _groundAreas.ReleaseEnded(tick);
    }

    /// <summary>Phase 10: this tick's hits to every connected observer that has the target in interest (backlog D3).</summary>
    private void SendHits(uint tick)
    {
        if (_hits == null)
            return;
        var notices = _hits.Notices;
        if (notices.Count > 0)
        {
            foreach (var entity in _entities)
            {
                if (entity is Player { IsConnected: true } player)
                    _hitSender.Send(player, tick, notices);
            }
        }
        _hits.ClearNotices();
    }

    private void UpdateInterest()
    {
        foreach (var entity in _entities)
        {
            if (entity is Player { IsConnected: true } player) // lingering players get no snapshots
                _interest.Update(player);
        }
    }

    /// <summary>
    /// Phase 10, on snapshot ticks after the interest update: the display name of every player that just entered an
    /// observer's interest, itself included (spec 9.3 name plates; <see cref="IdentitySender"/>).
    /// </summary>
    private void SendIdentities()
    {
        foreach (var entity in _entities)
        {
            if (entity is Player { IsConnected: true } player)
                _identities.Send(player);
        }
    }

    private void SendSnapshots(uint tick)
    {
        foreach (var entity in _entities)
        {
            if (entity is Player { IsConnected: true } player) // lingering players get no snapshots
                _snapshots.Send(player, tick, NowMs);
        }
    }
}
