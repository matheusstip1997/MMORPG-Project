using GameServer.Events;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace GameServer.Combat;

/// <summary>
/// Parry resolution of spec 3.3 (<c>ParrySystem</c>, R-3.3-03 to R-3.3-05), a pure function. The parry charge was spent
/// when the parry started (R-3.3-03, the action machine), so here a hit meets either a running parry or a press that was
/// refused for want of a charge:
/// <list type="bullet">
/// <item>a refused press whose would-be window covers the hit: <see cref="ParryResult.NoCharge"/>;</item>
/// <item>a parry in Startup or Recovery: <see cref="ParryResult.FailedTiming"/>;</item>
/// <item>in the window (the Active phase) by the attack's color and the defender's armor class (R-3.3-04): uncolored
/// <see cref="ParryResult.Reduced"/>; red <see cref="ParryResult.FailedRed"/>; yellow needs heavy armor and blue medium or
/// heavy, else <see cref="ParryResult.FailedArmor"/>; green, and yellow and blue with the armor, <see cref="ParryResult.Success"/>.</item>
/// </list>
/// What follows (user decisions, WP6): a success voids the hit (no damage, poise or flinch) and does nothing to the
/// attacker (TODO(open): parry consequences for the attacker); Reduced cuts the damage (step 7 of R-3.4-04) and the poise
/// damage by <c>uncoloredDamageReductionPercent</c> and never flinches; every failure takes the full hit.
/// </summary>
internal static class ParrySystem
{
    /// <param name="inWindow">The hit falls in the parry's Active phase.</param>
    /// <param name="noCharge">The parry is a refused press; the caller passes only those whose would-be window covers
    /// the hit (outside it a refused press is no parry at all).</param>
    public static ParryResult Judge(ParryColor color, ArmorClass armor, bool inWindow, bool noCharge)
    {
        if (noCharge)
            return ParryResult.NoCharge;
        if (!inWindow)
            return ParryResult.FailedTiming;
        return color switch
        {
            ParryColor.None => ParryResult.Reduced,
            ParryColor.Red => ParryResult.FailedRed,
            ParryColor.Yellow => armor == ArmorClass.Heavy ? ParryResult.Success : ParryResult.FailedArmor,
            ParryColor.Blue => armor == ArmorClass.Medium || armor == ArmorClass.Heavy ? ParryResult.Success : ParryResult.FailedArmor,
            _ => ParryResult.Success, // green: anyone
        };
    }

    /// <summary>
    /// The reason code the defender's client shows for a result (spec 9.7 <c>ReasonCodeStrings</c>). A timing failure
    /// says which side of the window the hit fell on (protocol 15): in the parry's Startup the press came too late, in its
    /// Recovery too early.
    /// </summary>
    /// <param name="phaseAtHit">The parry's phase at the hit's tick on the defender's timeline.</param>
    public static ActionReason ReasonFor(ParryResult result, ActionState phaseAtHit) => result switch
    {
        ParryResult.Success => ActionReason.ParrySuccess,
        ParryResult.Reduced => ActionReason.ParryReduced,
        ParryResult.FailedRed => ActionReason.ParryFailedRed,
        ParryResult.FailedArmor => ActionReason.ParryFailedArmor,
        ParryResult.FailedTiming => phaseAtHit == ActionState.Startup ? ActionReason.ParryFailedLate : ActionReason.ParryFailedEarly,
        _ => ActionReason.ParryNoCharge,
    };

    /// <summary>Damage and poise damage after a <see cref="ParryResult.Reduced"/> parry: <c>value x (100 - percent) / 100</c>.</summary>
    public static int Reduce(int value, int reductionPercent) =>
        (int)((long)value * (100 - Math.Clamp(reductionPercent, 0, 100)) / 100);
}
