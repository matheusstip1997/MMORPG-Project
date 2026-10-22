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
/// Monsters step the same machine without input, so flinches and staggers run out. Allocation-free per tick.
/// TODO(F): monsters drive the machine from their AI.
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
    /// Steps a monster's machine with no input, so a flinch or a stagger HitResolver started runs out (R-3.3-01, R-3.3-07).
    /// Monsters start no actions yet (TODO(F): the AI drives this same machine), so nothing fires.
    /// </summary>
    public void StepWithoutInput(Entity entity)
    {
        var input = new ActionInput(false, Shared.Protocol.InputActions.None, 0, entity.Movement.Facing, 0);
        var result = ActionMachine.Step(entity.Actions, input, UnlimitedActionResources.Instance, _fired);
        EffectsDropped += result.FiredDropped;
    }

    private void Dash(Player player, MovementState movement, FixVec2 delta)
    {
        var target = movement.Position + delta;
        var position = Shared.World.GridCollision.Move(_map.Grid, movement.Position, target);
        player.SpeedCheckedPosition = position;
        if (position == movement.Position)
            return;
        player.Movement = new MovementState(position, movement.Facing);
        player.HeightCm = _map.HeightCmAt(position, player.HeightCm);
        player.RegionIndex = _map.Grid.RegionIndexAt(position);
        _grid.Update(player);
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
