using Shared.Simulation.Economy;

namespace Shared.Tests.Simulation.Economy;

/// <summary>
/// The fee math of FeeService (spec 4.2 R-4.2-05, M4 decision 3; WP-M4-1) as a pure function: the percent rounded down so the
/// payer never pays more than the rate, every fee type, the Merchant discount on listing fees and sale taxes only, the owner
/// rate kept within its bounds, the owned split with the remainder to the sink, and the unowned case (all destroyed).
/// </summary>
public class FeeMathTests
{
    [Theory]
    [InlineData(FeeType.Listing, 1_000, 2, 20)]
    [InlineData(FeeType.SaleTax, 1_000, 5, 50)]
    [InlineData(FeeType.Contract, 1_000, 5, 50)]
    [InlineData(FeeType.Listing, 1_234, 2, 24)] // 24.68 rounded down
    [InlineData(FeeType.SaleTax, 1_239, 5, 61)] // 61.95 rounded down
    [InlineData(FeeType.Contract, 99, 5, 4)] // 4.95 rounded down
    public void Each_fee_type_is_its_percent_of_the_amount_rounded_down(FeeType type, long amount, int rate, long fee)
    {
        Assert.Equal(fee, FeeMath.Total(type, amount, rate, 0));
    }

    [Fact]
    public void The_payer_never_pays_more_than_the_rate_and_less_than_one_gold_below_it()
    {
        foreach (int rate in new[] { 0, 1, 2, 5, 7, 15, 99, 100 })
        {
            for (long amount = 0; amount <= 2_000; amount++)
            {
                long fee = FeeMath.Total(FeeType.SaleTax, amount, rate, 0);
                Assert.True(fee * 100 <= amount * rate, $"{fee} on {amount} at {rate}%");
                Assert.True((fee + 1) * 100 > amount * rate, $"{fee} on {amount} at {rate}%");
            }
        }
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 5)]
    [InlineData(19, 5)] // 0.95: rounds to no fee
    [InlineData(49, 2)] // 0.98
    [InlineData(1_000_000, 0)]
    public void A_zero_amount_a_zero_rate_or_an_amount_below_one_whole_gold_of_fee_pays_nothing(long amount, int rate)
    {
        Assert.Equal(0, FeeMath.Total(FeeType.SaleTax, amount, rate, 0));
    }

    [Fact]
    public void The_smallest_amount_that_owes_a_fee_owes_one_gold()
    {
        Assert.Equal(1, FeeMath.Total(FeeType.SaleTax, 20, 5, 0));
        Assert.Equal(1, FeeMath.Total(FeeType.Listing, 50, 2, 0));
    }

    [Fact]
    public void Amounts_up_to_the_largest_long_never_overflow()
    {
        Assert.Equal(long.MaxValue / 100 * 5 + long.MaxValue % 100 * 5 / 100, FeeMath.Percent(long.MaxValue, 5));
        Assert.Equal(long.MaxValue, FeeMath.Percent(long.MaxValue, 100));
        Assert.Equal((long)(9_000_000_000_000_000m * 15 / 100), FeeMath.Percent(9_000_000_000_000_000, 15));
    }

    [Fact]
    public void Negative_amounts_and_percents_outside_0_to_100_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FeeMath.Percent(-1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => FeeMath.Percent(10, 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => FeeMath.Total(FeeType.Listing, 10, 5, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FeeMath.Split(-1, false, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => FeeMath.Split(10, true, 101));
    }

    [Theory]
    [InlineData(FeeType.Listing, true)]
    [InlineData(FeeType.SaleTax, true)]
    [InlineData(FeeType.Contract, false)]
    public void The_Merchant_discount_applies_to_auction_and_shop_fees_and_sale_taxes_only(FeeType type, bool applies)
    {
        // R-4.2-05; the discount size is TODO(open): 20 % here is a test value only.
        Assert.Equal(applies, FeeMath.DiscountApplies(type));
        long full = FeeMath.Total(type, 10_000, 5, 0);
        Assert.Equal(applies ? 400 : 500, FeeMath.Total(type, 10_000, 5, 20));
        Assert.Equal(500, full);
    }

    [Fact]
    public void A_discount_is_taken_off_the_fee_and_rounded_down_again()
    {
        Assert.Equal(45, FeeMath.Total(FeeType.SaleTax, 1_000, 5, 10)); // 50, less 10 %
        Assert.Equal(44, FeeMath.Total(FeeType.SaleTax, 999, 5, 10)); // 49.95 -> 49, then 44.1 -> 44
        Assert.Equal(0, FeeMath.Total(FeeType.SaleTax, 39, 5, 10)); // 1.95 -> 1, then 0.9 -> 0
        Assert.Equal(0, FeeMath.Total(FeeType.Listing, 1_000, 2, 100));
    }

    [Theory]
    [InlineData(1, 0, 10, 1)]
    [InlineData(-3, 0, 10, 0)]
    [InlineData(25, 0, 10, 10)]
    [InlineData(3, 4, 8, 4)]
    public void An_owner_rate_is_kept_within_the_rows_bounds(int rate, int min, int max, int expected)
    {
        Assert.Equal(expected, FeeMath.ClampRate(rate, min, max));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(57)]
    [InlineData(1_000)]
    public void Unowned_the_whole_fee_is_destroyed_with_no_split(long total)
    {
        var split = FeeMath.Split(total, owned: false, destroyedSharePercent: 50);
        Assert.Equal(total, split.Total);
        Assert.Equal(0, split.OwnerShare);
        Assert.Equal(total, split.AdventurerShare);
    }

    [Theory]
    [InlineData(100, 50, 50, 50)]
    [InlineData(101, 50, 50, 51)] // the odd gold goes to the sink
    [InlineData(7, 30, 4, 3)] // destroyed 2.1 -> 3 (up), owner 4.9 -> 4 (down)
    [InlineData(10, 0, 10, 0)]
    [InlineData(10, 100, 0, 10)]
    [InlineData(1, 1, 0, 1)]
    public void Owned_the_destroyed_share_is_rounded_up_and_the_owner_gets_the_rest(long total, int destroyedPercent, long owner,
        long destroyed)
    {
        var split = FeeMath.Split(total, owned: true, destroyedSharePercent: destroyedPercent);
        Assert.Equal(owner, split.OwnerShare);
        Assert.Equal(destroyed, split.AdventurerShare);
        Assert.Equal(total, split.OwnerShare + split.AdventurerShare);
    }

    [Fact]
    public void The_fee_type_ids_round_trip_and_unknown_ids_are_refused()
    {
        foreach (var type in FeeTypes.All)
        {
            Assert.True(FeeTypes.TryParse(FeeTypes.Id(type), out var parsed));
            Assert.Equal(type, parsed);
        }
        Assert.Equal(new[] { "listing", "saleTax", "contract" }, FeeTypes.All.Select(FeeTypes.Id));
        Assert.False(FeeTypes.TryParse("tax", out _));
        Assert.Equal(Enum.GetValues<FeeType>().Length, FeeTypes.All.Length);
    }
}
