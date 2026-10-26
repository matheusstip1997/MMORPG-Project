using BotClient.Brain;
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
    private readonly HitEntry[] _hits = new HitEntry[HitMessage.MaxHits];
    private readonly BotPerception _view = new();
    private readonly Dictionary<ushort, int> _kitReachByNetId = new();
    private readonly Dictionary<int, string> _classByNetId;
    private readonly OwnLoot _ownLoot;
    private BotBrain? _brain;
    private BotKit? _kit;
    private uint _lastOwnActionStart;
    private bool _wasDead;
    private bool _stopping;
    private double _nowMs;

    public BotWorldClient(int index, BotData data, ActionCatalog catalog, Func<string, BotBrain> brainForClass)
    {
        Index = index;
        _data = data;
        _catalog = catalog;
        _brainForClass = brainForClass;
        _settings = PredictionSettings.From(data.Tables);
        _predictor = new LocalPlayerPredictor(_settings, data.Grid, catalog);
        _ticks = new TickClock(_settings);
        _packets = new InputPacketBuilder(_settings.InputRedundancy);
        _packetBuffer = new byte[_packets.MaxPacketSize];
        _registry = new EntityRegistry(_settings);
        _interpolation = new InterpolationClock(_settings);
        _inventory = new ClientInventory(data.Tables);
        _classByNetId = data.Rows<ClassDef>().ToDictionary(c => c.NetId, c => c.Id);
        _ownLoot = OwnLoot.From(data.Tables);
        var limits = data.Tables.Single<NetSecurity>();
        Connection = new ClientConnection(this, new ClientNetLimits(limits.MaxReliableMessageBytes, limits.MaxUnreliableMessageBytes));
    }

    public int Index { get; }

    public ClientConnection Connection { get; }

    public BotCounters Counters { get; } = new();

    public BotBrain? Brain => _brain;

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
        if (_brain != null)
            command = _brain.Think(Perceive());

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
        return view;
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
        _ticks.Reset();
        _packets.Reset();
    }

    public void OnReliableReceived(ReadOnlySpan<byte> message)
    {
        var id = ReliableMessages.PeekId(message);
        if (id == ReliableMessageId.PickupResult || id == ReliableMessageId.InventoryContents)
        {
            _inventory.Apply(message, out _);
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
            return;
        }
        if (id == ReliableMessageId.ActionResult
            && ActionResult.TryRead(message, out _, out ushort actionId, out _, out ActionReason reason))
        {
            if (RefusalReasons.Contains(reason))
            {
                Counters.Refusals[reason.ToString()] = Counters.Refusals.GetValueOrDefault(reason.ToString()) + 1;
                _brain?.OnRefused(actionId, reason, (long)_nowMs);
            }
        }
        // EntityIdentity (names) and ids added later: nothing for a bot to do.
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
        if (!hasOwn)
            return;

        _predictor.EntityId = own.EntityId;
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

        if (state < ActionState.Startup || state > ActionState.Recovery || !_kitReachByNetId.TryGetValue(own.ActionId, out int reach))
            return;
        uint start = unchecked(serverTick - own.StateAge);
        if (start == _lastOwnActionStart)
            return;
        _lastOwnActionStart = start;
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
        _predictor.SetLoadout(_catalog.Loadout(classId));
        _kit = BotKit.For(_data.Tables, classId);
        foreach (var action in _kit.Actions.Where(a => a.Damages))
            _kitReachByNetId[(ushort)action.Def.NetId] = action.ReachCm;
        _brain = _brainForClass(classId);
        _view.Ready = Ready;
    }

    private void OnHits(ReadOnlySpan<byte> message)
    {
        if (!HitMessage.TryRead(message, out _, _hits, out int count) || _registry.OwnEntityId is not uint self)
            return;
        for (int i = 0; i < count; i++)
        {
            var hit = _hits[i];
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
