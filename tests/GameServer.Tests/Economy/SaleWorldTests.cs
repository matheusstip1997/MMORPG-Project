using GameServer.Events;
using GameServer.Persistence;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Tests.Economy;

/// <summary>
/// Selling at an outpost in the world, tick by tick (R-4.2-01/02, R-7.1-01/02; WP-M2-4): the quote, per-unit pricing that
/// falls with each sale, the class 1 path (applied at once, confirmed only after its save committed, carrying pending
/// class 2 changes in the same save), refusals with their reasons, double submit, two sellers in one tick, the gold cap.
/// </summary>
public class SaleWorldTests
{
    private static EconomyFixture WithHorns(int count = 60, long gold = 0, Action<ConfigSet>? edit = null, int players = 1) =>
        new(gold, [EconomyFixture.Row(0, EconomyFixture.Horn, count), EconomyFixture.Row(1, EconomyFixture.Ash, 5)], edit, players);

    private static SellQuoteResult Quote(EconomyFixture f, ushort count, uint requestId = 100, byte slot = 0, int peer = 1)
    {
        f.Harness.SendQuote(peer, requestId, f.NpcId(EconomyFixture.Outpost), slot, count);
        f.Harness.Ticks(1);
        return f.Transport.QuotesFor(peer).Single(q => q.RequestId == requestId);
    }

    [Fact]
    public void A_quote_prices_units_one_after_the_other_and_the_next_quote_is_lower_after_a_sale()
    {
        var f = WithHorns();

        var first = Quote(f, 30);
        Assert.Equal(OutpostReason.None, first.Reason);
        Assert.Equal(10, first.FirstUnit); // base 10 x common 100% x 100% (nothing sold yet)
        Assert.True(first.LastUnit < first.FirstUnit, "the price must fall over a 30-unit sale for one player online");

        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.Outpost), 0, 30, first.Total);
        f.Commit();

        var sale = Assert.Single(f.Transport.SellResultsFor(1));
        Assert.Equal((OutpostReason.None, (ushort)30, first.Total), (sale.Reason, sale.Count, sale.Gold));
        Assert.Equal(first.Total, f.Inventory.CommittedGold(f.CharacterId));
        Assert.Equal(first.Total, f.Transport.GoldFor(1)[^1]);
        Assert.Equal(30, f.Inventory.Current(f.CharacterId)[0].Count);

        var after = Quote(f, 30, requestId: 101);
        Assert.True(first.LastUnit >= after.FirstUnit);
        Assert.True(after.Total < first.Total);
    }

    [Fact]
    public void A_sale_is_applied_at_once_and_confirmed_only_after_its_class_1_save_committed()
    {
        var f = WithHorns();
        var quote = Quote(f, 10);
        f.Harness.Saves.AutoComplete = false;
        int jobsBefore = f.Harness.Saves.Jobs.Count;

        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.Outpost), 0, 10, quote.Total);
        f.Harness.Ticks(2);

        Assert.Equal(50, f.Inventory.Current(f.CharacterId)[0].Count); // the world sees it: the units are locked
        Assert.Equal(quote.Total, f.Inventory.Gold(f.CharacterId));
        Assert.Equal(0, f.Inventory.CommittedGold(f.CharacterId));
        Assert.Empty(f.Transport.SellResultsFor(1));
        var job = Assert.Single(f.Harness.Saves.Jobs.Skip(jobsBefore));
        Assert.Equal(SaveReason.Class1, job.Reason); // issued at once, not on the class 2 cadence
        Assert.Equal(quote.Total, job.Inventory!.Gold!.GoldAfter);
        var entry = Assert.Single(job.Inventory.Gold.Entries);
        Assert.Equal((quote.Total, GoldFlow.Faucet, nameof(InventoryReason.OutpostSale), "npc:m1.outpost"),
            (entry.Delta, entry.Flow, entry.Reason, entry.Source));
        Assert.Empty(f.Events.Sales);

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(1);

        Assert.Single(f.Transport.SellResultsFor(1));
        var ids = f.Transport.ReliableIdsFor(1);
        int sold = ids.LastIndexOf(ReliableMessageId.SellResult);
        Assert.Equal([ReliableMessageId.SellResult, ReliableMessageId.InventoryContents, ReliableMessageId.GoldBalance], ids.Skip(sold).Take(3));
        var sale = Assert.Single(f.Events.Sales);
        Assert.Equal((f.CharacterId, f.NetId(EconomyFixture.Horn), 10, quote.Total, 1), (sale.CharacterId, sale.ItemNetId, sale.Count, sale.Gold, sale.NpcDefId));
        Assert.Equal(quote.Total, Assert.Single(f.Events.GoldChanges).Delta);
        Assert.Empty(f.Events.GoldDestroyed);
    }

    [Theory]
    [InlineData(1, 1, OutpostReason.NotSellable)] // demon ash is a material (R-4.1-01)
    [InlineData(2, 1, OutpostReason.NothingToSell)] // an empty slot
    [InlineData(0, 61, OutpostReason.NothingToSell)] // more than the stack holds
    [InlineData(0, 0, OutpostReason.NothingToSell)]
    [InlineData(200, 1, OutpostReason.NothingToSell)] // outside the bag
    public void A_refused_sale_is_answered_at_once_and_changes_nothing(byte slot, ushort count, OutpostReason reason)
    {
        var f = WithHorns();

        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.Outpost), slot, count, 0);
        f.Harness.Ticks(1);

        Assert.Equal(reason, Assert.Single(f.Transport.SellResultsFor(1)).Reason);
        Assert.Equal(60, f.Inventory.Current(f.CharacterId)[0].Count);
        Assert.Equal(0, f.Inventory.Gold(f.CharacterId));
    }

    [Fact]
    public void A_stolen_stack_cannot_be_sold_at_an_outpost()
    {
        var f = new EconomyFixture(bag: [EconomyFixture.Row(0, EconomyFixture.Horn, 5, stolen: true)]);

        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.Outpost), 0, 5, 0);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.NotSellable, Assert.Single(f.Transport.SellResultsFor(1)).Reason); // R-4.4-02
    }

    [Fact]
    public void A_sale_out_of_reach_is_refused_with_OutOfRange()
    {
        var f = WithHorns();

        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.FarOutpost), 0, 5, 0);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.OutOfRange, Assert.Single(f.Transport.SellResultsFor(1)).Reason);
    }

    [Fact]
    public void A_total_below_the_confirmed_quote_is_refused_and_a_higher_one_is_paid()
    {
        var f = WithHorns();
        var quote = Quote(f, 5);

        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.Outpost), 0, 5, quote.Total + 1);
        f.Harness.SendSell(1, 2, f.NpcId(EconomyFixture.Outpost), 0, 5, quote.Total - 10);
        f.Harness.Ticks(1);
        f.Commit();

        var results = f.Transport.SellResultsFor(1);
        Assert.Equal(OutpostReason.QuoteExpired, results.Single(r => r.RequestId == 1).Reason);
        var paid = results.Single(r => r.RequestId == 2);
        Assert.Equal((OutpostReason.None, quote.Total), (paid.Reason, paid.Gold)); // never less than confirmed
    }

    [Fact]
    public void A_sale_carries_the_pending_class_2_changes_in_the_same_save()
    {
        var f = WithHorns();
        var quote = Quote(f, 5);
        int jobsBefore = f.Harness.Saves.Jobs.Count;

        // A bag move (class 2, normally saved on the 1 s cadence) and then a sale, in one tick.
        f.Harness.SendMove(1, 1, fromSlot: 1, toSlot: 9);
        f.Harness.SendSell(1, 2, f.NpcId(EconomyFixture.Outpost), 0, 5, quote.Total);
        f.Commit();

        var job = Assert.Single(f.Harness.Saves.Jobs.Skip(jobsBefore));
        Assert.Equal(SaveReason.Class1, job.Reason);
        var keys = job.Inventory!.Ops.Select(o => o.OpKey).ToList();
        string sell = $"sell:test-run:{f.Player.Id}:{f.Player.Connection}:2";
        Assert.Equal([$"move:test-run:{f.Player.Id}:{f.Player.Connection}:1", sell + ":items", sell + ":gold"], keys);
        Assert.Single(f.Transport.SellResultsFor(1));
        Assert.Single(f.Transport.MoveResultsFor(1));
    }

    [Fact]
    public void A_repeated_sale_request_is_applied_once_and_answered_the_same()
    {
        var f = WithHorns();
        var quote = Quote(f, 5);

        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.Outpost), 0, 5, quote.Total);
        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.Outpost), 0, 5, quote.Total);
        f.Commit();
        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.Outpost), 0, 5, quote.Total);
        f.Commit();

        var results = f.Transport.SellResultsFor(1);
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal((OutpostReason.None, (ushort)5, quote.Total), (r.Reason, r.Count, r.Gold)));
        Assert.Equal(55, f.Inventory.Current(f.CharacterId)[0].Count);
        Assert.Equal(quote.Total, f.Inventory.CommittedGold(f.CharacterId));
        Assert.Single(f.Events.Sales);
    }

    [Fact]
    public void Two_sellers_in_one_tick_price_one_after_the_other()
    {
        var f = WithHorns(players: 2);
        var first = Quote(f, 30, peer: 1);
        var second = Quote(f, 30, requestId: 200, peer: 2);
        Assert.Equal(first.Total, second.Total);

        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.Outpost), 0, 30, first.Total);
        f.Harness.SendSell(2, 1, f.NpcId(EconomyFixture.Outpost), 0, 30, second.Total);
        f.Commit();

        // The lower entity id sells first; the other's units would now price lower than the quote it confirmed.
        Assert.Equal(OutpostReason.None, Assert.Single(f.Transport.SellResultsFor(1)).Reason);
        Assert.Equal(OutpostReason.QuoteExpired, Assert.Single(f.Transport.SellResultsFor(2)).Reason);
    }

    [Fact]
    public void A_sale_past_the_gold_cap_is_refused()
    {
        var f = WithHorns(gold: 95, edit: set => set.Single<EconomySettings>().MaxGold = 100);
        var quote = Quote(f, 1);

        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.Outpost), 0, 1, quote.Total);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.GoldCapReached, Assert.Single(f.Transport.SellResultsFor(1)).Reason);
        Assert.Equal(95, f.Inventory.Gold(f.CharacterId));
    }

    [Fact]
    public void The_committed_gold_is_sent_when_the_player_enters_and_reloads_at_the_next_login()
    {
        var f = WithHorns(gold: 42);
        Assert.Equal(42, f.Transport.GoldFor(1)[^1]);
        var quote = Quote(f, 3);

        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.Outpost), 0, 3, quote.Total);
        f.Commit();

        Assert.Equal(42 + quote.Total, f.Harness.Characters.Get(f.CharacterId).Gold); // what the fake worker stored
    }
}
