using GameServer.Entities;
using Shared.Data.Tables;
using Shared.Simulation.Actions;

namespace GameServer.Combat;

/// <summary>
/// Hands each effect phase 3 fires to the system that resolves its kind (R-3.1-07): <c>Projectile</c> to the
/// <see cref="ProjectileSystem"/> (spec 3.2), every other kind to <see cref="Fallback"/>. TODO(WP4 merge): Strike to the
/// HitResolver; TODO(D6): Heal, statuses and GroundArea.
/// </summary>
internal sealed class ActionEffectRouter : IActionEffectSink
{
    private readonly ProjectileSystem? _projectiles;

    public ActionEffectRouter(ProjectileSystem? projectiles)
    {
        _projectiles = projectiles;
    }

    /// <summary>Where effects of the other kinds go.</summary>
    public IActionEffectSink Fallback { get; set; } = NoActionEffectSink.Instance;

    public void OnEffect(Entity entity, in FiredEffect effect, uint tick)
    {
        if (effect.Kind == EffectKind.Projectile && _projectiles != null)
            _projectiles.OnEffect(entity, effect, tick);
        else
            Fallback.OnEffect(entity, effect, tick);
    }
}
