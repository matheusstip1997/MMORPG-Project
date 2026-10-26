using GameServer.Entities;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Actions;

namespace GameServer.Combat;

/// <summary>
/// A strike that fired in phase 3, placed where and how its caster stood at that moment (spec 3.1 hit shapes).
/// <paramref name="ActionNetId"/> and <paramref name="EffectIndex"/> name the effect in its action for the strike trace
/// (spec 11.7); <see cref="NoEffectIndex"/> when it did not come from a running action.
/// </summary>
internal readonly record struct PendingStrike(Entity Attacker, ActionEffectDef Effect, FixVec2 Origin, ushort Facing, uint Tick,
    int ActionNetId = 0, int EffectIndex = PendingStrike.NoEffectIndex)
{
    public const int NoEffectIndex = 255;
}

/// <summary>
/// Strikes fired in phase 3, waiting for phase 5 (spec 1.6: hit resolution after all actions ran). Shapes are placed when
/// the effect fires (spec 3.1), so the caster's position (after its dash) and locked facing are captured here. Phase 3
/// runs entities in id order, so the queue is in attacker-id order. Grows when needed and never shrinks, so steady
/// state allocates nothing.
/// </summary>
internal sealed class StrikeQueue
{
    private readonly List<PendingStrike> _strikes = new(16);

    public int Count => _strikes.Count;

    public PendingStrike this[int index] => _strikes[index];

    public void Add(Entity attacker, in FiredEffect effect, uint tick) =>
        _strikes.Add(new PendingStrike(attacker, effect.Effect.Def, attacker.Movement.Position, attacker.Actions.Facing, tick,
            effect.Action.NetId, effect.EffectIndex));

    /// <summary>An effect fired now from where the caster stands and faces (tests, and WP11's triggers).</summary>
    public void Add(Entity attacker, ActionEffectDef effect, uint tick) =>
        _strikes.Add(new PendingStrike(attacker, effect, attacker.Movement.Position, attacker.Actions.Facing, tick));

    public void Clear() => _strikes.Clear();
}

/// <summary>
/// Hands the effects phase 3 fires (<see cref="IActionEffectSink"/>) to the systems that resolve them: Strike to the
/// <see cref="StrikeQueue"/> (phase 5), Projectile to <see cref="Projectiles"/> (the ProjectileSystem of phase 4), Heal,
/// ApplyStatus and RemoveStatus to the <see cref="StatusSystem"/> queue (phase 6, backlog D6), placed like strikes,
/// GroundArea to <see cref="GroundAreas"/> (the <see cref="GroundAreaSystem"/>, placed now, ticking from phase 5). Dash
/// never comes here (the <see cref="ActionRunner"/> carries it out).
/// </summary>
internal sealed class EffectRouter : IActionEffectSink
{
    private readonly StrikeQueue _strikes;

    public EffectRouter(StrikeQueue strikes)
    {
        _strikes = strikes;
    }

    /// <summary>Where Projectile effects go (spec 3.2); nothing until the ProjectileSystem sets itself here.</summary>
    public IActionEffectSink Projectiles { get; set; } = NoActionEffectSink.Instance;

    /// <summary>Where Heal, ApplyStatus and RemoveStatus go (spec 3.7); null without combat rules: they then do nothing.</summary>
    public StatusSystem? Statuses { get; set; }

    /// <summary>Where GroundArea effects go (spec 3.1); nothing until the GroundAreaSystem sets itself here.</summary>
    public IActionEffectSink GroundAreas { get; set; } = NoActionEffectSink.Instance;

    public void OnEffect(Entity entity, in FiredEffect effect, uint tick)
    {
        switch (effect.Kind)
        {
            case EffectKind.Strike:
                _strikes.Add(entity, effect, tick);
                break;
            case EffectKind.Projectile:
                Projectiles.OnEffect(entity, effect, tick);
                break;
            case EffectKind.Heal:
            case EffectKind.ApplyStatus:
            case EffectKind.RemoveStatus:
                Statuses?.Queue.Add(entity, effect, tick);
                break;
            case EffectKind.GroundArea:
                GroundAreas.OnEffect(entity, effect, tick);
                break;
        }
    }
}
