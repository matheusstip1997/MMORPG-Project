using System;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace Shared.Simulation.Contracts
{
    /// <summary>
    /// The pure rules of a contract (spec 5.4 R-5.4-01..04, M4 decision 10, user answers 2026-11-15; WP-M4-10; spec 10.2: pure
    /// functions with unit tests), shared by the server, the client's forms and the bots. Integer only (rule 6); time is UTC ms
    /// from <c>IClock</c>.
    /// </summary>
    public static class ContractMath
    {
        public const long MsPerHour = 3_600_000L;
        public const long MsPerDay = 86_400_000L;

        /// <summary>
        /// The crafter's cancel cost after work started: <paramref name="percent"/> of the contract <paramref name="gold"/>, rounded
        /// down (the crafter never pays more than the rate), paid from the crafter's own gold and destroyed (a sink).
        /// </summary>
        public static long CancelCost(long gold, int percent)
        {
            if (gold < 0 || percent < 0 || percent > 100)
                throw new ArgumentOutOfRangeException(nameof(percent));
            // gold <= EconomySettings.maxGold (9e15 at most), so gold * 100 stays below long.MaxValue.
            return gold / 100 * percent + gold % 100 * percent / 100;
        }

        /// <summary>
        /// What a crafter pays of an expiry charge of <paramref name="amount"/> while holding <paramref name="held"/> gold: all of it,
        /// or what it holds when that is less (user answer 2026-11-16: the rest is dropped, no debt).
        /// </summary>
        public static long ChargeTaken(long amount, long held) => amount <= 0 || held <= 0 ? 0 : Math.Min(amount, held);

        /// <summary>A deadline a customer may choose: <paramref name="minHours"/> to <paramref name="maxHours"/> hours.</summary>
        public static bool DeadlineAllowed(int hours, int minHours, int maxHours) => hours >= minHours && hours <= maxHours;

        /// <summary>When an accepted contract's deadline ends (user answer 2026-11-15: the deadline runs from the acceptance).</summary>
        public static long DeadlineAt(long acceptedUtcMs, int hours) => acceptedUtcMs + hours * MsPerHour;

        /// <summary>When an unaccepted proposal leaves the board and returns (<c>ContractSettings.proposalOpenHours</c>).</summary>
        public static long OpenUntil(long proposedUtcMs, int openHours) => proposedUtcMs + openHours * MsPerHour;

        /// <summary>The UTC day of <paramref name="utcMs"/> (days since 1970-01-01, floor), the per-pair cap's day.</summary>
        public static long UtcDay(long utcMs) => utcMs >= 0 ? utcMs / MsPerDay : (utcMs - MsPerDay + 1) / MsPerDay;

        /// <summary>The pair of two characters either way round (R-5.4-04: A hiring B and B hiring A count for one pair).</summary>
        public static (long Low, long High) PairOf(long a, long b) => a <= b ? (a, b) : (b, a);

        /// <summary>Whether a pair that accepted <paramref name="acceptedToday"/> contracts this UTC day may accept one more.</summary>
        public static bool PairAllows(int acceptedToday, int capPerDay) => acceptedToday < capPerDay;

        /// <summary>The contract value check (R-5.4-04): at least <paramref name="minGold"/>, at most <paramref name="maxGold"/>.</summary>
        public static ContractReason CheckGold(long gold, long minGold, long maxGold) =>
            gold < minGold ? ContractReason.ContractValueTooLow
            : gold > maxGold ? ContractReason.ContractTermsInvalid
            : ContractReason.None;

        /// <summary>The contract refusal for a unit the trade rules refuse (R-5.4-03: the same units never enter a contract).</summary>
        public static ContractReason ReasonOf(TradeOfferCheck check) =>
            check == TradeOfferCheck.Ok ? ContractReason.None : ContractReason.ContractNotTradable;

        /// <summary>Who may cancel a contract in <paramref name="status"/>, and whether it costs (user answer 2026-11-15).</summary>
        /// <returns><see cref="ContractReason.None"/> when <paramref name="role"/> may cancel; <paramref name="costs"/> when the crafter pays.</returns>
        public static ContractReason CheckCancel(ContractStatus status, bool workStarted, ContractRole role, out bool costs)
        {
            costs = false;
            switch (role)
            {
                case ContractRole.Customer:
                    // Free until work started: an open proposal, or an accepted contract the crafter never forged on.
                    return status == ContractStatus.Proposed || (status == ContractStatus.Accepted && !workStarted)
                        ? ContractReason.None
                        : ContractReason.ContractStateInvalid;
                case ContractRole.Crafter:
                    if (status != ContractStatus.Accepted)
                        return ContractReason.ContractStateInvalid; // a running session ends first (cancel the forge)
                    costs = workStarted;
                    return ContractReason.None;
                default:
                    return ContractReason.ContractNotFound;
            }
        }
    }
}
