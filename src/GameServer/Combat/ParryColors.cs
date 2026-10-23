using GameServer.Entities;
using Shared.Data.Tables;
using Shared.Simulation.Actions;

namespace GameServer.Combat;

/// <summary>
/// The parry color an attack telegraphs (R-3.1-05) and is parried by (R-3.3-04, R-3.3-06): a monster's action uses its
/// <c>parryClassPve</c>, a player's its <c>parryClassPvp</c>. <c>ActionStarted</c>, snapshots and HitResolver all read it
/// here, so they agree.
/// </summary>
internal static class ParryColors
{
    public static ParryColor Of(Entity attacker, ActionTiming action) => Of(attacker, action.Def);

    public static ParryColor Of(Entity attacker, ActionDef action) => action.ParryColorFor(attacker is Monster);

    /// <summary>The color of the action <paramref name="entity"/> is running, None when it runs none.</summary>
    public static ParryColor Running(Entity entity)
    {
        var action = entity.Actions.Action;
        return action == null ? ParryColor.None : Of(entity, action);
    }
}
