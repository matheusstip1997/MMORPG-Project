using GameServer.AI;
using GameServer.Combat;
using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Net;
using GameServer.Outposts;
using GameServer.Sessions;
using GameServer.Social;
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
    private readonly BodyBlockers _bodies;
    private readonly InterestManager _interest;
    private readonly ITransport _transport;
    private readonly SnapshotSender _snapshots;
    private ActionCatalog _actions;
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
    private CombatRules? _combat;
    private readonly SpawnController? _spawner;
    private readonly ProjectileSystem? _projectiles;
    private readonly ProjectileSender? _projectileSender;
    private readonly BuildUpSystem? _buildUps;
    private readonly GroundAreaSystem? _groundAreas;
    private readonly GroundAreaSender? _groundAreaSender;
    private readonly IClock _clock;
    private readonly ChatService? _chat;
    private readonly ChatSender _chatSender;
    // Cached so phases 7 and 8 allocate no delegate per tick.
    private readonly Func<MonsterDef, WorldMarker, string, Monster> _spawnMonster;
    private readonly Action<Monster> _removeMonster;
    private readonly Func<uint, Entity?> _findEntity;
    private readonly MonsterAi? _monsterAi;
    private readonly InventoryService? _inventory;
    private readonly LootService? _loot;
    private readonly InventoryMoveHandler? _moves;
    private readonly InventorySender _inventorySender;
    private readonly OutpostSender _outpostSender;
    private SaleHandler? _sales;
    private VendorHandler? _vendor;
    private readonly List<DemandStateRow> _demandRows = new();
    private long _nextDemandSaveMs;
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
    /// <param name="chat">Local chat limits (R-6.5-01); without them chat requests are dropped and counted.</param>
    /// <param name="strikeTrace">Dev only (spec 11.7): told every strike and every target it is tested against.</param>
    /// <param name="economy">
    /// Outposts, prices, demand and vendors (spec 4.2, WP-M2-4); without them there are no NPCs and respawn delays stay at
    /// their base. Selling and buying need <paramref name="items"/> too.
    /// </param>
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
        string? runId = null,
        ChatSettings? chat = null,
        IStrikeTraceSink? strikeTrace = null,
        EconomyRules? economy = null)
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
        _bodies = new BodyBlockers(_grid);
        _interest = new InterestManager(_grid, settings.InterestRadiusCm, visibility ?? EveryoneVisible.Instance);
        _transport = transport;
        _snapshots = new SnapshotSender(transport, settings.MaxSnapshotBytes);
        _eventDispatch = eventDispatch ?? NoTickHook.Instance;
        _telemetryFlush = telemetryFlush ?? NoTickHook.Instance;
        _actions = actions ?? ActionCatalog.Empty;
        if (_actions.Count > 0 && _actions.TickRateHz != settings.TickRateHz)
            throw new ArgumentException("The action catalog was built for another tick rate.", nameof(actions));
        _actionRunner = new ActionRunner(map, _grid, events, combat);
        _actionRunner.Bodies = _bodies;
        _actionResults = new ActionResultSender(transport);
        _identities = new IdentitySender(transport);
        _clock = clock ?? new GameServer.Host.SystemClock();
        _chat = chat == null ? null : new ChatService(chat);
        _chatSender = new ChatSender(transport, _grid, settings.InterestRadiusCm);
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
            _hits = new HitResolver(combat, _actions, events, _clock, new Pcg32Rng(rngSeed, rngStream));
            _strikes = new StrikeResolver(_grid, combat, _hits) { Trace = strikeTrace };
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
            _monsterAi = new MonsterAi(aiRules, map, _grid, visibility ?? EveryoneVisible.Instance, _bodies);
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
            _moves = new InventoryMoveHandler(_inventory, runId ??= Guid.NewGuid().ToString("N"), () => _clock.UtcNowMs);
            if (combat != null)
            {
                LootRngStream = rngStream + LootStreamOffset;
                _loot = new LootService(items, _inventory, new LootWorld(this), events, new Pcg32Rng(rngSeed, LootRngStream),
                    visibility ?? EveryoneVisible.Instance, runId, () => NowMs);
                if (events != null)
                    _loot.Subscribe(events);
            }
        }
        _outpostSender = new OutpostSender(transport);
        if (economy != null)
            BuildEconomy(economy, map, events, visibility ?? EveryoneVisible.Instance, runId ??= Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Outposts, selling, demand and vendors (spec 4.2, WP-M2-4): one NPC per npc marker (in marker id order, rule 6), the
    /// interaction registry with the sell and buy handlers (selling and buying need the item tables), the demand loop and
    /// its hold on respawn delays, and the faucet and sink totals.
    /// </summary>
    private void BuildEconomy(EconomyRules economy, WorldMap map, EventBus? events, IVisibilityQuery visibility, string runId)
    {
        Economy = economy;
        foreach (var marker in map.Markers.OrderBy(m => m.Id, StringComparer.Ordinal)) // rule 6, whatever order the map lists
        {
            if (marker.Kind != Shared.World.MarkerKind.Npc)
                continue;
            if (!economy.TryGetNpc(marker.DefId, out var def))
                throw new ArgumentException($"npc marker '{marker.Id}' names unknown NpcDef '{marker.DefId}'", nameof(map));
            var npc = new Npc(_nextEntityId++, def, marker.Id, new MovementState(marker.Position, marker.Facing),
                map.HeightCmAt(marker.Position, map.Grid.BaseHeightCm))
            {
                RegionIndex = map.Grid.RegionIndexAt(marker.Position),
                SpeedCheckedPosition = marker.Position,
            };
            _entities.Add(npc);
            _grid.Add(npc);
        }

        Outposts = new OutpostInteractions(_findEntity, visibility, events, _clock, economy.Settings);
        Demand = new DemandService(economy, map, events);
        Ledger = new EconomyLedger();
        if (events != null)
        {
            Demand.Subscribe(events);
            Ledger.Subscribe(events);
        }
        if (_combat != null)
            _spawner?.SetDemand(Demand, _combat.TicksFor(economy.Demand.MinRespawnMs));
        Prices = new PriceService(economy, Demand);
        if (_inventory != null)
        {
            _inventory.MaxGold = economy.Settings.MaxGold;
            _sales = new SaleHandler(_inventory, Prices, Outposts, runId);
            _vendor = new VendorHandler(_inventory, economy, Outposts, runId);
            Outposts.Register(NpcInteraction.Sell, _sales);
            Outposts.Register(NpcInteraction.Buy, _vendor);
        }
    }

    public WorldSettings Settings => _settings;

    /// <summary>The economy tables (spec 4.2); null when the world was built without them.</summary>
    public EconomyRules? Economy { get; private set; }

    /// <summary>
    /// The NPC interaction registry (spec 4.2): other systems register their interaction kind's handler here when the world
    /// is built (WP-M2-3 rankUp, WP-M2-6 quests). Null without the economy tables.
    /// </summary>
    public OutpostInteractions? Outposts { get; private set; }

    /// <summary>The demand loop (R-4.2-03); null without the economy tables.</summary>
    public DemandService? Demand { get; private set; }

    /// <summary>Outpost sell prices (R-4.2-02); null without the economy tables.</summary>
    public PriceService? Prices { get; private set; }

    /// <summary>Faucet and sink totals (R-4.2-07); null without the economy tables.</summary>
    public EconomyLedger? Ledger { get; private set; }

    /// <summary>
    /// Where the demand state goes every <c>DemandSettings.persistIntervalMs</c> (set by the host before the world starts;
    /// null: not saved).
    /// </summary>
    public GameServer.Persistence.IDemandStateSink? DemandStateSink { get; set; }

    /// <summary>A copy of the demand loop's state for saving (world thread, or after the world stopped).</summary>
    public IReadOnlyList<DemandStateRow> CaptureDemandState()
    {
        if (Demand == null)
            return Array.Empty<DemandStateRow>();
        Demand.Capture(_demandRows, netId => _inventory != null && _inventory.Rules.TryGetItem(netId, out var item) ? item.Id : null);
        return _demandRows.ToArray();
    }

    /// <summary>Restores saved demand state before the world starts (the host reads it from the database).</summary>
    public void RestoreDemandState(IReadOnlyList<DemandStateRow> rows) =>
        Demand?.Restore(rows, id => _inventory != null && _inventory.Rules.TryGetItem(id, out var item) ? item.NetId : null);

    /// <summary>Phase 7: hands a copy of the demand state to <see cref="DemandStateSink"/> when due (volatile class, R-7.1-01).</summary>
    private void SaveDemandStateWhenDue(long nowMs)
    {
        if (Demand == null || DemandStateSink == null || Economy == null)
            return;
        if (_nextDemandSaveMs == 0)
        {
            _nextDemandSaveMs = nowMs + Economy.Demand.PersistIntervalMs;
            return;
        }
        if (nowMs < _nextDemandSaveMs)
            return;
        _nextDemandSaveMs = nowMs + Economy.Demand.PersistIntervalMs;
        DemandStateSink.Save(CaptureDemandState());
    }

    /// <summary>Where a dev hot reload posts its prepared rules (spec 11.7); taken at the top of the next tick.</summary>
    public RetuneMailbox Retunes { get; } = new();

    /// <summary>Number of the next tick to run; snapshots carry the tick they were built in.</summary>
    public uint ServerTick { get; private set; }

    /// <summary>Every entity, in id order. Simulation thread only.</summary>
    public IReadOnlyList<Entity> Entities => _entities;

    public SpatialGrid Grid => _grid;

    /// <summary>The world's collision grid, regions and markers. Region flags for other systems: <see cref="WorldMap.TryGetFlags"/>.</summary>
    public WorldMap Map => _map;

    public long SnapshotsTruncated => _snapshots.Truncated;

    /// <summary>Unreliable messages the transport dropped as over a peer's datagram limit (R-2.5-03); senders keep within it, so this stays 0.</summary>
    public long UnreliableTooLarge => _transport.UnreliableTooLarge;

    /// <summary>The newest of those drops: peer, size and the peer's limit.</summary>
    public TooLargeSend LastUnreliableTooLarge => _transport.LastUnreliableTooLarge;

    /// <summary>Local chat (R-6.5-01); null when the world was built without <see cref="ChatSettings"/>.</summary>
    internal ChatService? Chat => _chat;

    /// <summary>Chat lines sent to clients, echoes included.</summary>
    public long ChatDeliveries => _chatSender.Deliveries;

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
        // Dev hot reload (spec 11.7): a prepared plan swaps in before phase 1, so the whole tick runs one set of rules.
        if (Retunes.Take() is { } plan)
            ApplyRetune(plan, tick);
        // Interest only feeds snapshots so far, so phases 9 and 10 run on snapshot ticks: enter/leave deltas then
        // always reach the snapshot that carries them.
        bool snapshotTick = tick % (uint)_settings.SnapshotIntervalTicks == 0;

        Begin(TickPhase.ApplyInputs);
        ApplyCommands();
        ApplyInputs(tick);
        HandleRequests();
        HandleChat();

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
        // The demand loop's control step when due (R-4.2-03), before this tick's respawn delays use it.
        Demand?.Tick(_clock.UtcNowMs, ConnectedPlayers());
        SaveDemandStateWhenDue(_clock.UtcNowMs);
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
        SendChat();
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
    /// Swaps in the rules of a dev hot reload at the start of <paramref name="tick"/> (spec 11.7), before phase 1, so no
    /// tick mixes old and new rules. Every system that holds rules gets the new ones; then every entity: its action
    /// machine moves to the new catalog (<see cref="ActionMachine.Retune"/>: a running action ends, cooldowns are clamped),
    /// its meters to the new regen delays, its statuses to their new rows, a monster to its new row (with no requested
    /// attack). Combatants keep their base stats until they are built again (login, monster spawn). Projectiles, ground
    /// areas and pending hits finish with the rows they were made with. Also public for tests. No I/O.
    /// </summary>
    public void ApplyRetune(RetunePlan plan, uint tick)
    {
        if (_combat == null || plan.Actions.TickRateHz != _settings.TickRateHz || plan.Combat.TickRateHz != _settings.TickRateHz)
            throw new InvalidOperationException("Only a world with combat rules at the same tick rate can be retuned.");
        var oldActions = _actions;
        _actions = plan.Actions;
        _combat = plan.Combat;
        _actionRunner.Retune(plan.Combat);
        _hits?.Retune(plan.Combat, plan.Actions);
        _strikes?.Retune(plan.Combat);
        _buildUps?.Retune(plan.Combat);
        _groundAreas?.Retune(plan.Combat);
        _spawner?.Retune(plan.Combat);
        _snapshots.BuildUps = plan.Combat.BuildUps;
        if (plan.Projectiles != null)
            _projectiles?.Retune(plan.Projectiles, plan.Combat.MaxBodyRadiusCm);
        if (plan.MonsterAi != null)
            _monsterAi?.Retune(plan.MonsterAi);

        foreach (var entity in _entities)
        {
            if (ReferenceEquals(entity.Actions.Catalog, oldActions))
            {
                var loadout = entity is Player ? _actions.Loadout(entity.Actions.Loadout.ClassId) : ActionLoadout.None;
                ActionMachine.Retune(entity.Actions, _actions, loadout);
            }
            entity.Combatant?.Meters.Retune(plan.Combat.Config);
            if (entity is Monster monster)
            {
                if (plan.Combat.TryGetMonster(monster.Def.Id, out var def))
                    monster.Def = def;
                monster.Brain.RequestedAction = -1;
            }
        }
        _statuses?.Retune(plan.Combat, _entities, tick);
        plan.MarkApplied(tick);
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
    /// Connects, disconnects and admin kicks go to the <see cref="SessionManager"/> (backlog B4, spec 7.4): it loads the
    /// character off the tick, spawns or reattaches it, and keeps disconnected characters lingering (R-2.1-08).
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
                case WorldCommandKind.AdminKick:
                    command.AdminResults!.Post(command.ActionId, sessions.AdminKick(command.CharacterId));
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
                player.TickInputCount = 0;
                player.InputBacklogStreak = 0;
                continue;
            }
            // The wire and the simulation use the same key bits (W 1, A 2, S 4, D 8); facing is already the binary
            // angle decoded by Quantize.AngleFromWire.
            var previousActions = player.CurrentActions;
            var actions = frame.Actions;
            byte skillSlot = frame.SkillSlot;
            var viewFrame = frame;
            bool pressed = (frame.Actions & ~previousActions) != 0;
            player.TickInputs[0] = new MovementInput((SimMoveKeys)(byte)frame.Keys, frame.Facing);
            int count = 1;
            // Input catch-up (spec 1.6): frames still queued after the dequeue for InputBacklogTicks ticks in a row are a
            // backlog no later tick would drain (one frame per tick in, one out), so this tick applies up to
            // MaxInputFramesPerTick of them. Movement steps once per frame (phase 2); the action machine steps once on
            // their buttons together, so every press is kept; the view clock is the frame with the first new press.
            player.InputBacklogStreak = player.Inputs.Count > 0 ? player.InputBacklogStreak + 1 : 0;
            if (player.InputBacklogStreak >= _settings.InputBacklogTicks)
            {
                while (count < _settings.MaxInputFramesPerTick && player.Inputs.TryDequeue(out var next))
                {
                    if (!pressed && (next.Actions & ~frame.Actions) != 0)
                    {
                        pressed = true;
                        viewFrame = next;
                    }
                    actions |= next.Actions;
                    if (next.SkillSlot != 0)
                        skillSlot = next.SkillSlot;
                    player.TickInputs[count++] = new MovementInput((SimMoveKeys)(byte)next.Keys, next.Facing);
                    frame = next;
                }
                InputCatchUpFrames += count - 1;
                if (player.Inputs.Count == 0)
                    player.InputBacklogStreak = 0;
            }
            if (!pressed)
                viewFrame = frame; // no press: the newest view, as on a normal tick
            player.TickInputCount = count;
            player.CurrentInput = player.TickInputs[count - 1];
            player.CurrentActions = actions;
            player.CurrentSkillSlot = skillSlot;
            player.HasInputThisTick = true;
            player.HasInputAck = true;
            player.LastInputSequence = frame.Sequence;
            if (_combat != null)
                player.Defence.OnFrame(tick, viewFrame.ViewTick, viewFrame.SubTick, player.Inputs.RoundTripMs, _combat.ClockSync);
        }
    }

    /// <summary>Frames applied as input catch-up beyond the first of their tick (spec 1.6), for tests and telemetry.</summary>
    public long InputCatchUpFrames { get; private set; }

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
            // Spec 1.6 input catch-up: one step per frame applied this tick, as the client predicted them.
            for (int i = 0; i < player.TickInputCount; i++)
                player.Movement = StepPlayer(player, player.TickInputs[i]);
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
    /// One <see cref="MovementSim"/> step of a player with world collision and, with <c>BodyCollisionSettings</c>, body
    /// collision (R-2.2-07: monsters block, players pass through each other), against the bodies where they stand now.
    /// Client prediction runs the same step (<c>LocalPlayerPredictor</c>).
    /// </summary>
    private MovementState StepPlayer(Player player, in MovementInput input)
    {
        bool busy = player.Actions.IsBusy;
        var rules = _combat?.Bodies ?? BodyCollisionRules.Off;
        if (!rules.Enabled || busy || input.Keys == SimMoveKeys.None || player.Combatant is not { } body)
            return MovementSim.Step(player.Movement, input, busy, _settings.MoveSpeedCmPerSec, _settings.TickRateHz, _map.Grid);
        var near = _bodies.For(player, body.BodyRadiusRaw,
            MovementSim.StepPerTick(_settings.MoveSpeedCmPerSec, _settings.TickRateHz), _combat!.MaxBodyRadiusRaw);
        return MovementSim.Step(player.Movement, input, false, _settings.MoveSpeedCmPerSec, _settings.TickRateHz, _map.Grid,
            near, body.BodyRadiusRaw, rules);
    }

    /// <summary>
    /// Spec 2.2: the distance since the last check must stay within maxSpeed per tick plus the tolerance. It measures from
    /// where the previous phase 2 left the entity, so a move made outside MovementSim (a bug, or later knockback without
    /// its own rule) is caught too. Violations are counted and reported, not corrected: the spec only asks to count them.
    /// </summary>
    private void CheckSpeed(Player player, uint tick)
    {
        var position = player.Movement.Position;
        // A catch-up tick (spec 1.6) applied several frames: each may move one step.
        long allowed = _allowedRawPerTick * Math.Max(1, player.TickInputCount);
        if (SpeedCheck.Exceeds(player.SpeedCheckedPosition, position, allowed))
        {
            SpeedViolations++;
            _violations.SpeedViolation(tick, player.Id, player.AccountId,
                SpeedCheck.DistanceCm(player.SpeedCheckedPosition, position), allowed >> FixedPoint.FractionBits);
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
                if (HandleOutpostRequest(player, request))
                    continue;
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

    /// <summary>
    /// Phase 1 (WP-M2-4): an NPC interaction, a sale quote, a sale or a vendor purchase, in the player's arrival order with
    /// the pickups and bag moves. A world without the economy tables (or without the item tables, for sales and purchases)
    /// refuses them with <c>InteractionUnavailable</c>. False when <paramref name="request"/> is none of them.
    /// </summary>
    private bool HandleOutpostRequest(Player player, in Net.ClientRequest request)
    {
        var answers = player.Outposts;
        switch (request.Kind)
        {
            case Shared.Protocol.ReliableMessageId.OutpostInteractRequest:
                if (Outposts != null)
                    Outposts.Handle(player, request);
                else
                    answers.Interactions.Add(new Shared.Protocol.OutpostInteractResult
                    {
                        RequestId = request.RequestId, NpcEntityId = request.DropEntityId, Kind = request.Interaction,
                        Reason = Shared.Protocol.OutpostReason.InteractionUnavailable,
                    });
                return true;
            case Shared.Protocol.ReliableMessageId.SellQuoteRequest:
                if (_sales != null)
                    _sales.Quote(player, request);
                else
                    answers.Quotes.Add(new Shared.Protocol.SellQuoteResult
                    {
                        RequestId = request.RequestId, Reason = Shared.Protocol.OutpostReason.InteractionUnavailable,
                    });
                return true;
            case Shared.Protocol.ReliableMessageId.SellRequest:
                if (_sales != null)
                    _sales.Sell(player, request);
                else
                    answers.SaleRefusals.Add(new Shared.Protocol.SellResult
                    {
                        RequestId = request.RequestId, Reason = Shared.Protocol.OutpostReason.InteractionUnavailable,
                    });
                return true;
            case Shared.Protocol.ReliableMessageId.VendorBuyRequest:
                if (_vendor != null)
                    _vendor.Buy(player, request);
                else
                    answers.PurchaseRefusals.Add(new Shared.Protocol.VendorBuyResult
                    {
                        RequestId = request.RequestId, Reason = Shared.Protocol.OutpostReason.InteractionUnavailable,
                    });
                return true;
            default:
                return false;
        }
    }

    /// <summary>Players with a client: the active players that normalize the demand loop (R-4.2-02, R-4.2-03).</summary>
    private int ConnectedPlayers()
    {
        int count = 0;
        foreach (var player in _playersByPeer.Values)
        {
            if (player.IsConnected)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Phase 1, after the pickups and bag moves: every player's queued chat lines to the <see cref="ChatService"/> (R-6.5-01), players in
    /// entity ID order and each player's in arrival order. Lingering players have none; a world without chat settings
    /// queues none (the router refuses them).
    /// </summary>
    private void HandleChat()
    {
        if (_chat == null)
            return;
        long now = _clock.UtcNowMs;
        foreach (var entity in _entities)
        {
            if (entity is not Player player)
                continue;
            var lines = player.Inputs.ChatRequests;
            while (lines.TryDequeue(out var line))
                _chat.Submit(player, line.Text ?? string.Empty, now);
        }
    }

    /// <summary>Phase 10, every tick: accepted lines to everyone who sees their sender, refusals to their sender.</summary>
    private void SendChat()
    {
        if (_chat != null)
            _chatSender.Send(_chat);
    }

    /// <summary>Phase 10: pickup, bag move and outpost answers, committed bags and gold to connected players (backlog G, WP-M2-4).</summary>
    private void SendInventory()
    {
        foreach (var entity in _entities)
        {
            if (entity is not Player player)
                continue;
            if (player.IsConnected)
            {
                // WP-M2-4: outpost answers and committed sales before the contents that show them, the gold after.
                _outpostSender.SendAnswers(player, _inventory);
                _inventorySender.Send(player, _inventory);
                _outpostSender.SendGold(player, _inventory);
            }
            else
                player.ClearInventoryAnswers();
        }
    }

    /// <summary>A save committed a character's inventory changes (rule 5): now its client may see them.</summary>
    private void OnInventoryCommitted(long characterId)
    {
        if (_playersByCharacter.TryGetValue(characterId, out var player))
        {
            player.InventoryDirty = true;
            player.Outposts.GoldDirty = true; // the batch may have changed the gold too (WP-M2-4)
        }
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
