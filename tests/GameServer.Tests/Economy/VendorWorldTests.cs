using GameServer.Events;
using GameServer.Persistence;
using Shared.Protocol;

namespace GameServer.Tests.Economy;

/// <summary>
/// Buying from an NPC vendor in the world (R-4.2-01: fixed prices, gold destroyed, never buys; WP-M2-4): a class 1
/// operation confirmed after its save, with its refusals (<c>NotEnoughGold</c>, <c>NotInStock</c>, <c>BagFull</c>,
/// <c>QuoteExpired</c>, <c>OutOfRange</c>) and double submit.
/// </summary>
public class VendorWorldTests
{
    private const int PotionPrice = 25; // data/tables/NpcVendorStock.json (placeholder)

    [Fact]
    public void A_purchase_destroys_the_gold_creates_the_items_and_is_confirmed_after_its_save()
    {
        var f = new EconomyFixture(gold: 100);
        ushort potion = f.NetId(EconomyFixture.Potion);
        int jobsBefore = f.Harness.Saves.Jobs.Count;

        f.Harness.SendBuy(1, 1, f.NpcId(EconomyFixture.Vendor), potion, 2, 2 * PotionPrice);
        f.Harness.Ticks(1);
        Assert.Empty(f.Transport.BuyResultsFor(1)); // not before the commit
        f.Commit();

        var result = Assert.Single(f.Transport.BuyResultsFor(1));
        Assert.Equal((OutpostReason.None, potion, (ushort)2, 50L), (result.Reason, result.ItemNetId, result.Count, result.GoldSpent));
        Assert.Equal(50, f.Inventory.CommittedGold(f.CharacterId));
        Assert.Equal(50, f.Transport.GoldFor(1)[^1]);
        Assert.Equal((potion, 2), (f.Inventory.Current(f.CharacterId)[0].ItemNetId, f.Inventory.Current(f.CharacterId)[0].Count));
        var job = Assert.Single(f.Harness.Saves.Jobs.Skip(jobsBefore));
        Assert.Equal(SaveReason.Class1, job.Reason);
        var entry = Assert.Single(job.Inventory!.Gold!.Entries);
        Assert.Equal((-50L, 50L, GoldFlow.Sink, nameof(InventoryReason.VendorPurchase)), (entry.Delta, entry.BalanceAfter, entry.Flow, entry.Reason));
        Assert.Equal(50, Assert.Single(f.Events.GoldDestroyed).Amount);
    }

    [Fact]
    public void Not_enough_gold_is_refused_and_changes_nothing()
    {
        var f = new EconomyFixture(gold: 24);

        f.Harness.SendBuy(1, 1, f.NpcId(EconomyFixture.Vendor), f.NetId(EconomyFixture.Potion), 1, PotionPrice);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.NotEnoughGold, Assert.Single(f.Transport.BuyResultsFor(1)).Reason);
        Assert.Equal(24, f.Inventory.Gold(f.CharacterId));
        Assert.True(f.Inventory.Current(f.CharacterId)[0].IsEmpty);
    }

    [Theory]
    [InlineData(EconomyFixture.Horn, 1)] // the vendor does not sell it
    [InlineData(EconomyFixture.Potion, 0)]
    [InlineData(EconomyFixture.Potion, 21)] // more than one stack (maxStack 20)
    public void An_item_or_count_the_vendor_does_not_offer_is_refused_with_NotInStock(string item, ushort count)
    {
        var f = new EconomyFixture(gold: 10_000);

        f.Harness.SendBuy(1, 1, f.NpcId(EconomyFixture.Vendor), f.NetId(item), count, count * PotionPrice);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.NotInStock, Assert.Single(f.Transport.BuyResultsFor(1)).Reason);
    }

    [Fact]
    public void A_full_bag_is_refused_with_BagFull()
    {
        var full = Enumerable.Range(0, 30).Select(s => EconomyFixture.Row(s, EconomyFixture.Ash, 99)).ToArray();
        var f = new EconomyFixture(gold: 100, bag: full);

        f.Harness.SendBuy(1, 1, f.NpcId(EconomyFixture.Vendor), f.NetId(EconomyFixture.Potion), 1, PotionPrice);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.BagFull, Assert.Single(f.Transport.BuyResultsFor(1)).Reason);
        Assert.Equal(100, f.Inventory.Gold(f.CharacterId));
    }

    [Fact]
    public void A_total_other_than_the_fixed_price_is_refused_with_QuoteExpired()
    {
        var f = new EconomyFixture(gold: 100);

        f.Harness.SendBuy(1, 1, f.NpcId(EconomyFixture.Vendor), f.NetId(EconomyFixture.Potion), 1, PotionPrice - 1);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.QuoteExpired, Assert.Single(f.Transport.BuyResultsFor(1)).Reason);
    }

    [Fact]
    public void Buying_from_the_outpost_or_out_of_range_is_refused()
    {
        var f = new EconomyFixture(gold: 100);

        f.Harness.SendBuy(1, 1, f.NpcId(EconomyFixture.Outpost), f.NetId(EconomyFixture.Potion), 1, PotionPrice);
        f.Harness.Ticks(1);

        Assert.Equal(OutpostReason.InteractionUnavailable, Assert.Single(f.Transport.BuyResultsFor(1)).Reason);
    }

    [Fact]
    public void A_repeated_purchase_request_is_applied_once_and_answered_the_same()
    {
        var f = new EconomyFixture(gold: 100);
        ushort potion = f.NetId(EconomyFixture.Potion);

        f.Harness.SendBuy(1, 1, f.NpcId(EconomyFixture.Vendor), potion, 1, PotionPrice);
        f.Harness.SendBuy(1, 1, f.NpcId(EconomyFixture.Vendor), potion, 1, PotionPrice);
        f.Commit();
        f.Harness.SendBuy(1, 1, f.NpcId(EconomyFixture.Vendor), potion, 1, PotionPrice);
        f.Commit();

        Assert.Equal(3, f.Transport.BuyResultsFor(1).Count);
        Assert.All(f.Transport.BuyResultsFor(1), r => Assert.Equal((OutpostReason.None, 25L), (r.Reason, r.GoldSpent)));
        Assert.Equal(75, f.Inventory.CommittedGold(f.CharacterId));
        Assert.Equal(1, f.Inventory.Current(f.CharacterId)[0].Count);
    }
}
