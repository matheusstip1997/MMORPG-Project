using System.Collections.Concurrent;
using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Market;
using GameServer.Persistence;
using GameServer.Tests.Economy;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Economy;

namespace GameServer.Tests.Market;

/// <summary>
/// Listings and the auction house in a running world (spec 4.5, M4 decision 6, user answers 2026-11-12 and 2026-11-13; WP-M4-7)
/// with the repository's tables: a listing goes into escrow and pays its fee in the seller's save, answered after the commit; a
/// buyer takes any count, paying the price (the sale tax destroyed, the rest to the seller by mail) in one save; the refusals
/// each have their reason; two buyers in one tick never both settle; a cancel and an expiry go back by mail with the listing
/// closed in the same release; a purchase that does not fit arrives by mail; a search goes to the worker and comes back.
/// </summary>
[Trait("Category", "NoDuplication")]
public class ListingWorldTests
{
    private const string Auctioneer = "test.npc.auctioneer";
    private const string FarAuctioneer = "test.npc.auctioneer.far";
    private const long SellerGold = 100;
    private const long BuyerGold = 1_000;

    [Fact]
    public void A_listing_holds_the_units_in_escrow_pays_its_fee_and_is_answered_after_the_commit()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;
        f.Harness.SendListingCreate(1, 1, f.Npc, slot: 0, count: 4, unitPrice: 100);
        f.Harness.Ticks(2); // the save is issued, not completed

        Assert.Empty(f.Transport.ListingResultsFor(1));
        var save = f.Harness.Saves.Jobs.Single(j => j.Listing is { Creates.Count: > 0 });
        var row = Assert.Single(save.Listing!.Creates);
        Assert.Equal((4, 100L, 8L, ListingKind.Auction), (row.CountInitial, row.UnitPrice, row.FeePaid, row.Kind)); // 2 % of 400
        Assert.Equal(row.EscrowId, Assert.Single(save.Escrow!.Holds).EscrowId); // one transaction (rule 5)
        Assert.Contains(save.Inventory!.Ops, o => o.OpKey == FeeService.OpKeyOf(row.CreateOpKey));

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var answer = Assert.Single(f.Transport.ListingResultsFor(1));
        Assert.Equal((ListingAction.Create, ListingReason.None, row.ListingId, 8L), (answer.Action, answer.Reason, answer.ListingId, answer.Fee));
        Assert.True(f.Listings.IsActive(row.ListingId));
        Assert.Equal(SellerGold - 8, f.Inventory.CommittedGold(f.Seller));
        Assert.Equal(6, f.Units(f.Seller, EconomyFixture.Ash));
        var created = Assert.IsType<ListingCreated>(Assert.Single(f.Events, e => e is ListingCreated));
        Assert.Equal((row.ListingId, f.Seller, 4, 8L), (created.ListingId, created.SellerId, created.Count, created.Fee));
        Assert.Equal(8, f.Ledger.GoldDestroyedBy(InventoryReason.Fee));
    }

    [Fact]
    public void A_buyer_takes_part_then_the_rest_paying_the_price_with_the_tax_destroyed_and_the_proceeds_mailed()
    {
        var f = new Fixture();
        long id = f.List(count: 4, unitPrice: 100);

        f.Harness.SendListingBuy(2, 1, f.Npc, id, 3, 100);
        f.Harness.Ticks(4);
        var first = f.Transport.ListingResultsFor(2).Single();
        Assert.Equal((ListingReason.None, (ushort)3, 300L, 15L, false), (first.Reason, first.Count, first.Price, first.Tax, first.ToMail));
        Assert.Equal(BuyerGold - 300, f.Inventory.CommittedGold(f.Buyer)); // the buyer pays the price
        Assert.Equal(3, f.Units(f.Buyer, EconomyFixture.Ash));
        var proceeds = f.Harness.Saves.Jobs.SelectMany(j => j.Mail?.Sends ?? []).Single(s => s.Draft.Kind == MailKind.ListingProceeds);
        Assert.Equal((f.Seller, 285L), (proceeds.Draft.ToCharacterId, proceeds.Draft.Gold)); // the seller nets price - tax
        Assert.Equal(1, f.Listings.Find(id)!.CountLeft);

        f.Harness.SendListingBuy(2, 2, f.Npc, id, 1, 100);
        f.Harness.Ticks(4);
        Assert.Equal(ListingReason.None, f.Transport.ListingResultsFor(2)[^1].Reason);
        Assert.Null(f.Listings.Find(id)); // sold out
        Assert.Equal(4, f.Units(f.Buyer, EconomyFixture.Ash));
        var sales = f.Events.OfType<ListingSold>().ToList();
        Assert.Equal(new[] { (3, 300L, 15L, 1), (1, 100L, 5L, 0) }, sales.Select(s => (s.Count, s.Price, s.Tax, s.CountLeft)));
        Assert.Equal(8 + 15 + 5, f.Ledger.GoldDestroyedBy(InventoryReason.Fee)); // the listing fee and both taxes
        Assert.Equal(0, f.Ledger.GoldCreated); // no faucet (R-4.5-02)

        // A late buy of the sold-out listing hears why.
        f.Harness.SendListingBuy(2, 3, f.Npc, id, 1, 100);
        f.Harness.Ticks(1);
        Assert.Equal(ListingReason.ListingSold, f.Transport.ListingResultsFor(2)[^1].Reason);
    }

    [Fact]
    public void Each_refusal_has_its_reason_and_changes_nothing()
    {
        var f = new Fixture();
        long id = f.List(count: 4, unitPrice: 100);
        long sellerGold = f.Inventory.Gold(f.Seller);

        f.Harness.SendListingBuy(1, 10, f.Npc, id, 1, 100); // own
        f.Harness.SendListingBuy(2, 11, f.Npc, id, 1, 99); // another price
        f.Harness.SendListingBuy(2, 12, f.Npc, id, 5, 100); // more than is left
        f.Harness.SendListingBuy(2, 13, f.Npc, 999, 1, 100); // no such listing
        f.Harness.SendListingBuy(2, 14, f.FarNpc, id, 1, 100); // the far auctioneer
        f.Harness.SendListingCreate(1, 15, f.Npc, slot: 1, count: 1, unitPrice: 10); // a sell-only horn
        f.Harness.SendListingCreate(1, 16, f.Npc, slot: 0, count: 1, unitPrice: 0); // no price
        f.Harness.SendListingCreate(1, 17, f.Npc, slot: 0, count: 1, unitPrice: 10, durationIndex: 9); // no such duration
        f.Harness.SendListingCreate(1, 18, f.Npc, slot: 0, count: 99, unitPrice: 10); // more than the slot holds
        f.Harness.Ticks(1);

        Assert.Equal(new[]
        {
            ListingReason.ListingChanged, ListingReason.ListingCountInvalid, ListingReason.ListingNotFound, ListingReason.OutOfRange,
        }, f.Transport.ListingResultsFor(2).Select(r => r.Reason));
        Assert.Equal(new[]
        {
            ListingReason.ListingOwn, ListingReason.ListingSellOnly, ListingReason.ListingPriceInvalid,
            ListingReason.ListingDurationInvalid, ListingReason.ListingCountInvalid,
        }, f.Transport.ListingResultsFor(1).Skip(1).Select(r => r.Reason));
        Assert.Equal(4, f.Listings.Find(id)!.CountLeft);
        Assert.Equal(sellerGold, f.Inventory.Gold(f.Seller));
        Assert.Equal(BuyerGold, f.Inventory.Gold(f.Buyer));
    }

    [Fact]
    public void A_seller_at_the_cap_or_short_of_the_fee_is_refused()
    {
        var f = new Fixture(cap: 1, sellerGold: 5);
        f.Harness.SendListingCreate(1, 1, f.Npc, slot: 0, count: 4, unitPrice: 100); // fee 8 > 5 gold
        f.Harness.Ticks(4);
        Assert.Equal(ListingReason.NotEnoughGold, f.Transport.ListingResultsFor(1).Single().Reason);

        f.Harness.SendListingCreate(1, 2, f.Npc, slot: 0, count: 1, unitPrice: 10); // fee 0
        f.Harness.Ticks(4);
        Assert.Equal(ListingReason.None, f.Transport.ListingResultsFor(1)[^1].Reason);
        f.Harness.SendListingCreate(1, 3, f.Npc, slot: 0, count: 1, unitPrice: 10);
        f.Harness.Ticks(4);
        Assert.Equal(ListingReason.ListingCap, f.Transport.ListingResultsFor(1)[^1].Reason);
    }

    [Fact]
    public void Two_buyers_in_one_tick_never_both_settle_and_the_units_are_not_duplicated()
    {
        var f = new Fixture(players: 3);
        long id = f.List(count: 4, unitPrice: 10);
        f.Harness.SendListingBuy(2, 1, f.Npc, id, 4, 10);
        f.Harness.SendListingBuy(3, 1, f.Npc, id, 4, 10);
        f.Harness.Ticks(4);

        var answers = f.Transport.ListingResultsFor(2).Concat(f.Transport.ListingResultsFor(3)).Select(r => r.Reason).ToList();
        Assert.Single(answers, r => r == ListingReason.None);
        Assert.Single(answers, r => r is ListingReason.ListingBusy or ListingReason.ListingSold);
        Assert.Equal(4, f.Units(f.Buyer, EconomyFixture.Ash) + f.Units(f.Characters[2].CharacterId, EconomyFixture.Ash));
        Assert.Single(f.Events.OfType<ListingSold>());
    }

    [Fact]
    public void A_repeated_create_request_lists_once()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;
        f.Harness.SendListingCreate(1, 7, f.Npc, slot: 0, count: 2, unitPrice: 10);
        f.Harness.SendListingCreate(1, 7, f.Npc, slot: 0, count: 2, unitPrice: 10); // the same request id: the same operation
        f.Harness.Ticks(2);
        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        Assert.Single(f.Transport.ListingResultsFor(1), r => r.Reason == ListingReason.None);
        Assert.Equal(1, f.Listings.CountFor(f.Seller, ListingKind.Auction));
        Assert.Equal(8, f.Units(f.Seller, EconomyFixture.Ash));
    }

    [Fact]
    public void A_cancel_returns_the_units_by_mail_and_closes_the_listing_in_the_same_release()
    {
        var f = new Fixture();
        long id = f.List(count: 4, unitPrice: 100);
        f.Harness.SendListingCancel(1, 9, f.Npc, id);
        f.Harness.Ticks(1);
        Assert.DoesNotContain(f.Transport.ListingResultsFor(1), r => r.Action == ListingAction.Cancel); // waits for the release

        f.TickUntil(() => f.Transport.ListingResultsFor(1).Any(r => r.Action == ListingAction.Cancel));
        var call = Assert.Single(f.Releaser.Calls);
        Assert.Equal((f.Seller, (short)MailKind.ListingReturn, (short)ListingState.Cancelled), (call.To, call.Kind, call.Close!.State));
        Assert.Equal(ListingReason.None, f.Transport.ListingResultsFor(1).Single(r => r.Action == ListingAction.Cancel).Reason);
        Assert.Null(f.Listings.Find(id));
        Assert.Equal((4, id), f.Events.OfType<ListingCancelled>().Select(c => (c.CountReturned, c.ListingId)).Single());

        f.Harness.SendListingBuy(2, 1, f.Npc, id, 1, 100);
        f.Harness.Ticks(1);
        Assert.Equal(ListingReason.ListingNotFound, f.Transport.ListingResultsFor(2).Single().Reason);
    }

    [Fact]
    public void An_unsold_listing_expires_back_to_its_seller_by_mail_while_the_seller_is_offline()
    {
        var f = new Fixture();
        long id = f.List(count: 4, unitPrice: 100);
        f.Harness.Leave(1);
        f.Harness.Ticks(3); // the seller's session ends
        f.Harness.Clock.Advance(f.Settings.DurationsMs[0]);

        f.TickUntil(() => f.Events.OfType<ListingExpired>().Any());
        var call = Assert.Single(f.Releaser.Calls);
        Assert.Equal((f.Seller, (short)ListingState.Expired), (call.To, call.Close!.State));
        Assert.Equal(id, f.Events.OfType<ListingExpired>().Single().ListingId);
        Assert.Null(f.Listings.Find(id));
    }

    [Fact]
    public void A_purchase_that_does_not_fit_the_bag_arrives_by_mail_in_the_same_save()
    {
        var full = Enumerable.Range(0, 30).Select(s => EconomyFixture.Row(s, EconomyFixture.Horn, 1)).ToArray();
        var f = new Fixture(buyerBag: full);
        long id = f.List(count: 4, unitPrice: 10);
        f.Harness.SendListingBuy(2, 1, f.Npc, id, 2, 10);
        f.Harness.Ticks(4);

        var answer = f.Transport.ListingResultsFor(2).Single();
        Assert.Equal((ListingReason.None, true), (answer.Reason, answer.ToMail));
        var save = f.Harness.Saves.Jobs.Single(j => j.Listing is { Sales.Count: > 0 });
        var purchase = save.Mail!.Sends.Single(s => s.Draft.Kind == MailKind.ListingPurchase);
        Assert.Equal((f.Buyer, 2), (purchase.Draft.ToCharacterId, purchase.Draft.Items.Single().Count));
        Assert.Equal(EscrowTargetKind.Split, save.Escrow!.Releases.Single().ToKind); // part: the rest held again
        Assert.Equal(2, f.Listings.Find(id)!.CountLeft);
        Assert.Equal(0, f.Units(f.Buyer, EconomyFixture.Ash));
    }

    [Fact]
    public void A_search_goes_to_the_worker_and_its_page_comes_back_to_the_connection_that_asked()
    {
        var f = new Fixture();
        long id = f.List(count: 4, unitPrice: 100);
        var filters = new ListingSearchRequest { ItemNetIds = [f.Helpers.NetId(EconomyFixture.Ash)] };
        f.Harness.SendListingSearch(2, 5, f.Npc, filters);
        f.Harness.Ticks(1);
        var ask = Assert.Single(f.Search.Asks);
        Assert.Equal((f.Buyer, 5u, f.Settings.PageSize + 1), (ask.CharacterId, ask.RequestId, ask.Query.Limit));
        Assert.Equal([f.Helpers.NetId(EconomyFixture.Ash)], ask.Query.ItemNetIds);

        var escrow = f.Harness.World.Escrow!.Find(f.Listings.Find(id)!.EscrowId)!;
        f.Search.Answers.Enqueue(new ListingSearchAnswer(ask, [new ListingSearchRow(id, 100, 4, 99_000, EconomyFixture.Ash, 0,
            false, 0, false, escrow.Items[0].PropertiesJson)]));
        f.Harness.Ticks(1);

        var page = f.Transport.ListingPagesFor(2).Single();
        var row = Assert.Single(page.Rows);
        Assert.Equal((ListingReason.None, id, 100L, (ushort)4, false), (page.Reason, row.ListingId, row.UnitPrice, row.Item.Count, page.HasMore));
    }

    /// <summary>Releases to mail without a database: every call is recorded and released at once.</summary>
    internal sealed class FakeReleaser : IEscrowMailReleaser
    {
        public ConcurrentQueue<(string EscrowId, long To, short Kind, EscrowHolderClose? Close)> Calls { get; } = new();

        public Task<EscrowReleaseStatus> ReleaseToMailAsync(string escrowId, long toCharacterId, short mailKind, string releaseOpKey,
            long atUtcMs, EscrowHolderClose? close = null, CancellationToken ct = default)
        {
            Calls.Enqueue((escrowId, toCharacterId, mailKind, close));
            return Task.FromResult(EscrowReleaseStatus.Released);
        }
    }

    /// <summary>The search worker as a queue the test fills.</summary>
    internal sealed class FakeSearch : IListingSearchJobs
    {
        public List<ListingSearchAsk> Asks { get; } = [];
        public Queue<ListingSearchAnswer> Answers { get; } = new();

        public void Request(in ListingSearchAsk ask) => Asks.Add(ask);
        public bool TryTakeAnswer(out ListingSearchAnswer answer) => Answers.TryDequeue(out answer);
    }

    internal sealed class Fixture : IEventConsumer<ListingCreated>, IEventConsumer<ListingSold>, IEventConsumer<ListingCancelled>,
        IEventConsumer<ListingExpired>
    {
        public Fixture(int players = 2, int cap = 0, long sellerGold = SellerGold, ItemStackRow[]? buyerBag = null)
        {
            var tables = TestCombat.Tables();
            var settings = tables.Single<ListingSettings>();
            if (cap > 0)
                settings.MaxActivePerCharacter = cap;
            Settings = settings;
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<ListingCreated>(this);
            Bus.Subscribe<ListingSold>(this);
            Bus.Subscribe<ListingCancelled>(this);
            Bus.Subscribe<ListingExpired>(this);
            Ledger.Subscribe(Bus);
            var items = ItemRules.From(tables)!;
            var economy = EconomyRules.From(tables)!;
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(2_000, 0), EconomyFixture.Npc(Auctioneer, 200, 0, "m1.auctioneer"),
                EconomyFixture.Npc(FarAuctioneer, 5_000, 5_000, "m1.auctioneer"),
            ]);
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus,
                combat: TestCombat.Rules(tables, TestCombat.Point()), items: items, economy: economy,
                mail: MailRules.From(tables, items), listings: settings, listingSearch: Search, escrowReleaser: Releaser);
            for (int i = 1; i <= players; i++)
            {
                var character = Harness.Characters.Add(i, 0, new CharacterState(1, 0, "", 0, 0, 0, 0, 100, 50, 100));
                Harness.Characters.Items[character.CharacterId] = i == 1
                    ? [EconomyFixture.Row(0, EconomyFixture.Ash, 10), EconomyFixture.Row(1, EconomyFixture.Horn, 5)]
                    : i == 2 && buyerBag != null ? buyerBag : [];
                Harness.Characters.StoreGold(character.CharacterId, i == 1 ? sellerGold : BuyerGold);
                Characters.Add(character);
                Players.Add(Harness.Join(i));
            }
            Harness.Ticks(1);
            Helpers = new EconomyHelpers(items);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public EconomyLedger Ledger { get; } = new();
        public ListingSettings Settings { get; }
        public FakeReleaser Releaser { get; } = new();
        public FakeSearch Search { get; } = new();
        public EconomyHelpers Helpers { get; }
        public List<CharacterRecord> Characters { get; } = [];
        public List<Player> Players { get; } = [];
        public List<object> Events { get; } = [];
        public long Seller => Characters[0].CharacterId;
        public long Buyer => Characters[1].CharacterId;
        public ListingService Listings => Harness.World.Listings!;
        public InventoryService Inventory => Harness.World.Inventory!;
        public FakeTransport Transport => Harness.Transport;
        public uint Npc => NpcId(Auctioneer);
        public uint FarNpc => NpcId(FarAuctioneer);

        public uint NpcId(string markerId) => Harness.World.Entities.OfType<Npc>().Single(n => n.MarkerId == markerId).Id;

        /// <summary>The seller lists <paramref name="count"/> ash at <paramref name="unitPrice"/>; returns the committed listing's id.</summary>
        public long List(int count, long unitPrice)
        {
            uint request = (uint)(100 + Transport.ListingResultsFor(1).Count);
            Harness.SendListingCreate(1, request, Npc, slot: 0, count: (ushort)count, unitPrice: unitPrice);
            Harness.Ticks(4);
            var answer = Transport.ListingResultsFor(1).Single(r => r.RequestId == request);
            Assert.Equal(ListingReason.None, answer.Reason);
            return answer.ListingId;
        }

        /// <summary>Units of an item in a character's containers (the bag as the inventory holds it now).</summary>
        public int Units(long characterId, string itemId)
        {
            Assert.True(Helpers.Items.TryGetItem(itemId, out var item));
            var inventory = Inventory.CurrentInventory(characterId);
            int units = 0;
            for (int c = 0; c < inventory.ContainerCount; c++)
            {
                var bag = inventory.BagAt(c);
                for (int s = 0; s < bag.SlotCount; s++)
                {
                    if (bag[s].ItemNetId == item.NetId)
                        units += bag[s].Count;
                }
            }
            return units;
        }

        /// <summary>Ticks until <paramref name="done"/> (a release to mail runs off the world thread), at most two seconds.</summary>
        public void TickUntil(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(2);
            while (!done() && DateTime.UtcNow < until)
            {
                Harness.Ticks(1);
                Thread.Sleep(5);
            }
            Assert.True(done());
        }

        public void Handle(in EventEnvelope envelope, in ListingCreated payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ListingSold payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ListingCancelled payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ListingExpired payload) => Events.Add(payload);
    }

    internal sealed class EconomyHelpers(ItemRules items)
    {
        public ItemRules Items { get; } = items;

        public ushort NetId(string itemId)
        {
            Assert.True(Items.TryGetItem(itemId, out var item));
            return (ushort)item.NetId;
        }
    }
}
