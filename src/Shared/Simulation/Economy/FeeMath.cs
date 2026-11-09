using System;

namespace Shared.Simulation.Economy
{
    /// <summary>The parts of one fee (spec 4.2 R-4.2-05): the total the payer pays, the owner's share and the destroyed share.</summary>
    public readonly struct FeeSplit
    {
        public FeeSplit(long total, long ownerShare, long adventurerShare)
        {
            Total = total;
            OwnerShare = ownerShare;
            AdventurerShare = adventurerShare;
        }

        public long Total { get; }

        /// <summary>To the owner guild's restricted treasury (R-4.2-06); 0 in an unowned territory.</summary>
        public long OwnerShare { get; }

        /// <summary>To the Adventurer Guild: destroyed (a gold sink, R-4.2-04).</summary>
        public long AdventurerShare { get; }
    }

    /// <summary>
    /// The fee math of <c>FeeService</c> (spec 4.2 R-4.2-05, M4 decision 3; WP-M4-1; spec 10.2: a pure function with unit
    /// tests), shared with the economy simulator. Integer only (rule 6). Rounding: the fee is the percent of the amount
    /// <b>rounded down</b>, so the payer never pays more than the rate (an amount whose fee rounds to 0 pays nothing); a
    /// discount is taken off the fee and rounded down again; in an owned territory the destroyed share is rounded
    /// <b>up</b> (a remainder never goes to the owner) and the owner gets the rest.
    /// </summary>
    public static class FeeMath
    {
        /// <summary><paramref name="percent"/> % of <paramref name="amount"/>, rounded down; exact for any non-negative long.</summary>
        public static long Percent(long amount, int percent)
        {
            if (amount < 0)
                throw new ArgumentOutOfRangeException(nameof(amount));
            if (percent < 0 || percent > 100)
                throw new ArgumentOutOfRangeException(nameof(percent));
            return amount / 100 * percent + amount % 100 * percent / 100;
        }

        /// <summary>R-4.2-05: the Merchant discount applies to auction and shop fees and sale taxes only, never to the contract fee.</summary>
        public static bool DiscountApplies(FeeType type) => type == FeeType.Listing || type == FeeType.SaleTax;

        /// <summary>An owner's rate kept within the row's bounds (R-4.2-05: owners set rates within min and max).</summary>
        public static int ClampRate(int ratePercent, int minPercent, int maxPercent) =>
            Math.Min(maxPercent, Math.Max(minPercent, ratePercent));

        /// <summary>
        /// The fee on <paramref name="amount"/> at <paramref name="ratePercent"/>, less <paramref name="discountPercent"/> % of it
        /// when the discount applies to <paramref name="type"/>; both steps rounded down.
        /// </summary>
        public static long Total(FeeType type, long amount, int ratePercent, int discountPercent)
        {
            if (discountPercent < 0 || discountPercent > 100)
                throw new ArgumentOutOfRangeException(nameof(discountPercent));
            long fee = Percent(amount, ratePercent);
            return DiscountApplies(type) && discountPercent > 0 ? Percent(fee, 100 - discountPercent) : fee;
        }

        /// <summary>
        /// The split of <paramref name="total"/>: unowned, all of it destroyed (no split, R-4.2-05); owned,
        /// <paramref name="destroyedSharePercent"/> % destroyed, rounded up, and the rest to the owner.
        /// </summary>
        public static FeeSplit Split(long total, bool owned, int destroyedSharePercent)
        {
            if (total < 0)
                throw new ArgumentOutOfRangeException(nameof(total));
            if (destroyedSharePercent < 0 || destroyedSharePercent > 100)
                throw new ArgumentOutOfRangeException(nameof(destroyedSharePercent));
            if (!owned)
                return new FeeSplit(total, 0, total);
            long ownerShare = Percent(total, 100 - destroyedSharePercent); // the owner's part rounded down: the destroyed part rounded up
            return new FeeSplit(total, ownerShare, total - ownerShare);
        }
    }
}
