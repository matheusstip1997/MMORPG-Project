using Shared.Data.Tables;
using Shared.Util;

namespace Shared.Simulation.Crafting
{
    /// <summary>
    /// One round of the forge timing bar (spec 5.3 M3 decision 12; WP-M3-10), as the server issues it in <c>ForgeState</c>:
    /// the marker's speed, the target's centre and window (permille of the bar), how long the round lasts and the score of a
    /// press on the centre.
    /// </summary>
    public readonly struct TimingBarParams
    {
        public TimingBarParams(int speedPermillePerSec, int targetPermille, int halfWidthPermille, int durationMs, int hitMaxScore)
        {
            SpeedPermillePerSec = speedPermillePerSec;
            TargetPermille = targetPermille;
            HalfWidthPermille = halfWidthPermille;
            DurationMs = durationMs;
            HitMaxScore = hitMaxScore;
        }

        public int SpeedPermillePerSec { get; }
        public int TargetPermille { get; }
        public int HalfWidthPermille { get; }
        public int DurationMs { get; }
        public int HitMaxScore { get; }

        /// <summary>No round (all zero).</summary>
        public bool IsEmpty => DurationMs <= 0;
    }

    /// <summary>
    /// The forge timing bar's math (spec 5.3 M3 decision 12; WP-M3-10), shared by the server's scoring, the forge window's
    /// preview and the bots, so every side scores a press alike. The marker starts at 0 when the round opens and bounces
    /// along the bar (0 to <see cref="ForgeChallengeDef.BarPermille"/> and back) at its speed; a press scores
    /// <see cref="TimingBarParams.HitMaxScore"/> on the target's centre, falling linearly to 0 at the window's edge (integer,
    /// rounded down), and 0 outside the window, before the round or after it. Integer only, no allocation (rules 6, 7)
    /// except <see cref="Roll"/>, which runs once per round.
    /// </summary>
    public static class TimingBar
    {
        /// <summary>The <c>Pcg32Rng</c> stream of round 0; round n draws from this plus n (the seed is the session's).</summary>
        public const ulong Stream = 0x544D42415200UL; // "TMBAR"

        private const int Sweep = ForgeChallengeDef.BarPermille * 2;

        /// <summary>
        /// The parameters of round <paramref name="round"/> (from 1) of a session: speed and target drawn from the session's
        /// logged seed on the round's own stream (the forge's own rolls keep their order), the rest from the row.
        /// </summary>
        public static TimingBarParams Roll(ulong sessionSeed, int round, ForgeChallengeDef row)
        {
            var rng = new Pcg32Rng(sessionSeed, Stream + (ulong)round);
            int speed = rng.NextInt(row.SpeedMinPermillePerSec, row.SpeedMaxPermillePerSec + 1);
            int target = rng.NextInt(row.TargetMinPermille, row.TargetMaxPermille + 1);
            return new TimingBarParams(speed, target, row.HalfWidthPermille, row.DurationMs, row.HitMaxScore);
        }

        /// <summary>The marker's position (permille) <paramref name="offsetMs"/> after the round opened.</summary>
        public static int Position(in TimingBarParams bar, long offsetMs)
        {
            if (offsetMs <= 0 || bar.SpeedPermillePerSec <= 0)
                return 0;
            long travel = (long)bar.SpeedPermillePerSec * offsetMs / 1_000;
            int along = (int)(travel % Sweep);
            return along <= ForgeChallengeDef.BarPermille ? along : Sweep - along;
        }

        /// <summary>The score of a press <paramref name="offsetMs"/> after the round opened (0 outside the window or the round).</summary>
        public static int Score(in TimingBarParams bar, long offsetMs)
        {
            if (offsetMs < 0 || offsetMs > bar.DurationMs || bar.HalfWidthPermille <= 0)
                return 0;
            int distance = Position(in bar, offsetMs) - bar.TargetPermille;
            if (distance < 0)
                distance = -distance;
            if (distance >= bar.HalfWidthPermille)
                return 0;
            return (int)((long)bar.HitMaxScore * (bar.HalfWidthPermille - distance) / bar.HalfWidthPermille);
        }

        /// <summary>
        /// The first offset at or after <paramref name="afterMs"/> at which the marker reaches the target's centre (the
        /// bots' and the tests' perfect press); -1 when it does not within the round.
        /// </summary>
        public static long BestOffsetMs(in TimingBarParams bar, long afterMs)
        {
            if (bar.SpeedPermillePerSec <= 0)
                return -1;
            if (afterMs < 0)
                afterMs = 0;
            long speed = bar.SpeedPermillePerSec;
            long travelAfter = speed * afterMs / 1_000;
            long lap = travelAfter / Sweep * Sweep;
            long best = -1;
            // The centre is crossed going out (target) and coming back (sweep - target) on every lap.
            for (long l = lap - Sweep; l <= lap + Sweep; l += Sweep)
            {
                best = Earlier(best, Crossing(l + bar.TargetPermille, speed, afterMs));
                best = Earlier(best, Crossing(l + Sweep - bar.TargetPermille, speed, afterMs));
            }
            return best >= 0 && best <= bar.DurationMs ? best : -1;
        }

        /// <summary>The first whole ms at which the marker has travelled <paramref name="travel"/>; -1 when before the floor.</summary>
        private static long Crossing(long travel, long speed, long afterMs)
        {
            if (travel < 0)
                return -1;
            long offset = (travel * 1_000 + speed - 1) / speed;
            return offset >= afterMs ? offset : -1;
        }

        private static long Earlier(long a, long b) => a < 0 ? b : b < 0 ? a : a < b ? a : b;
    }
}
