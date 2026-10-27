using System;

namespace Shared.Simulation.Stats
{
    /// <summary>
    /// A meter that only falls with time: intoxication and food satiety (R-3.4-03, WP-M2-2). It stores thousandths of a
    /// point at an anchor time (UTC ms from <c>IClock</c>) and derives the current value on read, so it needs no update per
    /// tick, and a saved meter decays across the time the character was offline ("computed from timestamps on login").
    /// Never below 0; a time before the anchor (a clock that went backwards) decays nothing. Max and rate are passed in on
    /// every call (they are config, <c>ConsumptionMeterSettings</c>). No allocation.
    /// </summary>
    public struct DecayMeter
    {
        public const int MilliPerPoint = 1000;
        public const int MsPerMinute = 60_000;

        private long _milli;
        private long _anchorMs;

        /// <summary>Sets the value at <paramref name="atMs"/> (load from a save: its saved value and time). Negative counts as 0.</summary>
        public void Set(long milli, long atMs)
        {
            _milli = Math.Max(0, milli);
            _anchorMs = atMs;
        }

        /// <summary>Thousandths of a point at <paramref name="nowMs"/>, after decaying <paramref name="decayPerMinute"/> points a minute.</summary>
        public long MilliAt(long nowMs, int decayPerMinute)
        {
            if (_milli == 0 || decayPerMinute <= 0 || nowMs <= _anchorMs)
                return _milli;
            long elapsed = nowMs - _anchorMs;
            // Points per minute x elapsed ms / 60 = thousandths per elapsed ms; compare times first so a long absence
            // cannot overflow.
            long msToEmpty = (_milli * MsPerMinute + (long)decayPerMinute * MilliPerPoint - 1) / ((long)decayPerMinute * MilliPerPoint);
            if (elapsed >= msToEmpty)
                return 0;
            return Math.Max(0, _milli - elapsed * decayPerMinute * MilliPerPoint / MsPerMinute);
        }

        /// <summary>Whole points at <paramref name="nowMs"/>, rounded up, so a meter shows 0 only when it is empty.</summary>
        public int Current(long nowMs, int decayPerMinute)
        {
            long milli = MilliAt(nowMs, decayPerMinute);
            return (int)Math.Min(int.MaxValue, (milli + MilliPerPoint - 1) / MilliPerPoint);
        }

        /// <summary>
        /// Adds <paramref name="points"/> (a potion's toxicity, food's satiety; M3), capped at <paramref name="max"/>.
        /// Returns the whole points the meter shows more.
        /// </summary>
        public int Add(int points, long nowMs, int max, int decayPerMinute)
        {
            if (points <= 0)
                return 0;
            int before = Current(nowMs, decayPerMinute);
            long cap = (long)Math.Max(0, max) * MilliPerPoint;
            Store(Math.Min(cap, MilliAt(nowMs, decayPerMinute) + (long)points * MilliPerPoint), nowMs);
            return Current(nowMs, decayPerMinute) - before;
        }

        /// <summary>Folds the decay up to <paramref name="nowMs"/> into the stored value (before a save captures it).</summary>
        public void Settle(long nowMs, int decayPerMinute) => Store(MilliAt(nowMs, decayPerMinute), nowMs);

        private void Store(long milli, long nowMs)
        {
            _milli = milli;
            _anchorMs = Math.Max(_anchorMs, nowMs);
        }
    }
}
