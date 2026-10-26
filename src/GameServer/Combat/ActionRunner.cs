using GameServer.AI;
using GameServer.Entities;
using GameServer.Events;
using GameServer.World;
using GameServer.World.Map;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Combat;

/// <summary>
/// Receives the effects actions fire in phase 3 (R-3.1-07), in entity-id order. Dash is carried out by the
/// <see cref="ActionRunner"/> itself; the other kinds are for the systems that resolve them (<see cref="EffectRouter"/>):
/// Strike to the HitResolver in phase 5, Projectile to the ProjectileSystem of phase 4, Heal and statuses to the StatusSystem of phase 6 (D6), GroundArea to the GroundAreaSystem (ticks in phase 5).
/// </summary>
public interface IActionEffectSink
{
    void OnEffect(Entity entity, in FiredEffect effect, uint tick);
}

public sealed class NoActionEffectSink : IActionEffectSink
{
    public static readonly NoActionEffectSink Instance = new();

    private NoActionEffectSink()
    {
    }

    public void OnEffect(Entity entity, in FiredEffect effect, uint tick)
    {
    }
}

/// <summary>
/// Tick phase 3 for players (spec 1.6, backlog D2): runs each player's shared <see cref="ActionMachine"/> on the input
/// applied in phase 1, the same code client prediction runs, with its stamina costs and dodge and parry charges (D4).
/// A started action locks the facing (R-3.1-01) and publishes
/// <see cref="ActionStarted"/>; a refused one queues a reason code for the client (spec 9.7); a dash moves the player
/// with world collision and moves the speed-check reference with it, since the server made that move (spec 2.2).
/// Monsters run the same machine on the attack their AI requested (<see cref="Step(Monster, uint)"/>). Allocation-free per tick.
/// </summary>
internal sealed class ActionRunner
{
    private readonly WorldMap _map;
    private readonly SpatialGrid _grid;
    private readonly EventBus? _events;
    private readonly FiredEffect[] _fired = new FiredEffect[ActionMachine.MaxFiredPerStep];
    private readonly PlayerResources _resources = new();

    /// <param name="combat">The time base of the stamina meters; without it players pay no stamina.</param>
    public ActionRunner(WorldMap map, SpatialGrid grid, EventBus? events, CombatRules? combat = null)
    {
        _map = map;
        _grid = grid;
        _events = events;
        _resources.Rules = combat;
    }

    public IActionEffectSink Effects { get; set; } = NoActionEffectSink.Instance;

    /// <summary>Effects that fired but found no room in the buffer (a bug if ever non-zero).</summary>
    public long EffectsDropped { get; private set; }

    public void Step(Player player, uint tick)
    {
        var movement = player.Movement;
        // Without an input frame nothing is pressed; a buffered action that fires now keeps the current facing.
        ushort aim = player.HasInputThisTick ? player.CurrentInput.Facing : movement.Facing;
        var input = new ActionInput(player.HasInputThisTick, player.CurrentActions, player.CurrentSkillSlot, aim,
            player.LastInputSequence);
        _resources.Player = player;
        _resources.Tick = tick;
        var result = ActionMachine.Step(player.Actions, input, _resources, _fired);
        _resources.Player = null;
        var catalog = player.Actions.Catalog;

        // The defender's timeline (R-3.1-03, R-3.3-02): dodge and parry starts on its view clock.
        player.Defence.Step(player.Actions, tick, result.StartedIndex, player.LastInputSequence);

        if (result.StartedIndex >= 0)
        {
            player.Movement = movement = new MovementState(movement.Position, player.Actions.Facing);
            var started = catalog[result.StartedIndex];
            // R-3.1-05: the telegraph carries the parry color (a player's attack uses its PvP color, R-3.3-06).
            _events?.Publish(new ActionStarted(player.Id, started.NetId, tick, player.Actions.Facing,
                ParryColors.Of(player, started)), player.CharacterId);
        }

        if (result.Refused != Shared.Protocol.ActionReason.None)
        {
            ushort netId = result.RefusedIndex >= 0 ? (ushort)catalog[result.RefusedIndex].NetId : (ushort)0;
            player.ActionResults.Add(new PendingActionResult(result.RefusedSequence, netId, result.RefusedSlot, result.Refused));
            // R-3.3-03: a hit that lands where this parry would have been reports NoCharge.
            if (result.Refused == Shared.Protocol.ActionReason.ParryNoCharge && result.RefusedIndex >= 0)
                player.Defence.RefusedNoCharge(catalog[result.RefusedIndex], tick, result.RefusedSequence);
        }

        if (result.DashDelta != FixVec2.Zero)
            Dash(player, movement, result.DashDelta);

        EffectsDropped += result.FiredDropped;
        for (int i = 0; i < result.FiredCount; i++)
        {
            if (_fired[i].Kind != Shared.Data.Tables.EffectKind.Dash)
                Effects.OnEffect(player, _fired[i], tick);
        }
    }

    /// <summary>
    /// Phase 3 for a monster (backlog F): the same machine as players, driven by the attack its AI requested in phase 7
    /// (<see cref="MonsterBrain"/>), aimed at its target. The machine checks the cooldown and locks the facing (R-3.1-01);
    /// the start publishes <see cref="ActionStarted"/> (R-3.1-05 telegraph); fired strikes and projectiles go to
    /// <see cref="Effects"/> like a player's; a dash moves the monster, never into a no-entry region. Without a request the
    /// machine just runs on, so a flinch or a stagger runs out. Monsters pay no stamina (TODO(open), see MonsterDef).
    /// </summary>
    public void Step(Monster monster, uint tick)
    {
        var brain = monster.Brain;
        var movement = monster.Movement;
        var input = ActionInput.Request(brain.RequestedAction, brain.Aim);
        brain.RequestedAction = -1;
        var result = ActionMachine.Step(monster.Actions, input, UnlimitedActionResources.Instance, _fired);

        if (result.StartedIndex >= 0)
        {
            monster.Movement = movement = new MovementState(movement.Position, monster.Actions.Facing);
            // R-3.1-05: the telegraph carries the attack's parry color (a monster's attack uses its PvE color, R-3.3-06).
            var started = monster.Actions.Catalog[result.StartedIndex];
            _events?.Publish(new ActionStarted(monster.Id, started.NetId, tick, monster.Actions.Facing,
                ParryColors.Of(monster, started)));
        }

        if (result.DashDelta != FixVec2.Zero)
            Dash(monster, movement, result.DashDelta);

        EffectsDropped += result.FiredDropped;
        for (int i = 0; i < result.FiredCount; i++)
        {
            if (_fired[i].Kind != Shared.Data.Tables.EffectKind.Dash)
                Effects.OnEffect(monster, _fired[i], tick);
        }
    }

    /// <summary>Where monsters may not go (the safe city); null lets dashes go anywhere the grid allows.</summary>
    internal MonsterAi? MonsterAi { get; set; }

    private void Dash(Entity entity, MovementState movement, FixVec2 delta)
    {
        var target = movement.Position + delta;
        var position = Shared.World.GridCollision.Move(_map.Grid, movement.Position, target);
        if (entity is Monster && MonsterAi != null)
            position = MonsterAi.KeepOut(movement.Position, position);
        entity.SpeedCheckedPosition = position;
        if (position == movement.Position)
            return;
        entity.Movement = new MovementState(position, movement.Facing);
        entity.HeightCm = _map.HeightCmAt(position, entity.HeightCm);
        entity.RegionIndex = _map.Grid.RegionIndexAt(position);
        _grid.Update(entity);
    }

    /// <summary>
    /// Pays the stamina and MP of the player whose actions are stepping (R-3.1-02, R-3.1-04) from its combatant's meters at
    /// this tick's simulation time (<see cref="CombatRules.MsAt"/>); a spend restarts the meter's regen delay (spec 12.7:
    /// 1 s). Stamina is checked first, then MP; short stamina refuses with <c>NoStamina</c>, short MP with <c>NoMp</c>, and
    /// a refusal spends neither. A player without a combatant (a world built without combat rules) pays nothing. One
    /// instance reused every tick, so no allocation. Dodge and parry charges need nothing here: they live in the action
    /// machine state.
    /// </summary>
    private sealed class PlayerResources : IActionResources
    {
        public Player? Player;
        public uint Tick;
        public CombatRules? Rules;

        public ActionReason TrySpend(int staminaCost, int mpCost)
        {
            if (Rules == null || Player?.Combatant is not { } combatant)
                return ActionReason.None;
            long nowMs = Rules.MsAt(Tick);
            var meters = combatant.Meters;
            if (staminaCost > 0 && meters.Current(ResourceKind.Stamina, nowMs) < staminaCost)
                return ActionReason.NoStamina;
            if (mpCost > 0 && meters.Current(ResourceKind.Mp, nowMs) < mpCost)
                return ActionReason.NoMp;
            if (staminaCost > 0 && meters.TrySpend(ResourceKind.Stamina, staminaCost, nowMs) != SpendResult.Ok)
                return ActionReason.NoStamina;
            if (mpCost > 0 && meters.TrySpend(ResourceKind.Mp, mpCost, nowMs) != SpendResult.Ok)
                return ActionReason.NoMp; // not reached: checked above on the same meter and time
            return ActionReason.None;
        }
    }
}
