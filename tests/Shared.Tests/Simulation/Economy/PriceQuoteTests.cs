using Shared.Simulation.Economy;

namespace Shared.Tests.Simulation.Economy;

/// <summary>
/// The outpost sell price (R-4.2-02, WP-M2-4) as a pure function: base x rarity x dynamic multiplier, applied per unit
/// sequentially (selling n at once pays exactly what n single sales pay), the floor and the maximum, integer rounding per
/// unit, and normalization by active players with a minimum.
/// </summary>
public class PriceQuoteTests
{
    // The placeholder shape of data/tables/DemandCurve.json "price".
    private static readonly DemandCurveShape Curve = new([0, 1000, 2000, 4000, 8000], [100, 100, 80, 55, 30]);

    private static PriceInputs Inputs(long decayedMilli, long expectedMilli = 15_000, int baseGold = 10, int rarity = 100,
        int floor = 30, int max = 100) =>
        new(baseGold, rarity, Curve, floor, max, decayedMilli, expectedMilli);

    [Fact]
    public void Units_up_to_the_expected_sales_sell_at_the_full_price()
    {
        var quote = PriceQuote.Quote(Inputs(0), 15);

        Assert.Equal((150L, 10, 10), (quote.Total, quote.FirstUnit, quote.LastUnit));
    }

    [Fact]
    public void Each_unit_is_priced_after_the_units_before_it_so_the_price_falls_inside_one_sale()
    {
        Span<int> units = stackalloc int[60];
        var quote = PriceQuote.Quote(Inputs(0), 60, units);

        Assert.Equal(10, quote.FirstUnit);
        Assert.True(quote.LastUnit < quote.FirstUnit);
        for (int i = 1; i < units.Length; i++)
            Assert.True(units[i] <= units[i - 1], $"unit {i} costs more than unit {i - 1}");
        long sum = 0;
        foreach (var unit in units)
            sum += unit;
        Assert.Equal(quote.Total, sum);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(100)]
    public void Selling_n_at_once_pays_exactly_what_n_single_sales_pay(int count)
    {
        long bulk = PriceQuote.Quote(Inputs(4_000), count).Total;

        long singles = 0;
        long decayed = 4_000;
        for (int i = 0; i < count; i++)
        {
            singles += PriceQuote.Quote(Inputs(decayed), 1).Total;
            decayed += DemandMath.Unit;
        }

        Assert.Equal(singles, bulk);
    }

    [Fact]
    public void The_dynamic_multiplier_never_goes_below_the_floor_or_above_the_maximum()
    {
        var flooded = PriceQuote.Quote(Inputs(10_000_000), 5);
        Assert.Equal((3, 3), (flooded.FirstUnit, flooded.LastUnit)); // 10 x 30%

        var lowFloor = Inputs(10_000_000, floor: 50);
        Assert.Equal(50, PriceQuote.DynamicPercent(lowFloor, lowFloor.DecayedSalesMilli)); // the floor above the curve's end

        var capped = new PriceInputs(10, 100, new DemandCurveShape([0, 1000], [150, 100]), 30, 120, 0, 15_000);
        Assert.Equal(120, PriceQuote.DynamicPercent(capped, 0));
    }

    [Fact]
    public void Prices_are_rounded_down_per_unit_in_integer_math()
    {
        // 7 gold x 110% rarity x 80% (ratio 2.0) = 6.16 -> 6.
        Assert.Equal(6, PriceQuote.UnitPrice(7, 110, 80));
        var quote = PriceQuote.Quote(Inputs(30_000, baseGold: 7, rarity: 110), 1);
        Assert.Equal(6, quote.FirstUnit);
    }

    [Fact]
    public void The_rarity_multiplier_scales_the_price()
    {
        Assert.Equal(25, PriceQuote.Quote(Inputs(0, rarity: 250), 1).Total);
    }

    [Fact]
    public void Expected_sales_grow_with_active_players_but_never_below_the_minimum()
    {
        Assert.Equal(15_000, DemandMath.ExpectedMilli(1, 3, 5_000)); // one seller at 10-15 online counts as three
        Assert.Equal(15_000, DemandMath.ExpectedMilli(3, 3, 5_000));
        Assert.Equal(500_000, DemandMath.ExpectedMilli(100, 3, 5_000));

        // The same 30 units sold are a heavy load for a small server and nothing for a big one.
        var small = PriceQuote.DynamicPercent(Inputs(30_000, DemandMath.ExpectedMilli(1, 3, 5_000)), 30_000);
        var big = PriceQuote.DynamicPercent(Inputs(30_000, DemandMath.ExpectedMilli(100, 3, 5_000)), 30_000);
        Assert.Equal((80, 100), (small, big));
    }

    [Fact]
    public void A_count_below_one_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceQuote.Quote(Inputs(0), 0));
    }
}
