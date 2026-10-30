using System;

namespace Shared.Simulation.Economy
{
    /// <summary>
    /// The demand loop's respawn delay (R-4.2-03, R-5.2-03): one rule for monsters and resource nodes, server and tools.
    /// </summary>
    public static class RespawnMath
    {
        /// <summary>
        /// <paramref name="baseDelay"/> scaled by <paramref name="percent"/> (100 = the base delay), never shorter than
        /// <paramref name="floor"/> (or the base delay when that is shorter) and never shorter than 1, so a respawn never
        /// stops and never comes at once. Any unit (ticks or ms) as long as the base and the floor share it.
        /// </summary>
        public static long ScaledDelay(long baseDelay, int percent, long floor)
        {
            if (baseDelay < 0)
                throw new ArgumentOutOfRangeException(nameof(baseDelay), "The base delay is not negative.");
            if (percent < 0)
                throw new ArgumentOutOfRangeException(nameof(percent), "The scale is not negative.");
            long scaled = baseDelay * percent / 100;
            return Math.Max(Math.Max(scaled, Math.Min(baseDelay, floor)), 1);
        }
    }
}
