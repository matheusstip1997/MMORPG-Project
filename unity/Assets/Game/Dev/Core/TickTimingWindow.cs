#nullable enable
using System;

namespace Game.Gameplay.Dev
{
    /// <summary>What the tick timing panel shows (spec 11.7) over the last window of server ticks.</summary>
    public readonly struct TickTimingSummary
    {
        public TickTimingSummary(int ticks, long p50Us, long p99Us, long worstUs, int overBudget, long worstLateUs, long overruns,
            uint lastTick)
        {
            Ticks = ticks;
            P50Us = p50Us;
            P99Us = p99Us;
            WorstUs = worstUs;
            OverBudget = overBudget;
            WorstLateUs = worstLateUs;
            Overruns = overruns;
            LastTick = lastTick;
        }

        /// <summary>Ticks in the window.</summary>
        public int Ticks { get; }
        public long P50Us { get; }
        public long P99Us { get; }
        public long WorstUs { get; }

        /// <summary>Ticks in the window that ran longer than one tick period (the tick budget, architecture rule 12).</summary>
        public int OverBudget { get; }

        /// <summary>The latest a tick in the window started after its scheduled time.</summary>
        public long WorstLateUs { get; }

        /// <summary>The server's running total of schedule restarts (a tick started a full period late), from <c>TickStats</c>.</summary>
        public long Overruns { get; }

        public uint LastTick { get; }
    }

    /// <summary>
    /// The server's per-tick timing (<c>TickTiming</c> telemetry, backlog J) over a rolling window, for the dev tick panel:
    /// p50, p99 and worst tick time with the same nearest-rank percentile as the bot load test's report, ticks over the
    /// budget, the worst start lateness. A ring of the last <c>capacity</c> ticks; <see cref="Summarize"/> sorts a scratch
    /// copy, so nothing allocates after construction.
    /// </summary>
    public sealed class TickTimingWindow
    {
        private readonly long[] _tickUs;
        private readonly long[] _lateUs;
        private readonly long[] _sorted;
        private readonly long _budgetUs;
        private int _next;
        private int _count;
        private long _overruns;
        private uint _lastTick;

        /// <param name="capacity">Ticks kept: <c>DevTools.tickWindowMs</c> in ticks.</param>
        /// <param name="budgetUs">One tick period in microseconds (1 s / <c>StartingValuesM1.tickRateHz</c>).</param>
        public TickTimingWindow(int capacity, long budgetUs)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            if (budgetUs <= 0)
                throw new ArgumentOutOfRangeException(nameof(budgetUs));
            _tickUs = new long[capacity];
            _lateUs = new long[capacity];
            _sorted = new long[capacity];
            _budgetUs = budgetUs;
        }

        /// <summary>Ticks in the window <c>windowMs</c> long at <c>tickRateHz</c>, at least one.</summary>
        public static int CapacityFor(int windowMs, int tickRateHz) => Math.Max(1, (int)((long)windowMs * tickRateHz / 1000));

        /// <summary>One tick period in microseconds.</summary>
        public static long BudgetUsFor(int tickRateHz) => 1_000_000L / tickRateHz;

        public int Count => _count;

        public void AddTick(uint tick, long tickUs, long lateUs)
        {
            _tickUs[_next] = tickUs;
            _lateUs[_next] = lateUs;
            _next = (_next + 1) % _tickUs.Length;
            if (_count < _tickUs.Length)
                _count++;
            _lastTick = tick;
        }

        /// <summary>A <c>TickStats</c> record: its running total of overruns.</summary>
        public void AddStats(long overrunsTotal) => _overruns = overrunsTotal;

        public void Clear()
        {
            _next = 0;
            _count = 0;
            _overruns = 0;
            _lastTick = 0;
        }

        public TickTimingSummary Summarize()
        {
            int overBudget = 0;
            long worstLate = 0;
            for (int i = 0; i < _count; i++)
            {
                _sorted[i] = _tickUs[i];
                if (_tickUs[i] > _budgetUs)
                    overBudget++;
                worstLate = Math.Max(worstLate, _lateUs[i]);
            }
            Array.Sort(_sorted, 0, _count);
            return new TickTimingSummary(_count, Percentile(_sorted, _count, 50), Percentile(_sorted, _count, 99),
                _count > 0 ? _sorted[_count - 1] : 0, overBudget, worstLate, _overruns, _lastTick);
        }

        /// <summary>Nearest-rank percentile of the first <paramref name="count"/> sorted values; 0 for none.</summary>
        public static long Percentile(long[] sorted, int count, int percent)
        {
            if (count == 0)
                return 0;
            int rank = (int)Math.Ceiling(percent / 100.0 * count);
            return sorted[Math.Min(Math.Max(rank - 1, 0), count - 1)];
        }
    }
}
