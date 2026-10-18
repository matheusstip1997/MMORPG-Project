using System;

namespace Shared.Simulation
{
    /// <summary>
    /// Spec 2.2: the server validates the distance moved per tick against maxSpeed(stat + mount) with a tolerance and
    /// counts violations. Pure integer math, so bots and tests can run the same check.
    /// </summary>
    public static class SpeedCheck
    {
        /// <summary>
        /// The longest distance one tick may cover, in raw Q24.8 cm: <see cref="MovementSim.StepPerTick"/> plus
        /// <paramref name="tolerancePercent"/> percent, rounded down.
        /// </summary>
        public static long AllowedRawPerTick(int maxSpeedCmPerSec, int tickRateHz, int tolerancePercent)
        {
            if (tolerancePercent < 0)
                throw new ArgumentOutOfRangeException(nameof(tolerancePercent), "must not be negative");
            long step = MovementSim.StepPerTick(maxSpeedCmPerSec, tickRateHz);
            return step * (100 + tolerancePercent) / 100;
        }

        /// <summary>True when the move from <paramref name="from"/> to <paramref name="to"/> is longer than allowed.</summary>
        public static bool Exceeds(FixVec2 from, FixVec2 to, long allowedRawPerTick)
        {
            long dx = (long)to.X - from.X;
            long dy = (long)to.Y - from.Y;
            // Raw deltas reach 2^32, whose square overflows long; a delta longer than the limit on one axis already fails.
            if (dx > allowedRawPerTick || dx < -allowedRawPerTick || dy > allowedRawPerTick || dy < -allowedRawPerTick)
                return true;
            return dx * dx + dy * dy > allowedRawPerTick * allowedRawPerTick;
        }

        /// <summary>The move's length in whole cm, rounded down (for telemetry).</summary>
        public static long DistanceCm(FixVec2 from, FixVec2 to)
        {
            long dx = Math.Abs((long)to.X - from.X) >> FixedPoint.FractionBits;
            long dy = Math.Abs((long)to.Y - from.Y) >> FixedPoint.FractionBits;
            return IntegerSqrt(dx * dx + dy * dy);
        }

        private static long IntegerSqrt(long value)
        {
            if (value <= 0)
                return 0;
            long root = (long)Math.Sqrt(value);
            while (root * root > value)
                root--;
            while ((root + 1) * (root + 1) <= value)
                root++;
            return root;
        }
    }
}
