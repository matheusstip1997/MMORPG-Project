using Shared.Data.Tables;
using Shared.Simulation.Economy;

namespace Shared.Tests.Simulation.Economy;

/// <summary>
/// The large-transfer flag rule (spec 4.5 R-4.5-02, M4 decision 17; WP-M4-15) and the item value it uses (the user's answer of
/// 2026-11-15: value bands per category and tier times the rarity percent) as pure functions: one-way is net over the pair's day,
/// either threshold flags (gold only when one-sided, the user answer of 2026-11-15), the giver is the side the net value leaves; a band applies from its tier up; sell-only items use
/// PriceBase; unknown items and rarities are worth 0.
/// </summary>
public class TransferFlagMathTests
{
    private const int OneSided = 10; // the placeholder TransferFlagSettings.oneSidedGoldPercent

    [Fact]
    public void A_one_way_gift_over_the_gold_threshold_crosses_gold_and_value()
    {
        var day = new TransferPairDay(5_000, 0, 0, 0, 1);
        Assert.Equal(TransferFlagMath.GoldCrossed | TransferFlagMath.ValueCrossed, TransferFlagMath.Crossed(day, 5_000, 5_000, OneSided));
        Assert.Equal(TransferFlagMath.GoldCrossed, TransferFlagMath.Crossed(day, 5_000, 10_000, OneSided));
        Assert.True(TransferFlagMath.LowGives(day));
    }

    [Fact]
    public void An_ordinary_purchase_is_not_flagged_on_gold()
    {
        // User answer 2026-11-15 ("Only one-sided gold"): 6,000 gold for an item worth about its price. The higher account paid
        // the gold, the lower gave the item: the net value is 0 and the gold is not one-sided, so nothing is crossed.
        var day = new TransferPairDay(0, 6_000, 6_000, 0, 1);
        Assert.Equal(-6_000, TransferFlagMath.NetGold(day));
        Assert.Equal(0, TransferFlagMath.NetValue(day));
        Assert.False(TransferFlagMath.IsOneSidedGold(day, OneSided));
        Assert.Equal(0, TransferFlagMath.Crossed(day, 5_000, 1, OneSided));
        Assert.False(TransferFlagMath.LowGives(day)); // the value nets to zero: the gold's direction (the higher gave it)
    }

    [Theory]
    [InlineData(0, true)] // 6,000 gold for nothing
    [InlineData(5, true)] // for an item worth a few gold
    [InlineData(599, true)] // just below 10 % of the net gold
    [InlineData(600, false)] // 10 %: not one-sided
    [InlineData(5_500, false)] // about the price
    public void Six_thousand_gold_is_flagged_on_gold_only_when_little_value_came_back(long valueBack, bool flagged)
    {
        var day = new TransferPairDay(0, 6_000, valueBack, 0, 1);
        Assert.Equal(flagged, TransferFlagMath.IsOneSidedGold(day, OneSided));
        Assert.Equal(flagged, (TransferFlagMath.Crossed(day, 5_000, 1_000_000, OneSided) & TransferFlagMath.GoldCrossed) != 0);
    }

    [Fact]
    public void An_overpayment_is_still_caught_by_the_value_threshold()
    {
        // 30,000 gold for an item worth 6,000: not one-sided gold, but 24,000 of value went one way.
        var day = new TransferPairDay(30_000, 0, 0, 6_000, 1);
        Assert.Equal(TransferFlagMath.ValueCrossed, TransferFlagMath.Crossed(day, 5_000, 10_000, OneSided));
        Assert.Equal(24_000, TransferFlagMath.NetValue(day));
    }

    [Fact]
    public void Items_given_for_nothing_cross_the_value_threshold_only()
    {
        var day = new TransferPairDay(0, 0, 0, 12_000, 1);
        Assert.Equal(TransferFlagMath.ValueCrossed, TransferFlagMath.Crossed(day, 5_000, 10_000, OneSided));
        Assert.False(TransferFlagMath.LowGives(day));
        Assert.Equal(-12_000, TransferFlagMath.NetValue(day));
        Assert.False(TransferFlagMath.IsOneSidedGold(day, OneSided)); // no net gold at all
    }

    [Fact]
    public void Adding_transfers_sums_both_ways_and_counts_them()
    {
        var day = new TransferPairDay(0, 0, 0, 0, 0).Add(100, 0, 10, 0).Add(0, 40, 0, 5);
        Assert.Equal((100L, 40L, 10L, 5L, 2), (day.GoldLowToHigh, day.GoldHighToLow, day.ItemValueLowToHigh, day.ItemValueHighToLow, day.Transfers));
        Assert.Equal(60, TransferFlagMath.NetGold(day));
        Assert.Equal(65, TransferFlagMath.NetValue(day));
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(86_399_999L, 0L)]
    [InlineData(86_400_000L, 1L)]
    [InlineData(-1L, -1L)]
    [InlineData(1_800_000_000_000L, 20_833L)]
    public void The_day_is_the_UTC_day_number(long utcMs, long day)
    {
        Assert.Equal(day, TransferFlagMath.UtcDay(utcMs));
    }

    private static ItemValue Values() => new(
        [
            new ItemDef { Id = "ore", NetId = 1, Category = ItemCategory.Material, Tier = 1 },
            new ItemDef { Id = "oreT3", NetId = 2, Category = ItemCategory.Material, Tier = 3 },
            new ItemDef { Id = "sword", NetId = 3, Category = ItemCategory.Equipment, Tier = 2 },
            new ItemDef { Id = "horn", NetId = 4, Category = ItemCategory.SellOnly },
            new ItemDef { Id = "potion", NetId = 5, Category = ItemCategory.Consumable },
        ],
        [
            new ItemValueBand { Id = "material.t0", Category = ItemCategory.Material, Tier = 0, GoldPerUnit = 5 },
            new ItemValueBand { Id = "material.t2", Category = ItemCategory.Material, Tier = 2, GoldPerUnit = 15 },
            new ItemValueBand { Id = "equipment.t0", Category = ItemCategory.Equipment, Tier = 0, GoldPerUnit = 60 },
        ],
        [new PriceBase { ItemId = "horn", BaseGold = 10 }],
        [new RarityDef { Id = "common", Tier = 0 }, new RarityDef { Id = "rare", Tier = 2 }],
        [new RarityMult { RarityId = "common", PricePercent = 100 }, new RarityMult { RarityId = "rare", PricePercent = 250 }]);

    [Fact]
    public void An_item_takes_the_band_of_the_highest_tier_not_above_its_own_times_its_rarity_percent()
    {
        var values = Values();
        Assert.Equal(5, values.UnitValue("ore", 0)); // tier 1: the tier-0 band
        Assert.Equal(15, values.UnitValue("oreT3", 0)); // tier 3: the tier-2 band
        Assert.Equal(150, values.UnitValue("sword", 2)); // 60 x 250 %
        Assert.Equal(150, values.UnitValue(3, 2)); // by net id the same
        Assert.Equal(1_500, values.StackValue(3, 2, 10));
    }

    [Fact]
    public void A_sell_only_item_uses_its_price_base_and_the_rest_are_worth_nothing()
    {
        var values = Values();
        Assert.Equal(25, values.UnitValue("horn", 2)); // PriceBase 10 x 250 %
        Assert.Equal(0, values.UnitValue("potion", 0)); // no consumable band
        Assert.Equal(0, values.UnitValue("unknown", 0));
        Assert.Equal(0, values.UnitValue("ore", 1)); // no percent for rarity tier 1
        Assert.Equal(0, values.StackValue("ore", 0, 0));
        Assert.Equal(0, values.StackValue("ore", 0, -3));
    }
}
