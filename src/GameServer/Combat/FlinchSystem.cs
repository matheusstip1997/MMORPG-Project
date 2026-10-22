using GameServer.Entities;
using Shared.Data.Tables;
using Shared.Simulation.Actions;

namespace GameServer.Combat;

/// <summary>
/// Flinch, the small stagger of R-3.3-07 (spec 3.3 <c>FlinchSystem</c>), run per hit after poise when the poise did not
/// break (a break supersedes the flinch). A hit flinches its target when:
/// <list type="bullet">
/// <item>it is not a ground effect (R-3.3-09: ground effects never flinch);</item>
/// <item>its <c>flinchPower</c> is at least the target's <see cref="Combatant.FlinchResist"/>;</item>
/// <item>the target is not flinch-immune (<see cref="Combatant.FlinchImmuneUntilTick"/>);</item>
/// <item>the action machine takes it (<see cref="ActionMachine.Interrupt"/>): it cancels Startup or Channel, catches an
/// idle or flinched entity, and refuses during Active, Recovery, a dodge, a super armor phase (R-3.3-08) and a stagger.</item>
/// </list>
/// The flinch lasts <c>flinchMs x flinchDurationPercent / 100</c>. After it ends, further flinches are ignored for
/// <see cref="Combatant.FlinchImmunityMs"/> (no stunlock). A refused flinch starts no immunity.
/// </summary>
internal static class FlinchSystem
{
    /// <summary>The scaled flinch length of a hit on <paramref name="combatant"/> in ms.</summary>
    public static int DurationMs(ActionEffectDef effect, Combatant combatant) =>
        (int)((long)effect.FlinchMs * combatant.FlinchDurationPercent / 100);

    /// <summary>Flinches <paramref name="target"/> if the hit qualifies; returns false and changes nothing otherwise.</summary>
    public static bool TryFlinch(Entity target, Combatant combatant, ActionEffectDef effect, HitSource source, uint tick,
        CombatRules rules, out int durationMs)
    {
        durationMs = 0;
        if (source == HitSource.GroundArea || effect.FlinchPower <= 0 || effect.FlinchPower < combatant.FlinchResist)
            return false;
        if ((int)(tick - combatant.FlinchImmuneUntilTick) < 0)
            return false;
        int ms = DurationMs(effect, combatant);
        if (!ActionMachine.Interrupt(target.Actions, InterruptKind.Flinch, ms))
            return false;
        durationMs = ms;
        combatant.FlinchImmuneUntilTick = tick + (uint)(target.Actions.InterruptTicks + rules.TicksFor(combatant.FlinchImmunityMs));
        return true;
    }
}
