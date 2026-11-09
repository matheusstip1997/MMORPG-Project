using System;

namespace Shared.Simulation.Progression
{
    /// <summary>
    /// The party share of a kill's exp (spec 6.5 "M4 party", R-3.6-03; M4 decision 1; WP-M4-2), pure integer math
    /// (architecture rule 6). Each eligible member's weight is its share of the damage the eligible members dealt, raised to
    /// at least the floor, and the weights are then scaled so they sum to one (in parts per million, at most
    /// <see cref="ExpMath.PpmOne"/> after rounding down). A member's exp is its own gain (its rank and band modifiers on the
    /// whole base, <see cref="ExpMath.Gain"/>) times its weight, so the split loses no precision to small bases. A party of
    /// one gets the whole gain: a solo kill pays as before.
    /// </summary>
    public static class PartyExpSplit
    {
        /// <summary>
        /// Writes one weight in parts per million per member into <paramref name="weightsPpm"/> (same order as
        /// <paramref name="damage"/>). <paramref name="floorPercent"/> is 0 to 100. With no damage at all every member gets
        /// an equal weight. The weights never sum above <see cref="ExpMath.PpmOne"/>.
        /// </summary>
        public static void Weights(ReadOnlySpan<long> damage, int floorPercent, Span<long> weightsPpm)
        {
            if (weightsPpm.Length < damage.Length)
                throw new ArgumentException("one weight per member", nameof(weightsPpm));
            if (floorPercent < 0 || floorPercent > 100)
                throw new ArgumentOutOfRangeException(nameof(floorPercent));
            int count = damage.Length;
            if (count == 0)
                return;

            long total = 0;
            for (int i = 0; i < count; i++)
            {
                if (damage[i] < 0)
                    throw new ArgumentOutOfRangeException(nameof(damage), "damage is never negative");
                total += damage[i];
            }

            long floorPpm = floorPercent * (ExpMath.PpmOne / 100);
            long sum = 0;
            for (int i = 0; i < count; i++)
            {
                long raw = total == 0 ? ExpMath.PpmOne / count : damage[i] * ExpMath.PpmOne / total;
                if (raw < floorPpm)
                    raw = floorPpm;
                weightsPpm[i] = raw;
                sum += raw;
            }
            if (sum == 0)
            {
                // Only possible with total 0 and more members than parts per million: share evenly, rounded down.
                for (int i = 0; i < count; i++)
                    weightsPpm[i] = ExpMath.PpmOne / count;
                return;
            }
            for (int i = 0; i < count; i++)
                weightsPpm[i] = weightsPpm[i] * ExpMath.PpmOne / sum;
        }

        /// <summary>A member's exp: <paramref name="gain"/> (its own modifiers on the whole base) x its weight, rounded down.</summary>
        public static long Share(long gain, long weightPpm) =>
            gain <= 0 || weightPpm <= 0 ? 0 : gain * weightPpm / ExpMath.PpmOne;
    }
}
