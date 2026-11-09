using Shared.Data.Tables;
using Shared.Simulation.Items;

namespace Shared.Tests.Simulation.Items;

/// <summary>
/// The pure rules of a trade (WP-M4-4; spec 4.5 "M4 trade window", R-4.1-04, R-4.5-01, M4 decision 5): which units may be
/// offered and why not, the receiving space with stack merges, and the owner history a traded instance carries (spec 4.1).
/// </summary>
public class TradeRulesTests
{
    private const int MaxStack = 99;

    [Theory]
    [InlineData(ItemCategory.SellOnly, false, false, false, false, TradeOfferCheck.SellOnly)]
    [InlineData(ItemCategory.Quest, false, false, false, false, TradeOfferCheck.Quest)]
    [InlineData(ItemCategory.Material, true, false, false, false, TradeOfferCheck.Stolen)]
    [InlineData(ItemCategory.Equipment, false, true, false, false, TradeOfferCheck.Stolen)]
    [InlineData(ItemCategory.Equipment, false, false, true, false, TradeOfferCheck.NotTradable)]
    [InlineData(ItemCategory.Equipment, false, false, false, true, TradeOfferCheck.Equipped)]
    [InlineData(ItemCategory.Material, false, false, false, false, TradeOfferCheck.Ok)]
    [InlineData(ItemCategory.MoldOrTool, false, false, false, false, TradeOfferCheck.Ok)]
    [InlineData(ItemCategory.Consumable, false, false, false, false, TradeOfferCheck.Ok)]
    [InlineData(ItemCategory.Equipment, false, false, false, false, TradeOfferCheck.Ok)]
    public void Each_unit_that_cannot_trade_has_its_own_reason(ItemCategory category, bool stolen, bool tainted, bool nonTradable,
        bool equipped, TradeOfferCheck expected) =>
        Assert.Equal(expected, TradeRules.CheckOffer(category, stolen, tainted, nonTradable, equipped));

    [Fact]
    public void The_category_reason_comes_before_the_flags()
    {
        // A stolen sell-only drop names the category (the first rule a player can act on).
        Assert.Equal(TradeOfferCheck.SellOnly, TradeRules.CheckOffer(ItemCategory.SellOnly, true, false, true, false));
    }

    [Fact]
    public void Incoming_stacks_merge_before_they_take_empty_slots()
    {
        var bag = new Bag(2);
        bag.Load(0, new ItemStack(2, 0, false, 95));
        bag.Load(1, new ItemStack(1, 0, false, 99));

        Assert.True(TradeRules.FitsAll(TradeRules.Copy(bag), [new ItemStack(2, 0, false, 4)], [MaxStack]));
        Assert.False(TradeRules.FitsAll(TradeRules.Copy(bag), [new ItemStack(2, 0, false, 5)], [MaxStack]));
        Assert.False(TradeRules.FitsAll(TradeRules.Copy(bag), [new ItemStack(2, 0, true, 1)], [MaxStack])); // stolen never merges
        Assert.Equal(95, bag[0].Count); // the scratch is a copy
    }

    [Fact]
    public void Instances_need_an_empty_slot_each()
    {
        var bag = new Bag(2);
        bag.Load(0, new ItemStack(101, 0, false, 1, 77));

        Assert.True(TradeRules.FitsAll(TradeRules.Copy(bag), [new ItemStack(101, 0, false, 1, 78)], [1]));
        Assert.False(TradeRules.FitsAll(TradeRules.Copy(bag), [new ItemStack(101, 0, false, 1, 78), new ItemStack(101, 0, false, 1, 79)],
            [1, 1]));
    }

    [Fact]
    public void Freed_slots_count_once_the_receivers_own_units_left()
    {
        var bag = new Bag(1);
        bag.Load(0, new ItemStack(1, 0, false, 99));
        var scratch = TradeRules.Copy(bag);
        Assert.True(scratch.TryRemove(0, 99, null)); // the receiver gives its stack away in the same swap

        Assert.True(TradeRules.FitsAll(scratch, [new ItemStack(2, 0, false, 10)], [MaxStack]));
    }

    [Fact]
    public void The_owner_history_keeps_the_newest_owners_without_repeating_the_last()
    {
        var history = new List<long>();
        TradeRules.AppendOwner(history, 1, 3);
        TradeRules.AppendOwner(history, 1, 3); // the same giver twice in a row is one entry
        TradeRules.AppendOwner(history, 2, 3);
        TradeRules.AppendOwner(history, 3, 3);
        TradeRules.AppendOwner(history, 4, 3);
        TradeRules.AppendOwner(history, 0, 3); // no owner (escrow) adds nothing

        Assert.Equal([2L, 3L, 4L], history);
    }
}
