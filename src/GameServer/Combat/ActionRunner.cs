using GameServer.AI;
using GameServer.Entities;
using GameServer.Events;
using GameServer.World;
using GameServer.World.Map;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Combat;

/// <summary>
/// Receives the effects actions fire in phase 3 (R-3.1-07), in entity-id order. Dash is carried out by the
/// <see cref="ActionRunner"/> itself; the other kinds are for the systems that resolve them (<see cref="EffectRouter"/>):
/// Strike to the HitResolver in phase 5, Projectile to the ProjectileSystem of phase 4, TODO(D6) Heal, statuses and GroundArea.
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
    private readonly PlayerStamina _stamina = new();

    /// <param name="combat">The time base of the stamina meters; without it players pay no stamina.</param>
    public ActionRunner(WorldMap map, SpatialGrid grid, EventBus? events, CombatRules? combat = null)
    {
        _map = map;
        _grid = grid;
        _events = events;
        _stamina.Rules = combat;
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
        _stamina.Player = player;
        _stamina.Tick = tick;
        var result = ActionMachine.Step(player.Actions, input, _stamina, _fired);
        _stamina.Player = null;
        var catalog = player.Actions.Catalog;

        if (result.StartedIndex >= 0)
        {
            player.Movement = movement = new MovementState(movement.Position, player.Actions.Facing);
            // TODO(D4): the parry color of colored attacks (R-3.1-05); player actions have none in M1.
            _events?.Publish(new ActionStarted(player.Id, catalog[result.StartedIndex].NetId, tick, player.Actions.Facing,
                ParryColor.None), player.CharacterId);
        }

        if (result.Refused != Shared.Protocol.ActionReason.None)
        {
            ushort netId = result.RefusedIndex >= 0 ? (ushort)catalog[result.RefusedIndex].NetId : (ushort)0;
            player.ActionResults.Add(new PendingActionResult(result.RefusedSequence, netId, result.RefusedSlot, result.Refused));
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
            // TODO(WP6): the parry color of colored monster attacks (R-3.1-05, R-3.3-04).
            _events?.Publish(new ActionStarted(monster.Id, monster.Actions.Catalog[result.StartedIndex].NetId, tick,
                monster.Actions.Facing, ParryColor.None));
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
    /// Pays the stamina of the player whose actions are stepping (R-3.1-02, R-3.1-04) from its combatant's stamina meter at
    /// this tick's simulation time (<see cref="CombatRules.MsAt"/>); a spend restarts the meter's regen delay (spec 12.7:
    /// 1 s). Short stamina makes the machine refuse with <c>NoStamina</c>. A player without a combatant (a world built
    /// without combat rules) pays nothing. One instance reused every tick, so no allocation. Dodge and parry charges need
    /// nothing here: they live in the action machine state.
    /// </summary>
    private sealed class PlayerStamina : IActionResources
    {
        public Player? Player;
        public uint Tick;
        public CombatRules? Rules;

        public bool TrySpendStamina(int cost)
        {
            if (cost <= 0 || Rules == null || Player?.Combatant is not { } combatant)
                return true;
            return combatant.Meters.TrySpend(ResourceKind.Stamina, cost, Rules.MsAt(Tick)) == SpendResult.Ok;
        }
    }
}
