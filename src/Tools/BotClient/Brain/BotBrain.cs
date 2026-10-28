using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Util;
using Shared.World;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace BotClient.Brain;

/// <summary>An entity a bot sees, from the newest snapshot that listed it.</summary>
public readonly record struct SeenEntity(
    uint Id,
    EntityKind Kind,
    ushort DefId,
    FixVec2 Position,
    bool Alive,
    ActionState State,
    ushort ActionNetId,
    uint ActionStartTick);

/// <summary>What the bot knows this tick: its predicted self, its meters, and what it sees. Filled by the world client.</summary>
public sealed class BotPerception
{
    public long NowMs { get; set; }

    /// <summary>Predicted position (raw fixed point, ground plane).</summary>
    public FixVec2 Self { get; set; }

    public bool Dead { get; set; }

    /// <summary>When the bot last killed a monster (its own hit with <c>HitKind.Killed</c>), <see cref="long.MinValue"/> for never.</summary>
    public long LastKillAtMs { get; set; } = long.MinValue / 2;

    public bool HasResources { get; set; }

    public OwnResources Own { get; set; }

    /// <summary>Living and dead monsters in interest, by entity id.</summary>
    public List<SeenEntity> Monsters { get; } = [];

    /// <summary>Loot drops in interest, by entity id.</summary>
    public List<SeenEntity> Drops { get; } = [];

    /// <summary>The predicted machine is idle and the action is off cooldown (the client's own prediction, not the server's).</summary>
    public Func<KitAction, bool> Ready { get; set; } = _ => false;

    /// <summary>R-3.1-10: the predicted machine's running attack hit that has a next hit; default when there is none.</summary>
    public OwnChain Chain { get; set; }

    /// <summary>The own position of the newest snapshot (the server's, ground plane): where range checks see the bot.</summary>
    public FixVec2 Confirmed { get; set; }

    /// <summary>
    /// When the bot last hit or was hit (any own <c>Hit</c> entry, the moments the server marks a player in combat), so a
    /// step can tell the combat state of spec 12.7 (<c>StartingValuesM1.combatStateMs</c>); <see cref="long.MinValue"/> / 2 for never.
    /// </summary>
    public long LastCombatAtMs { get; set; } = long.MinValue / 2;

    /// <summary>The predicted action machine is idle (no action, not dead): gear may change (spec 4.1 M2 item model).</summary>
    public bool ActionIdle { get; set; }
}

/// <summary>
/// A running attack hit with a next hit (R-3.1-10), from the predicted machine: <see cref="StartTick"/> tells one hit from
/// the next (machine ticks), <see cref="WindowOpen"/> says an attack press now asks for the next hit
/// (<c>ActionTiming.ChainFromTick</c>; the machine starts it at the link tick), <see cref="AtLink"/> says a press now
/// lands on the link tick (the second press, <c>BotWorldClient.Chain</c> says why), <see cref="NextStaminaCost"/> is what
/// the next hit costs.
/// </summary>
public readonly record struct OwnChain(bool Running, uint StartTick, bool WindowOpen, bool AtLink, int NextStaminaCost);

/// <summary>What <see cref="BotBrain.Think"/> may do: all of it (the M1 bot), or one loop step's part.</summary>
public enum BotBrainMode : byte
{
    /// <summary>Fight, loot its own kills first, walk home: the M1 bot (<c>field-combat</c>).</summary>
    All,
    /// <summary>The loop's fight step: no looting.</summary>
    Fight,
    /// <summary>The loop's pickup step: only its own drops (it still dodges); otherwise it stands.</summary>
    Loot,
}

/// <summary>One tick of bot input: what a player would hold and press.</summary>
public struct BotCommand
{
    public MoveKeys Keys;
    public ushort Facing;
    public InputActions Actions;
    public byte SkillSlot;

    /// <summary>A loot drop to ask for on the reliable channel this tick, 0 for none.</summary>
    public uint PickupDropId;
}

public enum BotActivity : byte
{
    Waiting,
    Dead,
    WalkingHome,
    Approaching,
    Fighting,
    BackingOff,
    Looting,
}

/// <summary>
/// A bot's decisions (backlog J, <see cref="BotBehaviour"/>): walk to its home spawn point, fight the nearest living
/// monster with its class kit (<see cref="BotKit"/>), back off when hurt until recovered (<see cref="BotKit.Recovered"/>),
/// dodge some monster attacks, pick up drops, and start over after a respawn. One <see cref="Think"/> per client tick.
/// Deterministic for the same perceptions: random choices (dodge rolls, reaction delays) come from its seeded
/// <see cref="IRng"/>, and entities are visited in id order.
/// </summary>
public sealed class BotBrain
{
    private const int ReplanMs = 2_000;
    private const int GoalMovedCm = 100;
    private const int HomeArrivedCm = 300;
    private const int LegArrivedCm = 100;

    private readonly BotKit _kit;
    private readonly BotBehaviour _behaviour;
    private readonly WorldGrid _grid;
    private MoverGate _gate;
    private FixVec2 _home;
    private readonly IRng _rng;
    private readonly int _pickupReachCm;
    private readonly IReadOnlyDictionary<int, int> _monsterAttackReachByNetId;
    private readonly IReadOnlyDictionary<int, int>? _contactCmByNetId;
    private readonly Dictionary<uint, uint> _attackStartsSeen = new();
    private readonly HashSet<uint> _dropsAsked = [];
    private readonly Dictionary<string, long> _heldUntil = new(StringComparer.Ordinal);
    private List<FixVec2>? _path;
    private FixVec2 _pathGoal;
    private long _plannedAtMs;
    private FixVec2 _progressFrom;
    private long _progressAtMs;
    private bool _backingOff;
    /// <summary>The back-off lasts at least until then (<c>backOffMs</c> from its start), recovered or not.</summary>
    private long _backOffMinUntilMs;
    /// <summary>The current leg ends then (<c>backOffMs</c> per leg).</summary>
    private long _backOffUntilMs;
    private FixVec2 _backOffTo;
    /// <summary>When it began to wait for <c>reengageAbovePercent</c> (a back-off, or a new target while hurt); -1 for not waiting.</summary>
    private long _recoverSinceMs = -1;
    private long _nextActionAtMs;
    private long _dodgeAtMs = -1;
    private FixVec2 _dodgeFrom;
    private ushort _facing;
    private uint? _chainPressedFor;
    private uint? _chainLinkPressedFor;

    /// <param name="monsterAttackReachByNetId">Reach of each monster attack by <c>ActionDef.netId</c> (the dodge trigger).</param>
    /// <param name="contactCmByMonsterNetId">R-2.2-07: the centre distance at which each monster's body (by <c>MonsterDef.netId</c>) stops this bot.</param>
    public BotBrain(BotKit kit, BotBehaviour behaviour, WorldGrid grid, FixVec2 home, int pickupRangeCm,
        IReadOnlyDictionary<int, int> monsterAttackReachByNetId, IRng rng,
        IReadOnlyDictionary<int, int>? contactCmByMonsterNetId = null)
    {
        _contactCmByNetId = contactCmByMonsterNetId;
        _kit = kit;
        _behaviour = behaviour;
        _grid = grid;
        _home = home;
        _rng = rng;
        _pickupReachCm = pickupRangeCm * behaviour.PickupReachPercent / 100;
        _monsterAttackReachByNetId = monsterAttackReachByNetId;
    }

    public BotBehaviour Behaviour => _behaviour;

    /// <summary>Where the bot waits for monsters between fights (its home spawn point).</summary>
    public FixVec2 Home => _home;

    /// <summary>
    /// Moves the bot's home (<c>BotBehaviour.homesByLevel</c>, WP-M2-7: a camp for its new level). The walk there replans on
    /// the next think, since its goal moved.
    /// </summary>
    public void SetHome(FixVec2 home) => _home = home;

    public BotActivity Activity { get; private set; }

    /// <summary>The monster the bot fights or walks to, 0 for none.</summary>
    public uint TargetId { get; private set; }

    public long DodgesPressed { get; private set; }

    /// <summary>Walks that made no progress for <c>stuckTimeoutMs</c> and planned again (a wall, or a monster's body: R-2.2-07).</summary>
    public long StuckReplans { get; private set; }

    public long ActionsPressed { get; private set; }

    public long PickupsAsked { get; private set; }

    public long BackOffs { get; private set; }

    /// <summary>
    /// The level gate the bot's paths keep to (R-2.3-06, the tutorial gate of R-12.3-01): the world's gates with the own
    /// level, set by the world client at each level change; a new gate drops the current path.
    /// </summary>
    public MoverGate Gate
    {
        get => _gate;
        set
        {
            if (value.Level == _gate.Level && ReferenceEquals(value.Gates, _gate.Gates))
                return;
            _gate = value;
            _path = null;
        }
    }

    public long Replans { get; private set; }

    /// <summary>Hits of the chain the bot pressed for (R-3.1-10), once per hit: the second press at the link tick is not counted.</summary>
    public long ChainPresses { get; private set; }

    /// <summary>The last <see cref="Think"/> in <see cref="BotBrainMode.Loot"/> found one of its own drops to take.</summary>
    public bool SawDrop { get; private set; }

    /// <summary>The server refused an action (spec 9.4 <c>ActionResult</c>): after OnCooldown it waits the cooldown out, as QA does.</summary>
    public void OnRefused(ushort actionNetId, ActionReason reason, long nowMs)
    {
        if (reason != ActionReason.OnCooldown)
            return;
        var action = _kit.Actions.FirstOrDefault(a => a.Def.NetId == actionNetId);
        if (action != null)
            _heldUntil[action.Id] = nowMs + action.Def.CooldownMs;
    }

    public BotCommand Think(BotPerception view, BotBrainMode mode = BotBrainMode.All)
    {
        var command = new BotCommand { Facing = _facing };
        SawDrop = false;
        if (view.Dead || !view.HasResources)
        {
            Activity = view.Dead ? BotActivity.Dead : BotActivity.Waiting;
            TargetId = 0;
            _path = null;
            _dodgeAtMs = -1;
            _backOffUntilMs = 0;
            _backOffMinUntilMs = 0;
            _backingOff = false;
            _recoverSinceMs = -1;
            return command;
        }

        // The QA kill step's recovery rule (QaFightPlan.Recovered): after a back-off it stays out until HP is back to
        // reengageAbovePercent or a self-heal is usable, and it starts no new fight below that, for backOffRecoverMs at most.
        long now = view.NowMs;
        bool recovered = _kit.Recovered(State(view, 0), _behaviour);
        if (recovered)
            _recoverSinceMs = -1;
        bool recoverTimeUp = _recoverSinceMs >= 0 && now - _recoverSinceMs >= _behaviour.BackOffRecoverMs;

        var target = mode == BotBrainMode.Loot ? null : PickTarget(view);
        bool holdOff = false;
        if (target is { } next && !recovered && !recoverTimeUp && (_recoverSinceMs >= 0 || next.Id != TargetId))
        {
            if (_recoverSinceMs < 0)
                _recoverSinceMs = now;
            holdOff = true;
            target = null;
        }
        else if (target != null && recoverTimeUp)
            _recoverSinceMs = -1; // fights on at the HP it has; the next back-off or new target starts a new wait
        TargetId = target?.Id ?? 0;
        NoticeAttacks(view);

        if (_dodgeAtMs >= 0 && view.NowMs >= _dodgeAtMs)
        {
            // Dodge away from the attacker, in the direction of the keys (R-3.3-02).
            _dodgeAtMs = -1;
            var away = view.Self - _dodgeFrom;
            command.Keys = GridPath.KeysToward(GridPath.Cm(away.X), GridPath.Cm(away.Y));
            command.Actions = InputActions.Dodge;
            DodgesPressed++;
            return command;
        }

        if (_backingOff)
        {
            // backOffMs is the shortest back-off; after it, it walks back once recovered or after backOffRecoverMs.
            if (now < _backOffMinUntilMs || (!recovered && !recoverTimeUp))
                return Recover(view, command);
            _backingOff = false;
        }

        // Its own kill's drop first (locked to the killer, R-4.3-02), then the fight, then any drop left around.
        var drop = mode == BotBrainMode.Fight ? null : PickDrop(view);
        if (drop is { } loot && (target == null || view.NowMs - view.LastKillAtMs <= _behaviour.LootAfterKillMs))
        {
            SawDrop = true;
            return Loot(view, loot, command);
        }
        if (mode == BotBrainMode.Loot)
        {
            Activity = BotActivity.Waiting;
            _path = null;
            return command;
        }

        if (holdOff)
            return Recover(view, command);
        if (target is { } monster)
            return Fight(view, monster, command);

        if (GridPath.DistanceCm(view.Self, _home) > HomeArrivedCm)
        {
            Activity = BotActivity.WalkingHome;
            return Walk(view, _home, command);
        }
        Activity = BotActivity.Waiting; // at home: the monster respawns here
        _path = null;
        return command;
    }

    /// <summary>A tick with nothing pressed and the facing kept (a loop step that only waits).</summary>
    public BotCommand Hold() => new() { Facing = _facing };

    /// <summary>One tick of walking toward <paramref name="goal"/> on the grid path, re-planned when stuck (loop steps).</summary>
    public BotCommand WalkTo(BotPerception view, FixVec2 goal, BotCommand command) => Walk(view, goal, command);

    private BotCommand Loot(BotPerception view, SeenEntity loot, BotCommand command)
    {
        Activity = BotActivity.Looting;
        TargetId = 0;
        if (GridPath.DistanceCm(view.Self, loot.Position) > _pickupReachCm)
            return Walk(view, loot.Position, command);
        _dropsAsked.Add(loot.Id);
        command.PickupDropId = loot.Id;
        PickupsAsked++;
        return command;
    }

    private BotCommand Fight(BotPerception view, SeenEntity monster, BotCommand command)
    {
        double distance = GridPath.DistanceCm(view.Self, monster.Position);
        var toTarget = monster.Position - view.Self;
        _facing = GridPath.FacingToward(GridPath.Cm(toTarget.X), GridPath.Cm(toTarget.Y));
        command.Facing = _facing;

        if (ChainPressDue(view, distance))
        {
            // R-3.1-10: press during the hit's chain window (the machine keeps it and starts the next hit at the link tick),
            // again for the link tick, even if the target stepped back past the approach distance but is still within the
            // attack's reach.
            Activity = BotActivity.Fighting;
            _path = null;
            if (_chainPressedFor != view.Chain.StartTick)
                ChainPresses++;
            _chainPressedFor = view.Chain.StartTick;
            if (view.Chain.AtLink)
                _chainLinkPressedFor = view.Chain.StartTick;
            command.Actions = InputActions.Attack;
            ActionsPressed++;
            return command;
        }

        double approachCm = _kit.NormalAttack.ReachCm * _behaviour.ApproachReachPercent / 100.0;
        // R-2.2-07: the monster's body stops the bot at contact; never aim inside it.
        if (_contactCmByNetId != null && _contactCmByNetId.TryGetValue(monster.DefId, out int contactCm))
            approachCm = Math.Max(approachCm, contactCm + _behaviour.ApproachBodyMarginCm);
        if (distance > approachCm)
        {
            Activity = BotActivity.Approaching;
            command = Walk(view, monster.Position, command);
            command.Facing = _facing; // keep aiming at the target while walking
            return command;
        }

        Activity = BotActivity.Fighting;
        _path = null;
        long now = view.NowMs;
        var choice = _kit.Choose(State(view, distance), _behaviour);
        switch (choice.Move)
        {
            case FightMove.BackOff:
                StartLeg(view, monster.Position);
                _backingOff = true;
                _backOffMinUntilMs = _backOffUntilMs;
                if (_recoverSinceMs < 0)
                    _recoverSinceMs = now;
                BackOffs++;
                return command;
            case FightMove.Use when now >= _nextActionAtMs:
                var action = choice.Action!;
                if (action.Slot == 0)
                    command.Actions = InputActions.Attack;
                else
                    command.SkillSlot = (byte)action.Slot;
                _nextActionAtMs = now + Reaction();
                ActionsPressed++;
                return command;
            default:
                return command;
        }
    }

    /// <summary>
    /// R-3.1-10, the QA kill step's rule (<c>QaFightPlan.ChainPressDue</c>) plus a second press for the link tick: a running
    /// hit with a next hit, inside its chain window and not pressed for yet, or at its link tick and not pressed there yet;
    /// the next hit paid by the snapshot stamina, the target still in the normal attack's reach.
    /// </summary>
    private bool ChainPressDue(BotPerception view, double distanceCm)
    {
        var chain = view.Chain;
        bool due = (chain.WindowOpen && _chainPressedFor != chain.StartTick)
                   || (chain.AtLink && _chainLinkPressedFor != chain.StartTick);
        return _behaviour.PressAttackChain && chain.Running && due
               && view.Own.Stamina >= chain.NextStaminaCost && distanceCm <= _kit.NormalAttack.ReachCm;
    }

    /// <summary>
    /// Out of the fight until recovered: it walks a back-off leg; when a leg is over (<c>backOffMs</c>) it walks another one
    /// away from a living monster closer than a leg (QA re-plans its legs too), or with none that close it stands and
    /// regenerates.
    /// </summary>
    private BotCommand Recover(BotPerception view, BotCommand command)
    {
        Activity = BotActivity.BackingOff;
        if (view.NowMs >= _backOffUntilMs)
        {
            var near = NearestLiving(view, _behaviour.BackOffDistanceCm);
            if (near is { } monster)
                StartLeg(view, monster.Position);
            else
            {
                _backOffTo = view.Self;
                _backOffUntilMs = view.NowMs + _behaviour.BackOffMs;
            }
        }
        if (GridPath.DistanceCm(view.Self, _backOffTo) <= LegArrivedCm)
        {
            _path = null;
            return command;
        }
        return Walk(view, _backOffTo, command);
    }

    /// <summary>A back-off leg: <c>backOffDistanceCm</c> straight away from <paramref name="from"/>, for <c>backOffMs</c>.</summary>
    private void StartLeg(BotPerception view, FixVec2 from)
    {
        var toward = from - view.Self;
        double length = Math.Max(1.0, GridPath.DistanceCm(view.Self, from));
        double awayX = -GridPath.Cm(toward.X) / length, awayY = -GridPath.Cm(toward.Y) / length;
        _backOffTo = view.Self + FixVec2.FromCm((int)(awayX * _behaviour.BackOffDistanceCm), (int)(awayY * _behaviour.BackOffDistanceCm));
        _backOffUntilMs = view.NowMs + _behaviour.BackOffMs;
    }

    /// <summary>The bot's side of the fight from the snapshot meters; an action held after OnCooldown is not ready.</summary>
    private FightState State(BotPerception view, double distanceCm)
    {
        long now = view.NowMs;
        var own = view.Own;
        return new FightState(own.Hp, own.MaxHp, own.Mp, own.Stamina, distanceCm,
            a => view.Ready(a) && (!_heldUntil.TryGetValue(a.Id, out long until) || now >= until));
    }

    private static SeenEntity? NearestLiving(BotPerception view, int withinCm)
    {
        SeenEntity? best = null;
        double bestCm = withinCm;
        foreach (var monster in view.Monsters)
        {
            double cm = GridPath.DistanceCm(view.Self, monster.Position);
            if (monster.Alive && cm <= bestCm)
            {
                best = monster;
                bestCm = cm;
            }
        }
        return best;
    }

    /// <summary>The current target while it lives and stays in range, else the nearest living monster in range.</summary>
    private SeenEntity? PickTarget(BotPerception view)
    {
        SeenEntity? best = null;
        double bestCm = double.MaxValue;
        foreach (var monster in view.Monsters)
        {
            if (!monster.Alive)
                continue;
            double cm = GridPath.DistanceCm(view.Self, monster.Position);
            if (cm > _behaviour.TargetSearchCm)
                continue;
            if (monster.Id == TargetId)
                return monster;
            if (cm < bestCm)
            {
                best = monster;
                bestCm = cm;
            }
        }
        return best;
    }

    private SeenEntity? PickDrop(BotPerception view)
    {
        SeenEntity? best = null;
        double bestCm = _behaviour.PickupSearchCm;
        foreach (var drop in view.Drops)
        {
            if (_dropsAsked.Contains(drop.Id))
                continue;
            double cm = GridPath.DistanceCm(view.Self, drop.Position);
            if (cm <= bestCm)
            {
                best = drop;
                bestCm = cm;
            }
        }
        return best;
    }

    /// <summary>A monster in reach that starts an attack (Startup with a new start tick) may be dodged after a reaction delay.</summary>
    private void NoticeAttacks(BotPerception view)
    {
        foreach (var monster in view.Monsters)
        {
            if (!monster.Alive || monster.State != ActionState.Startup)
                continue;
            if (_attackStartsSeen.TryGetValue(monster.Id, out uint seen) && seen == monster.ActionStartTick)
                continue;
            _attackStartsSeen[monster.Id] = monster.ActionStartTick;
            if (!_monsterAttackReachByNetId.TryGetValue(monster.ActionNetId, out int reach)
                || GridPath.DistanceCm(view.Self, monster.Position) > reach)
                continue;
            // Two draws whatever the outcome, so one roll never shifts the next bot decision.
            bool dodge = _rng.RollPercent(_behaviour.DodgeChancePercent);
            int delay = Reaction();
            if (dodge && _dodgeAtMs < 0)
            {
                _dodgeAtMs = view.NowMs + delay;
                _dodgeFrom = monster.Position;
            }
        }
    }

    private BotCommand Walk(BotPerception view, FixVec2 goal, BotCommand command)
    {
        long now = view.NowMs;
        if (_path == null || GridPath.DistanceCm(goal, _pathGoal) > GoalMovedCm || now - _plannedAtMs > ReplanMs)
            Plan(view.Self, goal, now);

        if (now - _progressAtMs > _behaviour.StuckTimeoutMs)
        {
            if (GridPath.DistanceCm(view.Self, _progressFrom) < _behaviour.StuckMinMoveCm)
            {
                StuckReplans++;
                Plan(view.Self, goal, now);
            }
            _progressFrom = view.Self;
            _progressAtMs = now;
        }

        var waypoint = _path != null ? GridPath.NextWaypoint(_grid, view.Self, _path, _gate) : goal;
        var delta = waypoint - view.Self;
        command.Keys = GridPath.KeysToward(GridPath.Cm(delta.X), GridPath.Cm(delta.Y));
        if (command.Keys != MoveKeys.None)
        {
            _facing = GridPath.FacingToward(GridPath.Cm(delta.X), GridPath.Cm(delta.Y));
            command.Facing = _facing;
        }
        return command;
    }

    private void Plan(FixVec2 from, FixVec2 goal, long now)
    {
        _path = GridPath.Find(_grid, from, goal, _gate);
        _pathGoal = goal;
        _plannedAtMs = now;
        Replans++;
    }

    private int Reaction() => _rng.NextInt(_behaviour.ReactionMinMs, _behaviour.ReactionMaxMs + 1);
}
