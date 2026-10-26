using System;
using Shared.Data.Tables;

namespace Shared.Simulation.Stats
{
    /// <summary>
    /// One <see cref="BuildUpDef"/> with its times in ticks and its meter in units (spec 3.8). Immutable; built once by the
    /// combat rules from rows that passed the validator.
    /// </summary>
    public sealed class BuildUpRule
    {
        public BuildUpRule(BuildUpDef def, int index, int elementIndex, int resistStat, int decayDelayTicks, int penaltyWindowTicks,
            int tickRateHz)
        {
            if (tickRateHz <= 0)
                throw new ArgumentOutOfRangeException(nameof(tickRateHz));
            Def = def;
            Index = index;
            ElementIndex = elementIndex;
            ResistStat = resistStat;
            DecayDelayTicks = decayDelayTicks;
            PenaltyWindowTicks = penaltyWindowTicks;
            TickRateHz = tickRateHz;
        }

        public BuildUpDef Def { get; }

        public string Id => Def.Id;

        public int NetId => Def.NetId;

        /// <summary>Slot of this build-up in every combatant's meters (the rules sort the rows by id).</summary>
        public int Index { get; }

        /// <summary>The element's <see cref="CombatConfig"/> index (R-3.8-10).</summary>
        public int ElementIndex { get; }

        /// <summary>The element's resistance stat id in the <see cref="StatBlock"/> (R-3.8-10: the same stat as for damage).</summary>
        public int ResistStat { get; }

        /// <summary><c>decayDelayMs</c> rounded up to whole ticks.</summary>
        public int DecayDelayTicks { get; }

        /// <summary><c>repeatPenaltyWindowMs</c> rounded up to whole ticks.</summary>
        public int PenaltyWindowTicks { get; }

        public int TickRateHz { get; }
    }

    /// <summary>
    /// The pure build-up formulas of spec 3.8 in integer math (rule 6), in <see cref="UnitsPerPoint"/> units so a gain such as
    /// 25 x 85% stays exact. Shared so a tooltip or a tool can show the same numbers the server uses.
    /// </summary>
    public static class BuildUpMath
    {
        /// <summary>Meter units per point (the meter's 0-100 points of R-3.8-02 are 0-10000 units).</summary>
        public const int UnitsPerPoint = 100;

        /// <summary>A full meter in units (<see cref="BuildUpDef.MeterMax"/> points).</summary>
        public const int MaxUnits = BuildUpDef.MeterMax * UnitsPerPoint;

        /// <summary>
        /// R-3.8-10: the resistance the build-up formula uses, from the element's resistance stat: the stat times
        /// <c>resistScalePercent</c> / 100, clamped to <c>resistMinPercent..resistMaxPercent</c> (R-3.8-03: capped, a
        /// weakness allowed).
        /// </summary>
        public static int ResistPercent(BuildUpDef def, int resistStat)
        {
            long scaled = (long)resistStat * def.ResistScalePercent / 100;
            return (int)Math.Max(def.ResistMinPercent, Math.Min(def.ResistMaxPercent, scaled));
        }

        /// <summary>
        /// R-3.8-03: the units a gain of <paramref name="points"/> adds: points x (100 - resistance) / 100 x
        /// <paramref name="penaltyPercent"/> / 100 x <paramref name="keepPercent"/> / 100 (an uncolored parry keeps
        /// 100 - its reduction, R-3.8-07), rounded down, never below 0 nor above a full meter.
        /// </summary>
        public static int GainUnits(BuildUpDef def, int points, int resistStat, int penaltyPercent, int keepPercent = 100)
        {
            if (points <= 0)
                return 0;
            long units = (long)points * UnitsPerPoint * (100 - ResistPercent(def, resistStat)) / 100;
            units = units * Math.Max(0, penaltyPercent) / 100 * Math.Max(0, keepPercent) / 100;
            return (int)Math.Max(0, Math.Min(MaxUnits, units));
        }

        /// <summary>
        /// R-3.8-06: <paramref name="units"/> after decaying from <paramref name="fromTick"/> to <paramref name="nowTick"/> at
        /// <paramref name="pointsPerSec"/> (nothing for a <paramref name="nowTick"/> at or before <paramref name="fromTick"/>).
        /// </summary>
        public static int Decay(int units, uint fromTick, uint nowTick, int pointsPerSec, int tickRateHz)
        {
            int elapsed = (int)(nowTick - fromTick);
            if (elapsed <= 0 || units <= 0)
                return Math.Max(0, units);
            long lost = (long)pointsPerSec * UnitsPerPoint * elapsed / tickRateHz;
            return (int)Math.Max(0, units - lost);
        }

        /// <summary>The gain percent at a penalty level (R-3.8-05): 100 at level 0, else the level's entry (the last one beyond).</summary>
        public static int PenaltyPercent(BuildUpDef def, int level)
        {
            var levels = def.RepeatPenaltyGainPercents;
            if (level <= 0 || levels.Length == 0)
                return 100;
            return levels[Math.Min(level, levels.Length) - 1];
        }
    }

    /// <summary>
    /// One build-up meter of one target (spec 3.8 <c>BuildupMeters</c>: value, last gain time, penalty-until, penalty level),
    /// in ticks and <see cref="BuildUpMath.UnitsPerPoint"/> units. Decay is lazy (R-3.8-06): <see cref="Units"/> is the value
    /// at <see cref="SettledTick"/>, and it drops from <c>max(SettledTick, DecayStartTick)</c> on; only a gain moves
    /// <see cref="DecayStartTick"/>. A full meter is <see cref="Locked"/> while its status runs (R-3.8-02): it reads full and
    /// ignores gains and reductions until <see cref="Unlock"/> resets it to 0 and starts the repeat-penalty window.
    /// Volatile: <c>default</c> is an empty meter (death, logout, R-3.8-09).
    /// </summary>
    public struct BuildUpMeter
    {
        public int Units;
        public uint SettledTick;
        /// <summary>The meter starts to decay at this tick (the last gain plus the decay delay).</summary>
        public uint DecayStartTick;
        /// <summary>R-3.8-02: full while its triggered status lasts.</summary>
        public bool Locked;
        /// <summary>R-3.8-05: 0 for none, else the level whose gain percent applies until <see cref="PenaltyUntilTick"/>.</summary>
        public int PenaltyLevel;
        public uint PenaltyUntilTick;

        /// <summary>The meter in units at <paramref name="tick"/> (full while locked).</summary>
        public readonly int UnitsAt(BuildUpRule rule, uint tick)
        {
            if (Locked)
                return BuildUpMath.MaxUnits;
            uint from = (int)(DecayStartTick - SettledTick) > 0 ? DecayStartTick : SettledTick;
            return BuildUpMath.Decay(Units, from, tick, rule.Def.DecayPerSec, rule.TickRateHz);
        }

        /// <summary>The meter in whole points for the wire and the HUD, rounded up so a meter above 0 never shows 0.</summary>
        public readonly int PointsAt(BuildUpRule rule, uint tick) =>
            (UnitsAt(rule, tick) + BuildUpMath.UnitsPerPoint - 1) / BuildUpMath.UnitsPerPoint;

        /// <summary>True while the penalty window of the last reset runs (R-3.8-05).</summary>
        public readonly bool InPenaltyWindow(uint tick) => PenaltyLevel > 0 && (int)(PenaltyUntilTick - tick) > 0;

        /// <summary>
        /// A hit's gain (R-3.8-03): decay first, then <paramref name="points"/> scaled by resistance, the repeat penalty and
        /// <paramref name="keepPercent"/>. Returns true when it filled the meter: the meter is then locked (the caller triggers
        /// the status) and the penalty level deepens when the trigger falls in the window of the last reset, else it starts
        /// at 1. A locked meter gains nothing. The decay delay starts again (R-3.8-06).
        /// </summary>
        public bool Gain(BuildUpRule rule, int points, int resistStat, uint tick, int keepPercent = 100)
        {
            if (Locked || points <= 0)
                return false;
            int penalty = InPenaltyWindow(tick) ? BuildUpMath.PenaltyPercent(rule.Def, PenaltyLevel) : 100;
            int gained = BuildUpMath.GainUnits(rule.Def, points, resistStat, penalty, keepPercent);
            Units = Math.Min(BuildUpMath.MaxUnits, UnitsAt(rule, tick) + gained);
            SettledTick = tick;
            DecayStartTick = tick + (uint)rule.DecayDelayTicks;
            if (Units < BuildUpMath.MaxUnits)
                return false;
            int levels = rule.Def.RepeatPenaltyGainPercents.Length;
            PenaltyLevel = InPenaltyWindow(tick) ? Math.Min(levels, PenaltyLevel + 1) : 1;
            PenaltyUntilTick = tick; // the window starts when the effect ends (Unlock)
            Locked = true;
            return true;
        }

        /// <summary>
        /// R-3.8-04: lowers the meter by <paramref name="points"/> (not scaled by resistance). The decay delay keeps running.
        /// A locked meter is not lowered. Returns true when the meter changed.
        /// </summary>
        public bool Reduce(BuildUpRule rule, int points, uint tick)
        {
            if (Locked || points <= 0)
                return false;
            int before = UnitsAt(rule, tick);
            if (before == 0)
                return false;
            Units = Math.Max(0, before - points * BuildUpMath.UnitsPerPoint);
            SettledTick = tick;
            return true;
        }

        /// <summary>
        /// R-3.8-02: the triggered status ended (expiry, cleanse, death, reset): the meter resets to 0 and the repeat-penalty
        /// window of its level starts (R-3.8-05). Nothing for a meter that is not locked.
        /// </summary>
        public bool Unlock(BuildUpRule rule, uint tick)
        {
            if (!Locked)
                return false;
            Locked = false;
            Units = 0;
            SettledTick = tick;
            DecayStartTick = tick;
            PenaltyUntilTick = tick + (uint)rule.PenaltyWindowTicks;
            return true;
        }
    }
}
