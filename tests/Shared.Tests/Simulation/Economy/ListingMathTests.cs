using Shared.Protocol;
using Shared.Simulation.Economy;
using Shared.Simulation.Items;

namespace Shared.Tests.Simulation.Economy;

/// <summary>
/// The pure listing rules (spec 4.5, M4 decision 6; WP-M4-7): the total of a price times a count without overflow and within the
/// gold cap, the allowed asking prices, the seller's proceeds after the sale tax (the buyer pays the price, the seller nets the
/// price less the tax, as the economy simulator models it), the expiry, the buy checks (any count up to what is left, the price
/// the buyer saw; user answer 2026-11-13) and the listing refusal of each trade rule (R-4.5-01).
/// </summary>
public class ListingMathTests
{
    [Theory]
    [InlineData(10, 4, 1_000, true, 40)]
    [InlineData(250, 4, 1_000, true, 1_000)] // exactly the cap
    [InlineData(251, 4, 1_000, false, 0)] // above the cap
    [InlineData(0, 4, 1_000, false, 0)]
    [InlineData(10, 0, 1_000, false, 0)]
    [InlineData(long.MaxValue, 2, long.MaxValue, false, 0)] // would overflow
    public void The_total_is_price_times_count_within_the_gold_cap(long unitPrice, int count, long maxGold, bool ok, long total)
    {
        Assert.Equal(ok, ListingMath.TryTotal(unitPrice, count, maxGold, out long result));
        Assert.Equal(total, result);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(100, 1, true)] // the highest unit price
    [InlineData(101, 1, false)]
    [InlineData(0, 1, false)]
    [InlineData(100, 11, false)] // 1,100 passes the gold cap of 1,000
    public void A_listing_asks_between_one_and_the_highest_unit_price_and_the_whole_within_the_gold_cap(long unitPrice, int count, bool ok)
    {
        Assert.Equal(ok, ListingMath.PriceAllowed(unitPrice, count, maxUnitPrice: 100, maxGold: 1_000));
    }

    [Fact]
    public void The_seller_nets_the_price_less_the_sale_tax_and_the_buyer_pays_the_price()
    {
        long price = ListingMath.TryTotal(100, 3, long.MaxValue, out long total) ? total : 0;
        long tax = FeeMath.Total(FeeType.SaleTax, price, 5, 0);
        Assert.Equal((300L, 15L, 285L), (price, tax, ListingMath.Proceeds(price, tax)));
        Assert.Equal(price, ListingMath.Proceeds(price, tax) + tax); // conservation: proceeds plus the destroyed tax
        Assert.Throws<ArgumentOutOfRangeException>(() => ListingMath.Proceeds(10, 11));
    }

    [Fact]
    public void A_listing_expires_after_its_duration()
    {
        Assert.Equal(1_000 + 43_200_000L, ListingMath.ExpiresAt(1_000, 43_200_000));
    }

    [Theory]
    [InlineData(1, 4, 10, 10, ListingReason.None)]
    [InlineData(4, 4, 10, 10, ListingReason.None)] // all that is left
    [InlineData(5, 4, 10, 10, ListingReason.ListingCountInvalid)]
    [InlineData(0, 4, 10, 10, ListingReason.ListingCountInvalid)]
    [InlineData(1, 4, 9, 10, ListingReason.ListingChanged)] // the buyer saw another price
    public void A_buyer_takes_any_count_up_to_what_is_left_at_the_price_seen(int count, int left, long seen, long price, ListingReason reason)
    {
        Assert.Equal(reason, ListingMath.CheckBuy(count, left, seen, price));
    }

    [Theory]
    [InlineData(TradeOfferCheck.Ok, ListingReason.None)]
    [InlineData(TradeOfferCheck.SellOnly, ListingReason.ListingSellOnly)]
    [InlineData(TradeOfferCheck.Quest, ListingReason.ListingQuestItem)]
    [InlineData(TradeOfferCheck.Stolen, ListingReason.ListingStolen)]
    [InlineData(TradeOfferCheck.NotTradable, ListingReason.ListingNotTradable)]
    [InlineData(TradeOfferCheck.Equipped, ListingReason.ListingEquipped)]
    public void The_units_a_trade_refuses_a_listing_refuses_with_its_own_reason(TradeOfferCheck check, ListingReason reason)
    {
        Assert.Equal(reason, ListingMath.ReasonOf(check));
    }
}
