using Shared.Protocol;
using Shared.Simulation.Items;

namespace Shared.Simulation.Economy
{
    /// <summary>
    /// The pure rules of a listing (spec 4.5 "M4 listings", M4 decision 6; WP-M4-7; spec 10.2: pure functions with unit tests),
    /// shared by the server, the client's sell form and the economy simulator. Integer only (rule 6). A listing holds
    /// <c>count</c> units at one unit price; a buyer takes any count of them (user answer 2026-11-13) and pays unit price times
    /// that count. The sale tax (<c>FeeRates</c> <c>saleTax</c>) is taken out of what the buyer pays before the rest goes to the
    /// seller by mail, so the seller nets <see cref="Proceeds"/> and the buyer pays the price, as the simulator models it.
    /// </summary>
    public static class ListingMath
    {
        /// <summary>
        /// <paramref name="unitPrice"/> times <paramref name="count"/>, false when either is not positive or the total passes
        /// <paramref name="maxGold"/> (or would overflow).
        /// </summary>
        public static bool TryTotal(long unitPrice, int count, long maxGold, out long total)
        {
            total = 0;
            if (unitPrice <= 0 || count <= 0 || maxGold <= 0 || unitPrice > maxGold / count)
                return false;
            total = unitPrice * count;
            return true;
        }

        /// <summary>A unit price a listing may ask: 1 to <paramref name="maxUnitPrice"/>, the whole listing at most <paramref name="maxGold"/>.</summary>
        public static bool PriceAllowed(long unitPrice, int count, long maxUnitPrice, long maxGold) =>
            unitPrice >= 1 && unitPrice <= maxUnitPrice && TryTotal(unitPrice, count, maxGold, out _);

        /// <summary>What the seller receives of a sale of <paramref name="price"/> after the sale tax <paramref name="tax"/>.</summary>
        public static long Proceeds(long price, long tax)
        {
            if (price < 0 || tax < 0 || tax > price)
                throw new System.ArgumentOutOfRangeException(nameof(tax));
            return price - tax;
        }

        /// <summary>When a listing made at <paramref name="createdUtcMs"/> for <paramref name="durationMs"/> expires.</summary>
        public static long ExpiresAt(long createdUtcMs, int durationMs) => createdUtcMs + durationMs;

        /// <summary>The listing refusal for a unit the trade rules refuse (R-4.5-01: the same units never enter a trade or a listing).</summary>
        public static ListingReason ReasonOf(TradeOfferCheck check)
        {
            switch (check)
            {
                case TradeOfferCheck.Ok: return ListingReason.None;
                case TradeOfferCheck.SellOnly: return ListingReason.ListingSellOnly;
                case TradeOfferCheck.Quest: return ListingReason.ListingQuestItem;
                case TradeOfferCheck.Stolen: return ListingReason.ListingStolen;
                case TradeOfferCheck.NotTradable: return ListingReason.ListingNotTradable;
                default: return ListingReason.ListingEquipped;
            }
        }

        /// <summary>
        /// Whether a buy of <paramref name="count"/> may go on: at least one unit and no more than <paramref name="countLeft"/>, and the
        /// unit price the buyer saw is the listing's.
        /// </summary>
        public static ListingReason CheckBuy(int count, int countLeft, long seenUnitPrice, long unitPrice)
        {
            if (seenUnitPrice != unitPrice)
                return ListingReason.ListingChanged;
            if (count <= 0 || count > countLeft)
                return ListingReason.ListingCountInvalid;
            return ListingReason.None;
        }
    }
}
