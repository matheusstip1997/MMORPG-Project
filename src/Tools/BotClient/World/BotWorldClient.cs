using BotClient.Brain;
using BotClient.Loop;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Net;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace BotClient.World;

/// <summary>A bot's counters, for the run summary and the pass criteria (spec 11.5).</summary>
public sealed class BotCounters
{
    public bool Connected { get; set; }
    public bool EnteredWorld { get; set; }
    public long EnteredAtMs { get; set; }
    public bool Disconnected { get; set; }
    public string DisconnectReason { get; set; } = "";
    public long ActionsStarted { get; set; }
    public bool InCombat { get; set; }
    public long FirstCombatAtMs { get; set; }
    public long HitsLanded { get; set; }
    public long Kills { get; set; }
    public long HitsTaken { get; set; }
    public long DodgedHits { get; set; }
    public long Deaths { get; set; }
    public long PickupsOk { get; set; }
    public long PickupsRefused { get; set; }
    public Dictionary<string, long> Refusals { get; } = new(StringComparer.Ordinal);
    public long Corrections { get; set; }
    public long ActionMismatches { get; set; }
    public long UnknownAcks { get; set; }
    public long Snapshots { get; set; }
    public long FramesSent { get; set; }

    /// <summary>R-3.1-10: hits 2 to 4 of a normal attack chain that the server ran (the own snapshot's action id).</summary>
    public long ChainHitsStarted { get; set; }

    /// <summary>Chain hits that started before this bot's first kill: on a target still alive, not after a hit 1 that killed.</summary>
    public long ChainHitsBeforeFirstKill { get; set; }

    /// <summary>Rounds of the M2 loop played to the end (WP-M2-12).</summary>
    public long LoopRounds { get; set; }

    /// <summary>The loop's counters: per step and status (<c>fight.done</c>, <c>sell.notAvailable</c>) and the steps' own.</summary>
    public Dictionary<string, long> Loop { get; } = new(StringComparer.Ordinal);

    public void AddLoop(string name, long amount = 1) => Loop[name] = Loop.GetValueOrDefault(name) + amount;
}

/// <summary>
/// One bot in the world (backlog J): the Unity client without rendering, from the same engine-free sources
/// (<c>ClientCore</c>): <see cref="ClientConnection"/>, <see cref="LocalPlayerPredictor"/>, <see cref="InputPacketBuilder"/>,
/// <see cref="TickClock"/>, <see cref="EntityRegistry"/>, <see cref="InterpolationClock"/>, <see cref="ClientInventory"/>,
/// wired the way <c>PredictionDevClient</c> wires them. The class kit comes from the own spawn's <c>ClassDef</c> net id;
/// the view tick and its fraction from the interpolation clock (R-3.1-03). Inputs go through the predictor exactly as a
/// player's do, so the server sees nothing a real client would not send. Not thread safe: one thread drives it.
/// </summary>
public sealed class BotWorldClient : IClientConnectionListener, IDisposable
{
    private static readonly HashSet<ActionReason> RefusalReasons =
    [
        ActionReason.OnCooldown, ActionReason.SkillNotLearned, ActionReason.NoStamina, ActionReason.DodgeNoCharge,
        ActionReason.ParryNoCharge, ActionReason.NoMp,
    ];

    private readonly BotData _data;
    private readonly ActionCatalog _catalog;
    private readonly PredictionSettings _settings;
    private readonly Func<string, BotBrain> _brainForClass;
    private readonly LocalPlayerPredictor _predictor;
    private readonly TickClock _ticks;
    private readonly InputPacketBuilder _packets;
    private readonly byte[] _packetBuffer;
    private readonly EntityRegistry _registry;
    private readonly InterpolationClock _interpolation;
    private readonly ClientInventory _inventory;
    private readonly ClientProgression _progression = new(); // WP-M2-3
    private readonly ClientOutposts _outposts; // WP-M2-4: NPCs, sale and vendor answers, committed gold
    private readonly ClientSkillTree _skillTree; // WP-M2-5: learned skills, hotbar and the predicted loadout
    private readonly ClientQuests _quests = new(); // WP-M2-6: the quest board, run answers and the run (RunStatus at enter world)
    private readonly ClientJobs _jobs = new(); // WP-M3-1: the character's jobs and pick answers (JobState at enter world)
    private readonly ClientForge _forge = new(); // WP-M3-6: the forge session and finish answers
    private readonly ClientEnchant _enchant = new(); // WP-M3-8: enchant answers
    private readonly ClientConsumables _consumables; // WP-M3-3: item use requests and answers
    private readonly ClientHarvest _harvest; // WP-M3-5: harvest requests and answers
    private readonly ClientNodes _nodes; // WP-M3-4: node types, for the gather step
    private readonly HitEntry[] _hits = new HitEntry[HitMessage.MaxHits];
    private readonly BotPerception _view = new();
    private readonly Dictionary<ushort, int> _kitReachByNetId = new();
    private readonly Dictionary<int, string> _classByNetId;
    private readonly OwnLoot _ownLoot;
    private readonly BotStepRegistry _steps;
    private readonly IReadOnlyList<IBotOwnReader> _ownReaders;
    private readonly HashSet<int> _chainHitNetIds = [];
    private readonly Action<string> _log;
    private BotBrain? _brain;
    private BotLoop? _loop;
    private BotStepContext? _stepContext;
    private BotKit? _kit;
    private uint _lastOwnActionStart;
    private bool _wasDead;
    private int? _homeLevel;
    private bool _stopping;
    private double _nowMs;

    /// <param name="steps">The run's loop steps and own readers (WP-M2-12); the default registry when null.</param>
    public BotWorldClient(int index, BotData data, ActionCatalog catalog, Func<string, BotBrain> brainForClass,
        BotStepRegistry? steps = null, Action<string>? log = null)
    {
        Index = index;
        _data = data;
        _forge.Rules = Shared.Simulation.Crafting.ForgeRules.From(data.Tables); // WP-M3-6: the input picker of the forge steps
        _enchant.Rules = Shared.Simulation.Crafting.EnchantRules.From(data.Tables); // WP-M3-8: the enchant step
        _forge.NowMs = () => (long)_nowMs; // WP-M3-10: the forge timing bar runs on the bot's clock
        _catalog = catalog;
        _brainForClass = brainForClass;
        _steps = steps ?? BotStepRegistry.Default();
        _ownReaders = _steps.NewOwnReaders();
        _log = log ?? (_ => { });
        // R-3.1-10: hits 2 to 4 are the actions some hit names as its next one.
        for (int i = 0; i < catalog.Count; i++)
        {
            if (catalog[i].ChainNextIndex >= 0)
                _chainHitNetIds.Add(catalog[catalog[i].ChainNextIndex].NetId);
        }
        _settings = PredictionSettings.From(data.Tables);
        _predictor = new LocalPlayerPredictor(_settings, data.Grid, catalog);
        _ticks = new TickClock(_settings);
        _packets = new InputPacketBuilder(_settings.InputRedundancy);
        _packetBuffer = new byte[_packets.MaxPacketSize];
        _registry = new EntityRegistry(_settings);
        // R-2.2-07: bots predict the monsters' bodies like the client does.
        _predictor.UseBodies(new RegistryBodyBlockers(_registry, BodyRadii.From(data.Tables)));
        // R-2.3-06, R-12.3-01: the level gates, with the own level of ProgressionState (RunTick).
        _predictor.UseGates(data.Gates);
        _interpolation = new InterpolationClock(_settings);
        _inventory = new ClientInventory(data.Tables);
        _outposts = new ClientOutposts(data.Tables);
        _skillTree = new ClientSkillTree(data.Tables);
        _consumables = new ClientConsumables(data.Tables);
        _harvest = new ClientHarvest(data.Tables);
        _jobs.Rules = Shared.Simulation.Jobs.JobRules.From(data.Tables); // the JobDef ids of the own jobs (IOwnJobs.Has, the gather step)
        _nodes = new ClientNodes(data.Tables);
        _classByNetId = data.Rows<ClassDef>().ToDictionary(c => c.NetId, c => c.Id);
        _ownLoot = OwnLoot.From(data.Tables);
        var limits = data.Tables.Single<NetSecurity>();
        Connection = new ClientConnection(this, new ClientNetLimits(limits.MaxReliableMessageBytes, limits.MaxUnreliableMessageBytes));
    }

    public int Index { get; }

    public ClientConnection Connection { get; }

    public BotCounters Counters { get; } = new();

    /// <summary>The bot's own level, exp and rank as the server last sent them (WP-M2-3); null before the first message.</summary>
    public ProgressionState? Progression => _progression.State;

    public BotBrain? Brain => _brain;

    /// <summary>The M2 loop when the behaviour has <c>loopSteps</c>, from the first own spawn on; null for the M1 brain alone.</summary>
    public BotLoop? Loop => _loop;

    /// <summary>Own level, exp and gold as far as the protocol tells (WP-M2-12 level report).</summary>
    public BotOwnState Own { get; } = new();

    /// <summary>The class of the own spawn (the character's), empty until the first spawn named it.</summary>
    public string ClassId { get; private set; } = "";

    public uint? EntityId => _registry.OwnEntityId;

    public string CharacterName { get; set; } = "";

    /// <summary>Called before a planned disconnect so it is not counted as lost.</summary>
    public void Stop()
    {
        _stopping = true;
        Connection.Disconnect();
    }

    /// <summary>One frame of the bot's loop at <paramref name="nowMs"/> (monotonic): receive, then one input per client tick.</summary>
    public void Update(double nowMs, double deltaMs)
    {
        _nowMs = nowMs;
        Connection.PollEvents();
        if (_predictor.HasState && Connection.State == ConnectionState.Connected)
        {
            int ticks = _ticks.Advance(deltaMs);
            for (int i = 0; i < ticks; i++)
                RunTick();
        }
        _interpolation.Advance(nowMs, deltaMs);
        if (_interpolation.HasTime)
            _registry.Update(_interpolation.RenderTick);
    }

    private void RunTick()
    {
        var command = new BotCommand();
        if (Own.Level is { } level && level != _predictor.Level)
            _predictor.SetLevel(level); // the tutorial gate opens at the server's level, as on the client
        if (_brain != null)
        {
            // Paths keep to the same gate the prediction and the server apply (no planning through gated cells).
            _brain.Gate = new Shared.World.MoverGate(_data.Gates, _predictor.Level);
            if (_brain.Behaviour.HomesByLevel && Own.Level != _homeLevel)
            {
                // WP-M2-7: a camp of the bot's level, chosen again at each level-up.
                _homeLevel = Own.Level;
                _brain.SetHome(_data.HomeFor(_brain.Behaviour, Index, Own.Level));
            }
            var view = Perceive();
            command = _loop != null ? _loop.Tick(_stepContext!) : _brain.Think(view);
        }

        double renderTick = _interpolation.HasTime ? Math.Max(0.0, _interpolation.RenderTick) : 0.0;
        uint viewTick = (uint)Math.Floor(renderTick);
        byte viewSubTick = (byte)Math.Min(63.0, Math.Floor((renderTick - viewTick) * 64.0));
        var frame = _predictor.Predict(command.Keys, command.Facing, command.Actions, command.SkillSlot, viewTick, viewSubTick);
        int size = _packets.Write(frame, _packetBuffer);
        if (Connection.SendInput(new ReadOnlySpan<byte>(_packetBuffer, 0, size)))
            Counters.FramesSent++;
        if (command.PickupDropId != 0)
            Connection.SendReliable(ReliableMessages.Write(ReliableMessageId.PickupRequest, new PickupRequest { DropEntityId = command.PickupDropId }));
    }

    private BotPerception Perceive()
    {
        var view = _view;
        view.NowMs = (long)_nowMs;
        view.Self = _predictor.State.Position;
        view.HasResources = _registry.HasOwnResources;
        view.Own = _registry.OwnResources;
        view.Dead = _predictor.Actions.State == ActionState.Dead || (_registry.HasOwnResources && _registry.OwnResources.Hp <= 0);
        view.Monsters.Clear();
        view.Drops.Clear();
        foreach (var entity in _registry.EntityValues)
        {
            if (entity.Kind != EntityKind.Monster && entity.Kind != EntityKind.LootDrop)
                continue;
            if (!entity.Samples.TryNewest(out var sample))
                continue;
            // Snapshots are Y-up: the ground plane is X and Z (SnapshotAxes).
            var position = Shared.Simulation.FixVec2.FromCm(sample.XCm, sample.ZCm);
            // QA wave 6 O3: only drops still in the newest snapshot (OwnStatusesTick is that snapshot's tick; a drop gone
            // from it was taken or expired) and locked to this bot (OwnLoot); other drops would only be refused.
            if (entity.Kind == EntityKind.LootDrop && (sample.Tick != _registry.OwnStatusesTick || !_ownLoot.IsOwn(position, view.NowMs)))
                continue;
            var seen = new SeenEntity(entity.Id, entity.Kind, entity.DefId, position,
                entity.ActionState != ActionState.Dead && entity.HpBucket > 0, entity.ActionState, entity.ActionNetId,
                entity.ActionStartTick);
            (entity.Kind == EntityKind.Monster ? view.Monsters : view.Drops).Add(seen);
        }
        view.Monsters.Sort((a, b) => a.Id.CompareTo(b.Id));
        view.Drops.Sort((a, b) => a.Id.CompareTo(b.Id));
        view.Chain = Chain(_predictor.Actions);
        view.ActionIdle = !_predictor.Actions.IsBusy;
        return view;
    }

    /// <summary>
    /// R-3.1-10: the predicted machine's running hit that has a next hit. The server takes a chain press anywhere from
    /// <c>ChainFromTick</c> to the hit's end, but its machine steps once per server tick, not per input frame: input
    /// catch-up (two queued frames, one step) puts it behind the prediction, ticks without a frame ahead of it. So the bot
    /// presses when the window opens (up to <c>total - 2 - chainFrom</c> ticks of room ahead, and in before a kill by this
    /// hit is known) and again for the link tick (<c>link - chainFrom</c> ticks of room behind); a press is handled after
    /// the next frame's step, hence the <c>+ 1</c>. When the first press was taken the second lands in the next hit before
    /// its own window and is dropped. A snapshot that corrects the elapsed ticks moves <see cref="OwnChain.StartTick"/>, so
    /// the bot presses again after it.
    /// </summary>
    internal static OwnChain Chain(ActionMachineState actions)
    {
        var running = actions.Action;
        if (running == null || running.ChainNextIndex < 0 || actions.State < ActionState.Startup || actions.State > ActionState.Recovery)
            return default;
        int pressedAt = actions.Elapsed + 1;
        return new OwnChain(true, unchecked(actions.Tick - (uint)actions.Elapsed), actions.Elapsed >= running.ChainFromTick,
            pressedAt >= running.ChainLinkTick && pressedAt < running.TotalTicks, actions.Catalog[running.ChainNextIndex].StaminaCost);
    }

    /// <summary>The predicted machine is idle and the kit action off cooldown (what QA's PredictedReady checks).</summary>
    private bool Ready(KitAction action)
    {
        if (_predictor.Actions.IsBusy)
            return false;
        var loadout = _predictor.Loadout;
        int index = action.Slot == 0 ? loadout.AttackIndex : loadout.SlotIndex(action.Slot);
        return index >= 0 && _predictor.Actions.CooldownTicks(index) == 0;
    }

    public void OnConnected() => Counters.Connected = true;

    public void OnDisconnected(ClientDisconnectReason reason, ConnectRejectReason rejectReason, ushort serverProtocolVersion)
    {
        if (!_stopping)
        {
            Counters.Disconnected = true;
            // A refusal or a server kick names the server's reason code (spec 9.10), for example "ClosedByServer:Kicked".
            Counters.DisconnectReason = reason == ClientDisconnectReason.Rejected
                || (reason == ClientDisconnectReason.ClosedByServer && rejectReason != ConnectRejectReason.None)
                ? $"{reason}:{rejectReason}"
                : reason.ToString();
        }
        _predictor.Reset();
        _registry.Clear();
        _interpolation.Reset();
        _skillTree.Reset(); // a new connection gets its state and starts its request ids again (WP-M2-5)
        _jobs.Reset(); // and its jobs (WP-M3-1)
        _forge.Reset(); // and its forge session (WP-M3-6)
        _enchant.Reset(); // and its enchant answers (WP-M3-8)
        _consumables.Reset(); // and its use request ids (WP-M3-3)
        _harvest.Reset(); // and its harvest request ids (WP-M3-5)
        _ticks.Reset();
        _packets.Reset();
    }

    public void OnReliableReceived(ReadOnlySpan<byte> message)
    {
        var id = ReliableMessages.PeekId(message);
        // The client's kept state, from the first message on (enter world sends the bag, the gold and the progression before
        // the own spawn creates the loop): the loop steps read it, not only the messages they see (QA M2 wave 0 F2).
        if (id == ReliableMessageId.ProgressionState || id == ReliableMessageId.AttributeSpendResult)
            _progression.Apply(message, out _); // own level, exp, rank and attribute points and answers (WP-M2-3, WP-M2-13); OwnProgressReader below fills Own
        else if (_skillTree.Apply(message, out _))
        {
            // The committed skills and hotbar (WP-M2-5): the predicted loadout, as PredictionDevClient builds it.
            if (_skillTree.Loadout(_catalog) is { } learned)
                _predictor.SetLoadout(learned);
        }
        else if (_outposts.Apply(message, out _))
        {
            // outpost, sale and vendor answers and the committed gold (WP-M2-4); OwnGoldReader below fills Own.Gold
        }
        else if (_consumables.Apply(message, out _))
        {
            // item use answers (WP-M3-3), read by the loop's useItem step
        }
        else if (_harvest.Apply(message, out _))
        {
            // harvest answers (WP-M3-5), read by the loop's gather step
        }
        else if (_quests.Apply(message, out _))
        {
            // quest boards, run answers and the run (WP-M2-6), read by the loop's run step
        }
        else if (_jobs.Apply(message, out _))
        {
            // the jobs and pick answers (WP-M3-1), read by the loop's takeJob step and the job steps
        }
        else if (_forge.Apply(message, out _))
        {
            // the forge session and its results (WP-M3-6), read by the refine, craft and convert steps
        }
        else if (_enchant.Apply(message, out _))
        {
            // the enchant answers (WP-M3-8), read by the enchant step
        }
        else if (id == ReliableMessageId.InventoryMoveResult)
            _inventory.Apply(message, out _); // equip answers (WP-M2-1)
        else if (id == ReliableMessageId.PickupResult || id == ReliableMessageId.InventoryContents)
        {
            _inventory.Apply(message, out _);
            if (id == ReliableMessageId.InventoryContents)
                _predictor.SetEquippedWeapons(_skillTree.EquippedWeapons(_inventory.Contents)); // R-3.5-07, WP-M2-5
            if (id == ReliableMessageId.PickupResult && _inventory.LastResult is { } result)
            {
                if (result.Reason == PickupReason.None)
                    Counters.PickupsOk++;
                else
                {
                    Counters.PickupsRefused++;
                    string key = "Pickup" + result.Reason;
                    Counters.Refusals[key] = Counters.Refusals.GetValueOrDefault(key) + 1;
                }
            }
        }
        else if (id == ReliableMessageId.ActionResult
            && ActionResult.TryRead(message, out _, out ushort actionId, out _, out ActionReason reason))
        {
            if (RefusalReasons.Contains(reason))
            {
                Counters.Refusals[reason.ToString()] = Counters.Refusals.GetValueOrDefault(reason.ToString()) + 1;
                _brain?.OnRefused(actionId, reason, (long)_nowMs);
            }
        }
        // EntityIdentity (names) and ids added later: the own readers and the loop steps (WP-M2-12) see every message.
        foreach (var reader in _ownReaders)
            reader.OnReliable(id, message, Own);
        if (_loop != null)
            _loop.OnReliable(_stepContext!, id, message);
    }

    public void OnUnreliableReceived(ReadOnlySpan<byte> message)
    {
        HotMessage.TryPeekId(message, out var id);
        if (id == HotMessageId.Hit)
        {
            OnHits(message);
            return;
        }
        if (id != HotMessageId.Snapshot)
            return; // projectiles and ground areas: drawn only; the snapshots carry the state
        if (!_registry.ApplySnapshot(message, out var header, out var own, out bool hasOwn))
            return;
        Counters.Snapshots++;
        _interpolation.OnSnapshot(header.ServerTick, _nowMs);
        foreach (var reader in _ownReaders)
            reader.OnSnapshot(_registry, Own);
        if (!hasOwn)
            return;

        _predictor.EntityId = own.EntityId;
        _view.Confirmed = Game.Gameplay.Entities.SnapshotAxes.GroundState(own).Position;
        ApplyOwnClass();
        var result = _predictor.Reconcile(header, Game.Gameplay.Entities.SnapshotAxes.GroundState(own), ActionWireState.From(own));
        switch (result.Outcome)
        {
            case ReconcileOutcome.Initialized when !Counters.EnteredWorld:
                Counters.EnteredWorld = true;
                Counters.EnteredAtMs = (long)_nowMs;
                break;
            case ReconcileOutcome.Corrected:
                Counters.Corrections++;
                if (result.ActionMismatch)
                    Counters.ActionMismatches++;
                break;
            case ReconcileOutcome.UnknownAck:
                Counters.UnknownAcks++;
                break;
        }
        NoticeOwnState(header.ServerTick, own);
    }

    /// <summary>
    /// The server's own-entity state: an attack of the kit that the server started counts once, and makes the bot "in
    /// combat" when its target lives within that action's reach (the load test's criterion); a death is counted once.
    /// </summary>
    private void NoticeOwnState(uint serverTick, in EntitySnapshot own)
    {
        var state = (ActionState)own.State;
        bool dead = state == ActionState.Dead;
        if (dead && !_wasDead)
            Counters.Deaths++;
        _wasDead = dead;

        if (state < ActionState.Startup || state > ActionState.Recovery)
            return;
        bool chainHit = _chainHitNetIds.Contains(own.ActionId);
        int reach = 0;
        if (!chainHit && !_kitReachByNetId.TryGetValue(own.ActionId, out reach))
            return;
        uint start = unchecked(serverTick - own.StateAge);
        if (start == _lastOwnActionStart)
            return;
        _lastOwnActionStart = start;
        if (chainHit)
        {
            Counters.ChainHitsStarted++; // R-3.1-10: hits 2 to 4 (not kit actions of their own)
            if (Counters.Kills == 0)
                Counters.ChainHitsBeforeFirstKill++;
            return;
        }
        Counters.ActionsStarted++;
        if (Counters.InCombat || _brain == null || _brain.TargetId == 0)
            return;
        foreach (var monster in _view.Monsters)
        {
            if (monster.Id == _brain.TargetId && monster.Alive
                && GridPath.DistanceCm(_predictor.State.Position, monster.Position) <= reach)
            {
                Counters.InCombat = true;
                Counters.FirstCombatAtMs = (long)_nowMs;
            }
        }
    }

    /// <summary>What PredictionDevClient.ApplyOwnClass does: the own spawn's ClassDef net id picks the kit.</summary>
    private void ApplyOwnClass()
    {
        if (_registry.OwnDefId == 0 || _brain != null || !_classByNetId.TryGetValue(_registry.OwnDefId, out var classId))
            return;
        ClassId = classId;
        // WP-M2-5: the learned skills' loadout when the skill tree came first, else the class's bindings until it does.
        _predictor.SetLoadout(_skillTree.Loadout(_catalog) ?? _catalog.Loadout(classId));
        if (_data.Rows<ClassDef>().FirstOrDefault(c => c.Id == classId) is { } classDef)
            _predictor.OwnBodyRadiusRaw = Shared.Simulation.FixedPoint.FromCm(classDef.BodyRadiusCm);
        _kit = BotKit.For(_data.Tables, classId);
        foreach (var action in _kit.Actions.Where(a => a.Damages))
            _kitReachByNetId[(ushort)action.Def.NetId] = action.ReachCm;
        _brain = _brainForClass(classId);
        _view.Ready = Ready;
        if (_brain.Behaviour.LoopSteps.Length > 0)
        {
            _loop = _steps.NewLoop(_brain.Behaviour);
            _stepContext = new BotStepContext(Index, _data, _brain, _view, _registry, _inventory, Own, Counters, _steps,
                message => Connection.SendReliable(message), _log)
            {
                ClassId = classId, Outposts = _outposts, Progression = _progression, SkillTree = _skillTree, Quests = _quests, Jobs = _jobs,
                Consumables = _consumables, Forge = _forge, Enchant = _enchant,
                Harvest = _harvest, Nodes = _nodes,
                PlayAction = id => { if (_catalog.Find(id) is { } action) _predictor.RequestAction(action.Index); },
            };
        }
    }

    private void OnHits(ReadOnlySpan<byte> message)
    {
        if (!HitMessage.TryRead(message, out _, _hits, out int count) || _registry.OwnEntityId is not uint self)
            return;
        for (int i = 0; i < count; i++)
        {
            var hit = _hits[i];
            if ((hit.AttackerId == self || hit.TargetId == self) && hit.Kind != HitKind.Healed)
                _view.LastCombatAtMs = (long)_nowMs; // spec 12.7 combat state, as the server marks it (HitResolver)
            if (hit.AttackerId == self)
            {
                Counters.HitsLanded++;
                if (hit.Kind == HitKind.Killed)
                {
                    Counters.Kills++;
                    _view.LastKillAtMs = (long)_nowMs;
                    // O3: its drops are locked to this bot; they appear where it died.
                    if (_registry.Entities.TryGetValue(hit.TargetId, out var dead) && dead.Samples.TryNewest(out var last))
                        _ownLoot.NoteKill(Shared.Simulation.FixVec2.FromCm(last.XCm, last.ZCm), (long)_nowMs);
                }
            }
            else if (hit.TargetId == self)
            {
                if (hit.Kind == HitKind.Dodged)
                    Counters.DodgedHits++;
                else
                    Counters.HitsTaken++;
            }
        }
    }

    public void Dispose() => Connection.Dispose();
}
