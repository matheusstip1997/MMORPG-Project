using GameServer.Entities;
using GameServer.World;
using GameServer.World.Map;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.World;

namespace GameServer.AI;

/// <summary>
/// The generic monster AI (backlog F: aggro with the visibility rule, chase, attack by action table, leash), the same
/// code for every monster type; what differs is data (<see cref="Shared.Data.Tables.MonsterDef"/>, its attacks, and
/// <see cref="Shared.Data.Tables.MonsterAiSettings"/>).
/// <para>
/// Phase 7 (<see cref="Think"/>) decides, per monster in entity-id order, an intent on its <see cref="MonsterBrain"/>;
/// phase 2 of the next tick walks (<see cref="Move"/>, the shared <see cref="MovementSim.StepAlong"/> with grid
/// collision), and phase 3 starts the requested attack on the same <c>ActionMachine</c> players use (the machine locks
/// the facing, R-3.1-01). No random numbers: ties go to the lower entity id, attacks go in table order, so the same
/// inputs give the same result (architecture rule 6).
/// </para>
/// <para>
/// User decisions (2026-10-22): an idle monster aggroes the nearest alive player inside its <c>aggroRangeCm</c> that it
/// can see through <see cref="IVisibilityQuery"/> (R-2.4-03), or a player that damaged it if it can see that player; it
/// keeps the target until the target dies, leaves the world, can no longer be seen, or stands in a no-entry region, or
/// until the monster is past its <c>leashRangeCm</c> from home. Then it walks home ignoring players (hits still land),
/// and once home gets full HP and poise back; a walk home that stops making progress for <c>leashTimeoutMs</c> ends with
/// the monster placed at home. Monsters never enter no-entry regions (the safe city).
/// </para>
/// <para>
/// Bosses (<see cref="Shared.Data.Tables.MonsterDef.Boss"/>) use the same AI with one difference (user decision
/// 2026-10-23): a boss that loses its target inside its leash takes the nearest player it would aggro, and goes home only
/// when there is none. TODO(open): boss phases (attacks or behaviour changing at an HP threshold) and a damage-based threat table (spec
/// 10.4); in M2 "threat" is the provoke status (WP-M2-9), which turns a monster on its applier.
/// </para>
/// Simulation thread only; allocation-free per tick once the candidate list has grown.
/// </summary>
internal sealed class MonsterAi
{
    private MonsterAiRules _rules;
    private readonly WorldMap _map;
    private readonly SpatialGrid _grid;
    private readonly IVisibilityQuery _visibility;
    private readonly List<Entity> _candidates = new();
    private readonly BodyBlockers? _bodies;

    /// <summary>Called when a monster arrives home and resets (the world ends its statuses: user decision 2026-10-23).</summary>
    internal Action<Monster, uint>? LeashReset { get; set; }

    /// <param name="bodies">R-2.2-07: the world's body gather; null lets monsters walk through every body.</param>
    public MonsterAi(MonsterAiRules rules, WorldMap map, SpatialGrid grid, IVisibilityQuery visibility,
        BodyBlockers? bodies = null)
    {
        _rules = rules;
        _map = map;
        _grid = grid;
        _visibility = visibility;
        _bodies = bodies;
    }

    public MonsterAiRules Rules => _rules;

    /// <summary>Dev hot reload (spec 11.7): attacks, ranges and timings of the reloaded tables from the next think.</summary>
    public void Retune(MonsterAiRules rules) => _rules = rules;

    /// <summary>Phase 7: the monster's intent for the next tick. <paramref name="lookup"/> finds an entity by id.</summary>
    public void Think(Monster monster, uint tick, long nowMs, Func<uint, Entity?> lookup)
    {
        var brain = monster.Brain;
        brain.ClearIntent();
        if (monster.Combatant is not { IsAlive: true })
            return;

        // Spec 3.7 M2 status list (WP-M2-9, provoke and threat): a provoked monster turns on the status's applier while it
        // lasts, from Idle or a chase; a walk home (the leash) wins. After it ends the monster keeps that target as usual.
        if (brain.Mode != MonsterMode.Return && Provoker(monster, lookup) is { } provoker)
        {
            brain.Target = provoker;
            brain.Mode = MonsterMode.Chase;
        }

        if (brain.Mode == MonsterMode.Idle)
        {
            var target = Retaliation(monster, brain, lookup);
            brain.RetaliateId = 0;
            if (target == null && (tick + monster.Id) % (uint)_rules.ThinkTicks == 0)
                target = NearestPlayer(monster);
            if (target != null)
            {
                brain.Target = target;
                brain.Mode = MonsterMode.Chase;
            }
            else if (DistanceSqCm(monster.Movement.Position, brain.Home) > Square(_rules.HomeToleranceCm))
            {
                StartReturn(monster, tick);
            }
        }
        else
        {
            brain.RetaliateId = 0; // hits do not re-aggro a monster that is busy or going home
        }

        if (brain.Mode == MonsterMode.Chase)
            Chase(monster, brain, tick);
        if (brain.Mode == MonsterMode.Return)
            Return(monster, brain, tick, nowMs);
    }

    /// <summary>
    /// Phase 2: the walk or turn the last think asked for, unless an action, a flinch or a stagger runs (R-2.2-02). A step
    /// into a no-entry region is cut back to the axis that stays out, so the monster slides along its border like a wall.
    /// Bodies block it (R-2.2-07: players and other monsters, with the <c>BodyCollisionSettings</c> walk response).
    /// The world records the position history after this (R-3.1-03).
    /// </summary>
    public void Move(Monster monster)
    {
        var brain = monster.Brain;
        if (!brain.Turns || monster.Actions.IsBusy || monster.Combatant is not { IsAlive: true })
            return;
        var from = monster.Movement.Position;
        if (!brain.Walks)
        {
            monster.Movement = new MovementState(from, brain.Facing);
            return;
        }
        // R-2.2-03 with moveSpeedPercent (WP-M2-9: Cold's slow).
        int statId = _rules.MoveSpeedPercentStat;
        int speed = MovementSim.SpeedFor(monster.Def.MoveSpeedCmPerSec,
            statId >= 0 ? monster.Combatant!.Stats.Get(statId) : 0);
        var bodies = _rules.Bodies;
        if (!bodies.Enabled || _bodies == null || monster.Combatant is not { } body)
        {
            var next = MovementSim.StepAlong(monster.Movement, brain.Facing, false, speed,
                _rules.TickRateHz, _map.Grid);
            Place(monster, KeepOut(from, next.Position), next.Facing);
            return;
        }
        // R-2.2-07: players and other monsters block the walk (the chase and the walk home alike); the monster slides
        // round them and never pushes. The no-entry clip runs last, and may not push it into a body either.
        var near = _bodies.For(monster, body.BodyRadiusRaw,
            MovementSim.StepPerTick(speed, _rules.TickRateHz), _rules.MaxBodyRadiusRaw);
        var stepped = MovementSim.StepAlong(monster.Movement, brain.Facing, false, speed,
            _rules.TickRateHz, _map.Grid, near, body.BodyRadiusRaw, bodies);
        var to = KeepOut(from, stepped.Position);
        if (to != stepped.Position && BodyCollision.IsDeeper(from, to, body.BodyRadiusRaw, near, bodies))
            to = from;
        Place(monster, to, stepped.Facing);
    }

    /// <summary>Where a monster may end a move from <paramref name="from"/> towards <paramref name="to"/>: out of no-entry regions.</summary>
    internal FixVec2 KeepOut(FixVec2 from, FixVec2 to)
    {
        if (to == from || !_rules.IsNoEntry(_map, to))
            return to;
        var alongX = new FixVec2(to.X, from.Y);
        if (alongX != from && !_rules.IsNoEntry(_map, alongX))
            return alongX;
        var alongY = new FixVec2(from.X, to.Y);
        if (alongY != from && !_rules.IsNoEntry(_map, alongY))
            return alongY;
        return from;
    }

    /// <summary>The applier of the monster's provoke status (WP-M2-9) when it is a valid target, else null.</summary>
    private Player? Provoker(Monster monster, Func<uint, Entity?> lookup)
    {
        var statuses = monster.Combatant!.Statuses;
        int index = statuses.IndexOf(StatusControl.Provoke);
        if (index < 0 || lookup(statuses[index].SourceEntityId) is not Player player)
            return null;
        return IsValidTarget(monster, player) ? player : null;
    }

    private Player? Retaliation(Monster monster, MonsterBrain brain, Func<uint, Entity?> lookup)
    {
        if (brain.RetaliateId == 0 || lookup(brain.RetaliateId) is not Player player)
            return null;
        return IsValidTarget(monster, player) ? player : null;
    }

    /// <summary>The nearest valid player inside the aggro range; equal distances go to the lower entity id.</summary>
    private Player? NearestPlayer(Monster monster)
    {
        int range = monster.Def.AggroRangeCm;
        int x = monster.XCm, y = monster.YCm;
        _candidates.Clear();
        _grid.QueryCells(x - range, y - range, x + range, y + range, _candidates);
        Player? best = null;
        long bestDistanceSq = Square(range);
        foreach (var candidate in _candidates)
        {
            if (candidate is not Player player)
                continue;
            long distanceSq = DistanceSqCm(monster.Movement.Position, player.Movement.Position);
            if (distanceSq > bestDistanceSq || (distanceSq == bestDistanceSq && best != null && player.Id > best.Id))
                continue;
            if (!IsValidTarget(monster, player))
                continue;
            best = player;
            bestDistanceSq = distanceSq;
        }
        _candidates.Clear();
        return best;
    }

    /// <summary>Alive, still in the world, outside no-entry regions, and seen by the monster (R-2.4-03).</summary>
    private bool IsValidTarget(Monster monster, Player player) =>
        player.InGrid
        && player.Combatant is { IsAlive: true }
        && !_rules.IsNoEntry(player.RegionIndex)
        && _visibility.CanSee(monster, player);

    private void Chase(Monster monster, MonsterBrain brain, uint tick)
    {
        if (DistanceSqCm(monster.Movement.Position, brain.Home) > Square(monster.Def.LeashRangeCm))
        {
            StartReturn(monster, tick);
            return;
        }
        var target = brain.Target;
        if (target == null || !IsValidTarget(monster, target))
        {
            // User decision 2026-10-23: a boss that loses its target takes the nearest player it would aggro, so one
            // death does not reset the fight for the others; other monsters go home.
            target = monster.Def.Boss ? NearestPlayer(monster) : null;
            if (target == null)
            {
                StartReturn(monster, tick);
                return;
            }
            brain.Target = target;
        }
        if (monster.Actions.IsBusy)
            return;

        var from = monster.Movement.Position;
        var at = target.Movement.Position;
        ushort aim = FixedTrig.AngleOf((long)at.X - from.X, (long)at.Y - from.Y);
        int radius = target.Combatant!.BodyRadiusRaw;
        var attacks = _rules.AttacksOf(monster.Def);
        MonsterAttack? shortest = null;
        foreach (var attack in attacks)
        {
            if (monster.Actions.CooldownTicks(attack.Index) == 0 && attack.WouldHit(from, aim, at, radius))
            {
                brain.RequestedAction = attack.Index;
                brain.Aim = aim;
                return;
            }
            if (shortest == null || attack.NominalReachCm < shortest.NominalReachCm)
                shortest = attack;
        }
        // Nothing ready reaches: close in until the shortest attack would reach, then wait facing the target.
        brain.Turns = true;
        brain.Facing = aim;
        brain.Walks = shortest == null || !shortest.WouldHit(from, aim, at, radius);
    }

    private void StartReturn(Monster monster, uint tick)
    {
        var brain = monster.Brain;
        brain.Target = null;
        brain.Mode = MonsterMode.Return;
        brain.BestHomeDistanceSq = DistanceSqCm(monster.Movement.Position, brain.Home);
        brain.LastProgressTick = tick;
    }

    private void Return(Monster monster, MonsterBrain brain, uint tick, long nowMs)
    {
        var position = monster.Movement.Position;
        long distanceSq = DistanceSqCm(position, brain.Home);
        if (distanceSq <= Square(_rules.HomeToleranceCm))
        {
            Arrive(monster, tick, nowMs, placeAtHome: false);
            return;
        }
        if (distanceSq < brain.BestHomeDistanceSq)
        {
            brain.BestHomeDistanceSq = distanceSq;
            brain.LastProgressTick = tick;
        }
        else if ((int)(tick - brain.LastProgressTick) >= _rules.LeashTimeoutTicks)
        {
            Arrive(monster, tick, nowMs, placeAtHome: true); // stuck: no pathfinding in M1
            return;
        }
        brain.Turns = true;
        brain.Walks = true;
        brain.Facing = FixedTrig.AngleOf((long)brain.Home.X - position.X, (long)brain.Home.Y - position.Y);
    }

    /// <summary>Home: the leash reset (full HP and poise), the spawn facing, and back to idle.</summary>
    private void Arrive(Monster monster, uint tick, long nowMs, bool placeAtHome)
    {
        var brain = monster.Brain;
        if (placeAtHome)
        {
            Place(monster, brain.Home, brain.HomeFacing);
            // A jump, not a walk: a rewind must not find the monster somewhere between (R-3.1-03).
            monster.Combatant!.History.Clear();
            monster.Combatant.History.Record(tick, brain.Home);
        }
        else
        {
            monster.Movement = new MovementState(monster.Movement.Position, brain.HomeFacing);
        }
        monster.Combatant!.RestoreFull(nowMs);
        LeashReset?.Invoke(monster, tick);
        brain.Mode = MonsterMode.Idle;
        brain.Target = null;
        brain.RetaliateId = 0;
    }

    /// <summary>Moves the monster with the bookkeeping every server-side move needs (height, region, grid cell).</summary>
    private void Place(Monster monster, FixVec2 position, ushort facing)
    {
        bool moved = position != monster.Movement.Position;
        monster.Movement = new MovementState(position, facing);
        monster.SpeedCheckedPosition = position;
        if (!moved)
            return;
        monster.HeightCm = _map.HeightCmAt(position, monster.HeightCm);
        monster.RegionIndex = _map.Grid.RegionIndexAt(position);
        _grid.Update(monster);
    }

    private static long Square(int cm) => (long)cm * cm;

    /// <summary>Squared distance in cm (positions rounded to cm); far apart pairs saturate instead of overflowing.</summary>
    internal static long DistanceSqCm(FixVec2 a, FixVec2 b)
    {
        long dx = FixedPoint.ToCmRound(a.X) - (long)FixedPoint.ToCmRound(b.X);
        long dy = FixedPoint.ToCmRound(a.Y) - (long)FixedPoint.ToCmRound(b.Y);
        const long Far = 1_000_000_000; // 10,000 km: beyond any range
        if (Math.Abs(dx) > Far || Math.Abs(dy) > Far)
            return long.MaxValue;
        return dx * dx + dy * dy;
    }
}
