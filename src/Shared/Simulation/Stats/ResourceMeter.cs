using System;

namespace Shared.Simulation.Stats
{
    public enum ResourceKind : byte
    {
        Hp = 0,
        Mp = 1,
        Stamina = 2,
    }

    /// <summary>
    /// Why a spend was refused. D4 maps these to UI reason codes (spec 9, for example <c>NoStamina</c>) when it wires
    /// stamina into actions.
    /// </summary>
    public enum SpendResult : byte
    {
        Ok = 0,

        /// <summary>The meter holds less than the cost; nothing was spent.</summary>
        NotEnough = 1,

        /// <summary>The cost was zero or negative; nothing was spent.</summary>
        InvalidAmount = 2,
    }

    /// <summary>
    /// One regenerating meter (spec 3.4 R-3.4-03), computed lazily from timestamps (like the charge meters of 3.3): it
    /// stores the value at an anchor time and the time regeneration may start, and derives the current value on read,
    /// so it needs no per-tick update. Values are kept in thousandths of a point, so regeneration of a few points per
    /// second is exact at any read rate (rate per second x elapsed ms = thousandths). A spend or drain restarts the
    /// regeneration delay; a restore does not. Max and rate are passed in on every call (they are stats).
    /// </summary>
    public struct ResourceMeter
    {
        public const int MilliPerPoint = 1000;

        private long _milli;
        private long _anchorMs;
        private long _regenFromMs;

        /// <summary>Sets the value (spawn, respawn, load). Regeneration may start at once.</summary>
        public void Set(int value, long nowMs)
        {
            _milli = (long)Math.Max(0, value) * MilliPerPoint;
            _anchorMs = nowMs;
            _regenFromMs = nowMs;
        }

        /// <summary>Whole points at <paramref name="nowMs"/>, rounded down.</summary>
        public int Current(long nowMs, int max, int regenPerSec, bool regenAtZero) =>
            (int)(MilliAt(nowMs, max, regenPerSec, regenAtZero) / MilliPerPoint);

        /// <summary>
        /// Thousandths of a point at <paramref name="nowMs"/>: the stored value clamped to <paramref name="max"/>,
        /// plus regeneration since the later of the anchor and the end of the delay. A clock that goes backwards
        /// adds nothing. With <paramref name="regenAtZero"/> false an empty meter stays empty (HP of a dead entity).
        /// </summary>
        public long MilliAt(long nowMs, int max, int regenPerSec, bool regenAtZero)
        {
            long maxMilli = (long)Math.Max(0, max) * MilliPerPoint;
            long value = Math.Min(_milli, maxMilli);
            if (regenPerSec <= 0 || value >= maxMilli || (_milli == 0 && !regenAtZero))
                return value;
            long start = Math.Max(_anchorMs, _regenFromMs);
            if (nowMs <= start)
                return value;
            long elapsed = nowMs - start;
            long missing = maxMilli - value;
            // Compare times instead of multiplying, so a long absence cannot overflow.
            if (elapsed >= (missing + regenPerSec - 1) / regenPerSec)
                return maxMilli;
            return value + elapsed * regenPerSec;
        }

        /// <summary>Spends all of <paramref name="amount"/> or nothing.</summary>
        public SpendResult TrySpend(int amount, long nowMs, int max, int regenPerSec, bool regenAtZero, int regenDelayMs)
        {
            if (amount <= 0)
                return SpendResult.InvalidAmount;
            long current = MilliAt(nowMs, max, regenPerSec, regenAtZero);
            long cost = (long)amount * MilliPerPoint;
            if (current < cost)
                return SpendResult.NotEnough;
            Store(current - cost, nowMs);
            _regenFromMs = nowMs + Math.Max(0, regenDelayMs);
            return SpendResult.Ok;
        }

        /// <summary>Removes up to <paramref name="amount"/> (damage); returns the whole points the display lost.</summary>
        public int Drain(int amount, long nowMs, int max, int regenPerSec, bool regenAtZero, int regenDelayMs)
        {
            if (amount <= 0)
                return 0;
            long current = MilliAt(nowMs, max, regenPerSec, regenAtZero);
            long after = Math.Max(0, current - (long)amount * MilliPerPoint);
            Store(after, nowMs);
            _regenFromMs = nowMs + Math.Max(0, regenDelayMs);
            return (int)(current / MilliPerPoint - after / MilliPerPoint);
        }

        /// <summary>Adds up to <paramref name="amount"/> (heal, potion), capped at max; returns the whole points gained.</summary>
        public int Restore(int amount, long nowMs, int max, int regenPerSec, bool regenAtZero)
        {
            if (amount <= 0)
                return 0;
            long current = MilliAt(nowMs, max, regenPerSec, regenAtZero);
            long after = Math.Min((long)Math.Max(0, max) * MilliPerPoint, current + (long)amount * MilliPerPoint);
            Store(after, nowMs);
            return (int)(after / MilliPerPoint - current / MilliPerPoint);
        }

        /// <summary>Folds regeneration up to now into the stored value, before max or rate change.</summary>
        public void Settle(long nowMs, int max, int regenPerSec, bool regenAtZero) =>
            Store(MilliAt(nowMs, max, regenPerSec, regenAtZero), nowMs);

        private void Store(long milli, long nowMs)
        {
            _milli = milli;
            _anchorMs = nowMs;
        }
    }
}
