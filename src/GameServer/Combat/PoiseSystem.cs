using GameServer.Entities;
using Shared.Simulation.Actions;

namespace GameServer.Combat;

/// <summary>
/// Poise (R-3.3-01, spec 3.3 <c>PoiseSystem</c>) on a combatant's <see cref="PoiseState"/>, in ticks. Pure functions of the
/// state, the hit and the rules, so the same inputs always give the same bar (architecture rule 6).
/// <list type="bullet">
/// <item>Regeneration is lazy: nothing runs per tick. The bar comes back <see cref="CombatRules.PoiseRegenPercentPerSec"/>
/// of its max per second, starting <see cref="CombatRules.PoiseRegenDelayTicks"/> after the last hit that dealt poise
/// damage (<see cref="PoiseState.LastHitTick"/>); a hit first folds that regeneration in.</item>
/// <item>A hit subtracts its poise damage; at 0 the bar breaks: the entity is staggered for <see cref="CombatRules.StaggerMs"/>
/// (<see cref="ActionMachine.Interrupt"/> with <c>PoiseBreak</c>, from any state but death), the bar resets to max, and
/// stagger immunity starts (<see cref="CombatRules.StaggerImmunityTicks"/>, no chain stuns).</item>
/// <item>During stagger immunity poise damage is ignored, so the bar stays full until it ends.</item>
/// </list>
/// </summary>
internal static class PoiseSystem
{
    /// <summary>The bar at <paramref name="tick"/>, with the lazy regeneration since the last poise hit.</summary>
    public static int CurrentAt(in PoiseState poise, uint tick, CombatRules rules)
    {
        if (poise.Current >= poise.Max)
            return poise.Max;
        long regenTicks = (long)(int)(tick - poise.LastHitTick) - rules.PoiseRegenDelayTicks;
        if (regenTicks <= 0 || rules.PoiseRegenPercentPerSec <= 0)
            return poise.Current;
        long gained = (long)poise.Max * rules.PoiseRegenPercentPerSec * regenTicks / (100L * rules.TickRateHz);
        return (int)Math.Min(poise.Max, poise.Current + gained);
    }

    /// <summary>True while a break's stagger immunity runs.</summary>
    public static bool IsImmune(in PoiseState poise, uint tick) => (int)(tick - poise.ImmuneUntilTick) < 0;

    /// <summary>
    /// Applies <paramref name="poiseDamage"/> at <paramref name="tick"/>. Returns true when the bar broke (it is then
    /// reset and immune); the caller staggers the entity (<see cref="Hit"/> does both).
    /// </summary>
    public static bool Apply(ref PoiseState poise, int poiseDamage, uint tick, CombatRules rules)
    {
        if (poiseDamage <= 0 || IsImmune(poise, tick))
            return false;
        int current = CurrentAt(poise, tick, rules) - poiseDamage;
        poise.LastHitTick = tick;
        if (current > 0)
        {
            poise.Current = current;
            return false;
        }
        poise.Current = poise.Max;
        poise.ImmuneUntilTick = tick + (uint)rules.StaggerImmunityTicks;
        return true;
    }

    /// <summary>Applies the hit's poise damage to <paramref name="target"/> and staggers it when the bar breaks.</summary>
    public static bool Hit(Entity target, Combatant combatant, int poiseDamage, uint tick, CombatRules rules)
    {
        if (!Apply(ref combatant.Poise, poiseDamage, tick, rules))
            return false;
        ActionMachine.Interrupt(target.Actions, InterruptKind.PoiseBreak, rules.StaggerMs);
        return true;
    }
}
