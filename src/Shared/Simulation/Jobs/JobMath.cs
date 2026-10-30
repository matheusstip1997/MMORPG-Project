using System;
using Shared.Simulation.Progression;

namespace Shared.Simulation.Jobs
{
    /// <summary>
    /// Job XP of R-5.1-04, pure integer math (architecture rule 6): xp = baseXp x tierPercent / 100 x repeatPercent / 100,
    /// rounded down. The tier factor makes harder nodes and recipes pay more; the repeat factor is the diminishing return for
    /// repeating the same node or recipe inside the job's recent window, never below its floor. Every number comes from
    /// <c>JobXpWeights</c> and is a placeholder (balance is deferred, M3 plan section 2).
    /// </summary>
    public static class JobMath
    {
        /// <summary>The difficulty percent of <paramref name="tier"/> (1-based): below 1 the first entry, past the end the last.</summary>
        public static int TierPercent(int[] tierPercents, int tier)
        {
            if (tierPercents.Length == 0)
                return 0;
            int index = tier - 1;
            if (index < 0)
                index = 0;
            else if (index >= tierPercents.Length)
                index = tierPercents.Length - 1;
            return tierPercents[index];
        }

        /// <summary>
        /// The percent kept after <paramref name="repeats"/> earlier repeats in the window: 100 - repeats x step, at least
        /// <paramref name="floorPercent"/>, at most 100.
        /// </summary>
        public static int RepeatPercent(int repeats, int stepPercent, int floorPercent)
        {
            long kept = 100L - (long)Math.Max(0, repeats) * Math.Max(0, stepPercent);
            if (kept < floorPercent)
                kept = floorPercent;
            return (int)Math.Min(100, Math.Max(0, kept));
        }

        /// <summary>The XP of one activity: <paramref name="baseXp"/> x tier percent / 100 x repeat percent / 100, rounded down.</summary>
        public static int XpFor(int baseXp, int tierPercent, int repeatPercent) =>
            baseXp <= 0 || tierPercent <= 0 || repeatPercent <= 0 ? 0 : (int)((long)baseXp * tierPercent * repeatPercent / 10_000);

        /// <summary>
        /// Adds <paramref name="gain"/> to a job at <paramref name="level"/> with <paramref name="xp"/> into that level, taking
        /// as many level-ups as it pays for. <paramref name="xpToNext"/> holds levels 1 to <paramref name="maxLevel"/> - 1
        /// (index level - 1); at the cap the XP stays 0 and the rest is dropped (like character exp, <see cref="ExpMath.Apply"/>).
        /// Returns the levels gained; each one gives <c>JobSettings.pointsPerLevel</c> job points.
        /// </summary>
        public static int AddXp(ref int level, ref long xp, long gain, int[] xpToNext, int maxLevel) =>
            ExpMath.Apply(ref level, ref xp, gain, xpToNext, maxLevel);
    }
}
