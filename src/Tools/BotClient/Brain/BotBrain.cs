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
/// monster with its class kit (<see cref="BotKit"/>), dodge some monster attacks, pick up drops, and start over after a
/// respawn. One <see cref="Think"/> per client tick. Deterministic for the same perceptions: random choices (dodge rolls,
/// reaction delays) come from its seeded <see cref="IRng"/>, and entities are visited in id order.
/// </summary>
public sealed class BotBrain
{
    private const int ReplanMs = 2_000;
    private const int GoalMovedCm = 100;
    private const int HomeArrivedCm = 300;

    private readonly BotKit _kit;
    private readonly BotBehaviour _behaviour;
    private readonly WorldGrid _grid;
    private readonly FixVec2 _home;
    private readonly IRng _rng;
    private readonly int _pickupReachCm;
    private readonly IReadOnlyDictionary<int, int> _monsterAttackReachByNetId;
    private readonly Dictionary<uint, uint> _attackStartsSeen = new();
    private readonly HashSet<uint> _dropsAsked = [];
    private readonly Dictionary<string, long> _heldUntil = new(StringComparer.Ordinal);
    private List<FixVec2>? _path;
    private FixVec2 _pathGoal;
    private long _plannedAtMs;
    private FixVec2 _progressFrom;
    private long _progressAtMs;
    private long _backOffUntilMs;
    private FixVec2 _backOffTo;
    private long _nextActionAtMs;
    private long _dodgeAtMs = -1;
    private FixVec2 _dodgeFrom;
    private ushort _facing;

    /// <param name="monsterAttackReachByNetId">Reach of each monster attack by <c>ActionDef.netId</c> (the dodge trigger).</param>
    public BotBrain(BotKit kit, BotBehaviour behaviour, WorldGrid grid, FixVec2 home, int pickupRangeCm,
        IReadOnlyDictionary<int, int> monsterAttackReachByNetId, IRng rng)
    {
        _kit = kit;
        _behaviour = behaviour;
        _grid = grid;
        _home = home;
        _rng = rng;
        _pickupReachCm = pickupRangeCm * behaviour.PickupReachPercent / 100;
        _monsterAttackReachByNetId = monsterAttackReachByNetId;
    }

    public BotActivity Activity { get; private set; }

    /// <summary>The monster the bot fights or walks to, 0 for none.</summary>
    public uint TargetId { get; private set; }

    public long DodgesPressed { get; private set; }

    public long ActionsPressed { get; private set; }

    public long PickupsAsked { get; private set; }

    public long BackOffs { get; private set; }

    public long Replans { get; private set; }

    /// <summary>The server refused an action (spec 9.4 <c>ActionResult</c>): after OnCooldown it waits the cooldown out, as QA does.</summary>
    public void OnRefused(ushort actionNetId, ActionReason reason, long nowMs)
    {
        if (reason != ActionReason.OnCooldown)
            return;
        var action = _kit.Actions.FirstOrDefault(a => a.Def.NetId == actionNetId);
        if (action != null)
            _heldUntil[action.Id] = nowMs + action.Def.CooldownMs;
    }

    public BotCommand Think(BotPerception view)
    {
        var command = new BotCommand { Facing = _facing };
        if (view.Dead || !view.HasResources)
        {
            Activity = view.Dead ? BotActivity.Dead : BotActivity.Waiting;
            TargetId = 0;
            _path = null;
            _dodgeAtMs = -1;
            _backOffUntilMs = 0;
            return command;
        }

        var target = PickTarget(view);
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

        if (view.NowMs < _backOffUntilMs)
        {
            Activity = BotActivity.BackingOff;
            return Walk(view, _backOffTo, command);
        }

        // Its own kill's drop first (locked to the killer, R-4.3-02), then the fight, then any drop left around.
        var drop = PickDrop(view);
        if (drop is { } loot && (target == null || view.NowMs - view.LastKillAtMs <= _behaviour.LootAfterKillMs))
            return Loot(view, loot, command);

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

        double approachCm = _kit.NormalAttack.ReachCm * _behaviour.ApproachReachPercent / 100.0;
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
        var own = view.Own;
        var choice = _kit.Choose(new FightState(own.Hp, own.MaxHp, own.Mp, own.Stamina, distance,
            a => view.Ready(a) && (!_heldUntil.TryGetValue(a.Id, out long until) || now >= until)), _behaviour);
        switch (choice.Move)
        {
            case FightMove.BackOff:
                double length = Math.Max(1.0, distance);
                double awayX = -GridPath.Cm(toTarget.X) / length, awayY = -GridPath.Cm(toTarget.Y) / length;
                _backOffTo = view.Self + FixVec2.FromCm((int)(awayX * _behaviour.BackOffDistanceCm), (int)(awayY * _behaviour.BackOffDistanceCm));
                _backOffUntilMs = now + _behaviour.BackOffMs;
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
                Plan(view.Self, goal, now);
            _progressFrom = view.Self;
            _progressAtMs = now;
        }

        var waypoint = _path != null ? GridPath.NextWaypoint(_grid, view.Self, _path) : goal;
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
        _path = GridPath.Find(_grid, from, goal);
        _pathGoal = goal;
        _plannedAtMs = now;
        Replans++;
    }

    private int Reaction() => _rng.NextInt(_behaviour.ReactionMinMs, _behaviour.ReactionMaxMs + 1);
}
