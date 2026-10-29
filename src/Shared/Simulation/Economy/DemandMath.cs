using System;

namespace Shared.Simulation.Economy
{
    /// <summary>
    /// A demand curve's points (R-4.2-03, <c>DemandCurve</c> rows): ratio in thousandths to a whole percent, linear between
    /// points and flat beyond the ends. The arrays are the table's; the curve never copies or changes them.
    /// </summary>
    public readonly struct DemandCurveShape
    {
        private readonly int[] _ratioPermille;
        private readonly int[] _percent;

        public DemandCurveShape(int[] ratioPermille, int[] percent)
        {
            if (ratioPermille == null || percent == null || ratioPermille.Length == 0 || ratioPermille.Length != percent.Length)
                throw new ArgumentException("a curve needs one percent per ratio point and at least one point");
            _ratioPermille = ratioPermille;
            _percent = percent;
        }

        /// <summary>The percent at <paramref name="ratioPermille"/> (integer interpolation, rounded toward the lower point).</summary>
        public int Evaluate(long ratioPermille)
        {
            int last = _ratioPermille.Length - 1;
            if (ratioPermille <= _ratioPermille[0])
                return _percent[0];
            if (ratioPermille >= _ratioPermille[last])
                return _percent[last];
            int i = 1;
            while (_ratioPermille[i] < ratioPermille)
                i++;
            long x0 = _ratioPermille[i - 1], x1 = _ratioPermille[i];
            long y0 = _percent[i - 1], y1 = _percent[i];
            return (int)(y0 + (y1 - y0) * (ratioPermille - x0) / (x1 - x0));
        }
    }

    /// <summary>
    /// The pure math of the demand loop (R-4.2-03, spec 10.2: pure functions with unit tests). Integer only (rule 6):
    /// counters are thousandths of a unit (one kill or one unit sold = 1000), ratios thousandths, outputs whole percents.
    /// Shared so the economy simulator (WP-M2-12) runs the same numbers as the server.
    /// </summary>
    public static class DemandMath
    {
        /// <summary>One kill or one unit sold, in counter units.</summary>
        public const long Unit = 1000;

        /// <summary>Ratios above this are treated as this (far beyond any curve point).</summary>
        public const long MaxRatioPermille = 1_000_000_000;

        /// <summary>
        /// The expected decayed count for the active players: at least <paramref name="minActivePlayers"/> count, so the
        /// loop behaves at 10-15 players as at hundreds (R-4.2-02, R-4.2-03).
        /// </summary>
        public static long ExpectedMilli(int activePlayers, int minActivePlayers, int expectedPerPlayerMilli) =>
            (long)Math.Max(activePlayers, minActivePlayers) * expectedPerPlayerMilli;

        /// <summary>decayed / expected, in thousandths (1000 = as expected).</summary>
        public static long RatioPermille(long decayedMilli, long expectedMilli)
        {
            if (expectedMilli <= 0)
                throw new ArgumentOutOfRangeException(nameof(expectedMilli));
            if (decayedMilli <= 0)
                return 0;
            if (decayedMilli >= MaxRatioPermille / 1000 * expectedMilli)
                return MaxRatioPermille;
            return decayedMilli * 1000 / expectedMilli;
        }

        /// <summary>One control step of decay: keeps <paramref name="retentionPermille"/> thousandths (rounded down).</summary>
        public static long Decay(long decayedMilli, int retentionPermille) => decayedMilli * retentionPermille / 1000;

        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

        /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> by at most <paramref name="maxStep"/>.</summary>
        public static int Smooth(int current, int target, int maxStep)
        {
            if (target > current + maxStep)
                return current + maxStep;
            if (target < current - maxStep)
                return current - maxStep;
            return target;
        }

        /// <summary>
        /// One control step of an output (spawn delay scale, reward multiplier): the curve at the ratio, clamped, then moved
        /// at most <paramref name="maxStep"/> from <paramref name="current"/>, and clamped again.
        /// </summary>
        public static int Step(int current, in DemandCurveShape curve, long ratioPermille, int min, int max, int maxStep) =>
            Clamp(Smooth(current, Clamp(curve.Evaluate(ratioPermille), min, max), maxStep), min, max);
    }
}
