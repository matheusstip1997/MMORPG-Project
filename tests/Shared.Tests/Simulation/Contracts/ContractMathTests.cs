using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Contracts;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;

namespace Shared.Tests.Simulation.Contracts;

/// <summary>
/// The pure contract rules (spec 5.4 R-5.4-01..04, M4 decision 10, user answers 2026-11-15; WP-M4-10): the crafter's cancel cost
/// (rounded down, never more than the rate), the deadline bounds and the deadline from the acceptance, the proposal's time on the
/// board, the UTC day and the unordered pair of the per-pair cap, the value check, who may cancel when and whether it costs, and
/// the terms check of a definition's slots.
/// </summary>
public class ContractMathTests
{
    [Theory]
    [InlineData(10, 10, 1)]
    [InlineData(19, 10, 1)] // 1.9 rounds down
    [InlineData(100, 10, 10)]
    [InlineData(123, 10, 12)]
    [InlineData(5, 10, 0)] // below a whole gold: nothing
    [InlineData(1_000, 0, 0)]
    [InlineData(1_000, 100, 1_000)]
    [InlineData(9_000_000_000_000_000, 10, 900_000_000_000_000)] // the gold cap does not overflow
    public void The_cancel_cost_is_the_percent_of_the_gold_rounded_down(long gold, int percent, long cost)
    {
        Assert.Equal(cost, ContractMath.CancelCost(gold, percent));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, 101)]
    [InlineData(10, -1)]
    public void A_cancel_cost_out_of_range_throws(long gold, int percent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContractMath.CancelCost(gold, percent));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(72, true)]
    [InlineData(73, false)]
    public void A_deadline_is_one_to_seventy_two_hours(int hours, bool allowed)
    {
        Assert.Equal(allowed, ContractMath.DeadlineAllowed(hours, 1, 72));
    }

    [Fact]
    public void The_deadline_runs_from_the_acceptance_and_a_proposal_stays_its_open_hours()
    {
        Assert.Equal(1_000 + 3 * 3_600_000L, ContractMath.DeadlineAt(1_000, 3));
        Assert.Equal(1_000 + 24 * 3_600_000L, ContractMath.OpenUntil(1_000, 24));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(86_399_999, 0)]
    [InlineData(86_400_000, 1)]
    [InlineData(-1, -1)]
    public void The_utc_day_floors(long utcMs, long day)
    {
        Assert.Equal(day, ContractMath.UtcDay(utcMs));
    }

    [Fact]
    public void A_pair_is_the_same_either_way_round_and_its_cap_counts_acceptances()
    {
        Assert.Equal(ContractMath.PairOf(7, 3), ContractMath.PairOf(3, 7));
        Assert.True(ContractMath.PairAllows(4, 5));
        Assert.False(ContractMath.PairAllows(5, 5));
    }

    [Theory]
    [InlineData(9, ContractReason.ContractValueTooLow)]
    [InlineData(10, ContractReason.None)]
    [InlineData(1_000, ContractReason.None)]
    [InlineData(1_001, ContractReason.ContractTermsInvalid)] // above the gold cap
    public void The_value_is_at_least_the_minimum_and_at_most_the_gold_cap(long gold, ContractReason reason)
    {
        Assert.Equal(reason, ContractMath.CheckGold(gold, 10, 1_000));
    }

    [Theory]
    // The customer: free until work started (an open proposal, or accepted without a forge start).
    [InlineData(ContractStatus.Proposed, false, ContractRole.Customer, ContractReason.None, false)]
    [InlineData(ContractStatus.Accepted, false, ContractRole.Customer, ContractReason.None, false)]
    [InlineData(ContractStatus.Accepted, true, ContractRole.Customer, ContractReason.ContractStateInvalid, false)]
    [InlineData(ContractStatus.InProgress, true, ContractRole.Customer, ContractReason.ContractStateInvalid, false)]
    // The crafter: an accepted contract; after work started it costs.
    [InlineData(ContractStatus.Accepted, false, ContractRole.Crafter, ContractReason.None, false)]
    [InlineData(ContractStatus.Accepted, true, ContractRole.Crafter, ContractReason.None, true)]
    [InlineData(ContractStatus.InProgress, true, ContractRole.Crafter, ContractReason.ContractStateInvalid, false)]
    [InlineData(ContractStatus.Proposed, false, ContractRole.None, ContractReason.ContractNotFound, false)]
    public void Who_may_cancel_and_whether_it_costs(ContractStatus status, bool started, ContractRole role, ContractReason reason, bool costs)
    {
        Assert.Equal(reason, ContractMath.CheckCancel(status, started, role, out bool cost));
        Assert.Equal(costs, cost);
    }

    [Fact]
    public void Every_trade_refusal_is_a_not_tradable_contract_input()
    {
        Assert.Equal(ContractReason.None, ContractMath.ReasonOf(TradeOfferCheck.Ok));
        foreach (var check in new[] { TradeOfferCheck.Stolen, TradeOfferCheck.SellOnly, TradeOfferCheck.Quest, TradeOfferCheck.NotTradable,
                     TradeOfferCheck.Equipped })
            Assert.Equal(ContractReason.ContractNotTradable, ContractMath.ReasonOf(check));
    }

    private static readonly ConfigSet Tables = Shared.Tests.Simulation.Crafting.ForgeMathTests.LoadRepository();
    private static readonly ForgeRules Forge = ForgeRules.From(Tables)!;

    private static ContractSlot Slot(ContractSupplier supplier, string itemId, int count = 1) => new()
    {
        Supplier = supplier, ItemNetId = (ushort)(itemId.Length == 0 ? 0 : Forge.Item(itemId)!.NetId), Count = (ushort)count,
    };

    [Fact]
    public void The_terms_name_one_slot_per_input_each_taking_its_item()
    {
        Assert.True(Forge.TryGet(ForgeKind.Mold, 1, out var sword)); // m3.moldSword: two free slots of one unit
        Assert.Equal(ContractReason.None, ContractRules.CheckTerms(sword,
            new[] { Slot(ContractSupplier.Customer, "m3.oreT1"), Slot(ContractSupplier.Crafter, "") }, Forge.Item));
        Assert.Equal(ContractReason.None, ContractRules.CheckTerms(sword,
            new[] { Slot(ContractSupplier.Customer, "m3.oreT1"), Slot(ContractSupplier.Customer, "m1.demonAsh") }, Forge.Item));
        Assert.Equal(ContractReason.ContractTermsInvalid, ContractRules.CheckTerms(sword,
            new[] { Slot(ContractSupplier.Customer, "m3.oreT1") }, Forge.Item)); // one slot short
        Assert.Equal(ContractReason.ContractTermsInvalid, ContractRules.CheckTerms(sword,
            new[] { Slot(ContractSupplier.Customer, "m3.oreT1", 2), Slot(ContractSupplier.Crafter, "") }, Forge.Item)); // two units
        Assert.Equal(ContractReason.ContractTermsInvalid, ContractRules.CheckTerms(sword,
            new[] { Slot(ContractSupplier.Customer, "m3.roughGemT1"), Slot(ContractSupplier.Crafter, "") }, Forge.Item)); // no forge input
        Assert.Equal(ContractReason.ContractTermsInvalid, ContractRules.CheckTerms(sword,
            new[] { Slot(ContractSupplier.Customer, ""), Slot(ContractSupplier.Crafter, "") }, Forge.Item)); // the customer names none
        Assert.Equal(ContractReason.ContractTermsInvalid, ContractRules.CheckTerms(null, Array.Empty<ContractSlot>(), Forge.Item));
        Assert.Equal(1, ContractRules.CustomerSlots(new[] { Slot(ContractSupplier.Customer, "m3.oreT1"), Slot(ContractSupplier.Crafter, "") }));
    }

    [Fact]
    public void A_crafter_slot_takes_the_named_item_or_any_the_input_takes()
    {
        Assert.True(Forge.TryGet(ForgeKind.Mold, 1, out var sword));
        var any = Slot(ContractSupplier.Crafter, "");
        var ore = Slot(ContractSupplier.Crafter, "m3.oreT1");
        Assert.True(ContractRules.CrafterSlotTakes(any, sword.Inputs[0], Forge.Item("m1.demonAsh")!));
        Assert.True(ContractRules.CrafterSlotTakes(ore, sword.Inputs[0], Forge.Item("m3.oreT1")!));
        Assert.False(ContractRules.CrafterSlotTakes(ore, sword.Inputs[0], Forge.Item("m1.demonAsh")!));
        Assert.False(ContractRules.CrafterSlotTakes(Slot(ContractSupplier.Customer, "m3.oreT1"), sword.Inputs[0], Forge.Item("m3.oreT1")!));
    }
}
