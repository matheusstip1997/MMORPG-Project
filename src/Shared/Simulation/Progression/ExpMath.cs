using System;

namespace Shared.Simulation.Progression
{
    /// <summary>
    /// Exp formula of R-3.6-03, pure integer math (architecture rule 6): exp = base x rankModifier x bandModifier, rounded
    /// down, with no floor. The rank modifier is a percent; the band modifier is in parts per million so the multiplicative
    /// penalty above the band keeps its precision over many levels. The numbers come from the config tables
    /// (<see cref="ProgressionCurves"/>); every one is a placeholder (spec 3.6 "M2 placeholder curves").
    /// </summary>
    public static class ExpMath
    {
        /// <summary>x1.0 in parts per million.</summary>
        public const long PpmOne = 1_000_000;

        /// <summary>
        /// Band modifier in parts per million (R-3.6-03): 1.0 inside [<paramref name="bandMin"/>, <paramref name="bandMax"/>];
        /// below the band +<paramref name="belowPercentPerLevel"/>% per level under its minimum, at most
        /// <paramref name="belowCapPercent"/>%; above it x<paramref name="aboveKeepPercent"/>/100 once per level over its
        /// maximum (multiplicative, reaching 0: no floor).
        /// </summary>
        public static long BandPpm(int level, int bandMin, int bandMax, int belowPercentPerLevel, int belowCapPercent, int aboveKeepPercent)
        {
            if (level < bandMin)
            {
                long bonus = Math.Min((long)belowCapPercent, (long)belowPercentPerLevel * (bandMin - level));
                return PpmOne + bonus * (PpmOne / 100);
            }
            long ppm = PpmOne;
            for (int gap = level - bandMax; gap > 0 && ppm > 0; gap--)
                ppm = ppm * aboveKeepPercent / 100;
            return ppm;
        }

        /// <summary>
        /// Rank modifier percent for <paramref name="rankDiff"/> (monster rank minus player rank) from contiguous rows that
        /// start at <paramref name="firstDiff"/>; a difference beyond them is clamped to the nearest row.
        /// </summary>
        public static int RankPercent(int rankDiff, int firstDiff, int[] percents)
        {
            int index = rankDiff - firstDiff;
            if (index < 0)
                index = 0;
            else if (index >= percents.Length)
                index = percents.Length - 1;
            return percents[index];
        }

        /// <summary>Exp of one kill: <paramref name="baseExp"/> x percent / 100 x ppm / 1 000 000, rounded down.</summary>
        public static long Gain(long baseExp, int rankPercent, long bandPpm) =>
            baseExp <= 0 || rankPercent <= 0 || bandPpm <= 0 ? 0 : baseExp * rankPercent * bandPpm / (100 * PpmOne);

        /// <summary>
        /// Adds <paramref name="gain"/> to a character at <paramref name="level"/> with <paramref name="exp"/> into that
        /// level, taking as many level-ups as it pays for (several in one kill are allowed). <paramref name="expToNext"/>
        /// holds the exp of levels 1 to <paramref name="maxLevel"/> - 1 (index level - 1). At <paramref name="maxLevel"/>
        /// the exp stays 0 and the rest is dropped (placeholder cap, TODO(open) in spec 3.6). Returns the levels gained.
        /// </summary>
        public static int Apply(ref int level, ref long exp, long gain, int[] expToNext, int maxLevel)
        {
            if (gain < 0)
                throw new ArgumentOutOfRangeException(nameof(gain));
            int start = level;
            if (level >= maxLevel)
            {
                exp = 0;
                return 0;
            }
            exp += gain;
            while (level < maxLevel && exp >= expToNext[level - 1])
            {
                exp -= expToNext[level - 1];
                level++;
            }
            if (level >= maxLevel)
                exp = 0;
            return level - start;
        }
    }
}
