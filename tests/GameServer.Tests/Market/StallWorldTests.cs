using System.Collections.Concurrent;
using GameServer.Entities;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Market;
using GameServer.Persistence;
using GameServer.Tests.Economy;
using GameServer.Tests.Support;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.World;

namespace GameServer.Tests.Market;

/// <summary>
/// Shop stalls in a running world (spec 4.5 "M4 stalls", M4 decision 7 and the user answers of 2026-11-13; WP-M4-8) with the
/// repository's tables: a stall opens on a free place in reach with its first listing, the stall's row riding the listing's save;
/// one stall per place and per character, decided in memory even for two opens in one tick; a buyer pays through the listing
/// model while the owner is offline; a stall closes when its last listing is gone, at its time or by its owner, and its unsold
/// items go back by mail; a purchase after the close hears why; a restart brings an open stall back on its place.
/// </summary>
[Trait("Category", "NoDuplication")]
public class StallWorldTests
{
    private const string PlaceA = "test.stall.a"; // ordinal 0, in reach of the spawn
    private const string PlaceB = "test.stall.b"; // ordinal 1, far
    private const long SellerGold = 100;
    private const long BuyerGold = 1_000;

    [Fact]
    public void A_stall_opens_with_its_first_listing_and_its_row_in_one_save_answered_after_the_commit()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;
        f.Harness.SendStall(1, f.Open(1, place: 0, count: 4, unitPrice: 100));
        f.Harness.Ticks(2);

        Assert.Empty(f.Transport.StallResultsFor(1));
        Assert.Empty(f.Harness.World.Entities.OfType<ShopStall>());
        var save = f.Harness.Saves.Jobs.Single(j => j.Shop is { IsEmpty: false });
        var shop = Assert.Single(save.Shop!.Opens);
        var listing = Assert.Single(save.Listing!.Creates);
        Assert.Equal((ListingKind.Stall, shop.ShopId, shop.ClosesUtcMs), (listing.Kind, listing.StallId, listing.ExpiresUtcMs));
        Assert.Equal((PlaceA, f.Seller), (shop.PlaceMarkerId, shop.OwnerId));
        Assert.Equal(f.Stalls.Settings.DurationMs, shop.ClosesUtcMs - shop.OpenedUtcMs);
        Assert.Single(save.Escrow!.Holds); // one transaction (rule 5)

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var answer = Assert.Single(f.Transport.StallResultsFor(1));
        var stall = Assert.Single(f.Harness.World.Entities.OfType<ShopStall>());
        Assert.Equal((StallAction.Open, StallReason.None, stall.Id, listing.ListingId, 8L),
            (answer.Action, answer.Reason, answer.StallEntityId, answer.ListingId, answer.Fee)); // 2 % of 400
        Assert.Equal((0, (ushort)0, shop.ShopId), (stall.PlaceOrdinal, stall.DefId, stall.ShopId));
        Assert.Empty(f.Transport.ListingResultsFor(1)); // the listing's own answer leaves as the stall's
        Assert.Equal(shop.ShopId, f.Events.OfType<StallOpened>().Single().ShopId);
        Assert.False(f.Stalls.IsFree(0));
    }

    [Fact]
    public void The_stall_shows_its_owner_through_the_display_identity()
    {
        var f = new Fixture();
        f.OpenStall(count: 2, unitPrice: 10);
        f.Harness.Ticks(4);
        var stall = f.Harness.World.Entities.OfType<ShopStall>().Single();
        Assert.Equal(f.Players[0].Name, GameServer.Social.DisplayIdentity.For(stall, f.Players[1]));
        Assert.Contains(f.Transport.IdentitiesFor(2), i => i.EntityId == stall.Id && i.Name == f.Players[0].Name);
    }

    [Fact]
    public void Each_refusal_has_its_reason_and_changes_nothing()
    {
        var f = new Fixture();
        f.Harness.SendStall(1, f.Open(1, place: 1, count: 1, unitPrice: 10)); // the far place
        f.Harness.SendStall(1, f.Open(2, place: 7, count: 1, unitPrice: 10)); // no such place
        f.Harness.SendStall(1, f.Open(3, place: 0, count: 1, unitPrice: 10, slot: 1)); // a sell-only horn
        f.Harness.SendStall(1, f.Open(4, place: 0, count: 1, unitPrice: 0)); // no price
        f.Harness.Ticks(1);

        Assert.Equal(new[] { StallReason.OutOfRange, StallReason.StallNotFound, StallReason.StallListingRefused, StallReason.StallListingRefused },
            f.Transport.StallResultsFor(1).Select(r => r.Reason));
        Assert.Equal(new[] { ListingReason.ListingSellOnly, ListingReason.ListingPriceInvalid },
            f.Transport.StallResultsFor(1).Skip(2).Select(r => r.ListingReason));
        Assert.True(f.Stalls.IsFree(0));
        Assert.Equal(SellerGold, f.Inventory.Gold(f.Seller));

        var stall = f.OpenStall(count: 2, unitPrice: 10);
        f.Harness.SendStall(1, f.Open(20, place: 0, count: 1, unitPrice: 10)); // a second stall
        f.Harness.SendStall(2, f.Request(21, StallAction.Add, stall, count: 1, unitPrice: 10)); // not the owner
        f.Harness.SendStall(2, f.Request(22, StallAction.Close, stall));
        f.Harness.Ticks(1);
        Assert.Equal(StallReason.StallOwnAlready, f.Transport.StallResultsFor(1)[^1].Reason);
        Assert.Equal(new[] { StallReason.StallNotYours, StallReason.StallNotYours }, f.Transport.StallResultsFor(2).Select(r => r.Reason));
    }

    [Fact]
    public void Two_players_opening_one_place_in_one_tick_give_one_stall_and_one_row()
    {
        var f = new Fixture(players: 3, sellerBags: 2);
        f.Harness.SendStall(1, f.Open(1, place: 0, count: 1, unitPrice: 10));
        f.Harness.SendStall(2, f.Open(1, place: 0, count: 1, unitPrice: 10));
        f.Harness.Ticks(4);

        var answers = f.Transport.StallResultsFor(1).Concat(f.Transport.StallResultsFor(2)).Select(r => r.Reason).ToList();
        Assert.Single(answers, r => r == StallReason.None);
        Assert.Single(answers, r => r == StallReason.StallPlaceTaken);
        Assert.Single(f.Harness.World.Entities.OfType<ShopStall>());
        Assert.Single(f.Harness.Saves.Jobs.Where(j => j.Shop is { IsEmpty: false }));
        Assert.Single(f.Harness.Saves.Jobs.SelectMany(j => j.Listing?.Creates ?? []));
    }

    [Fact]
    public void A_buyer_buys_while_the_owner_is_offline_and_the_proceeds_wait_in_the_owners_mail()
    {
        var f = new Fixture();
        var stall = f.OpenStall(count: 4, unitPrice: 100);
        long listing = f.Listings.ActiveForStall(f.ShopId(stall)).Single().ListingId;
        f.Harness.Leave(1);
        f.Harness.Ticks(3); // no linger in tests: the owner is offline
        Assert.False(f.Inventory.IsAttached(f.Seller));
        Assert.NotNull(f.Stalls.Find(stall)); // a stall sells while its owner is offline (M4 decision 7)

        f.Harness.SendStall(2, f.Request(1, StallAction.Browse, stall));
        f.Harness.Ticks(1);
        var state = f.Transport.StallStatesFor(2).Single();
        Assert.Equal((stall, false, true, listing), (state.StallEntityId, state.Own, state.Open, state.Rows.Single().ListingId));

        f.Harness.SendStall(2, f.Request(2, StallAction.Buy, stall, listingId: listing, count: 1, unitPrice: 100));
        f.Harness.Ticks(4);
        var bought = f.Transport.StallResultsFor(2).Single(r => r.Action == StallAction.Buy);
        Assert.Equal((StallReason.None, (ushort)1, 100L, 5L), (bought.Reason, bought.Count, bought.Price, bought.Tax));
        var proceeds = f.Harness.Saves.Jobs.SelectMany(j => j.Mail?.Sends ?? []).Single(s => s.Draft.Kind == MailKind.ListingProceeds);
        Assert.Equal((f.Seller, 95L), (proceeds.Draft.ToCharacterId, proceeds.Draft.Gold)); // price - tax, carried by the buyer's save
        Assert.Equal(1, f.Units(f.Buyer, EconomyFixture.Ash));
        Assert.Contains(f.Transport.StallStatesFor(2), s => s.Rows.Length == 1 && s.Rows[0].Item.Count == 3); // the viewer sees the change
        Assert.Empty(f.Transport.ListingResultsFor(2));
    }

    [Fact]
    public void The_last_sale_closes_the_stall_and_frees_its_place_and_a_later_buy_hears_it_closed()
    {
        var f = new Fixture();
        var stall = f.OpenStall(count: 2, unitPrice: 10);
        long listing = f.Listings.ActiveForStall(f.ShopId(stall)).Single().ListingId;
        f.Harness.SendStall(2, f.Request(1, StallAction.Buy, stall, listingId: listing, count: 2, unitPrice: 10));
        f.Harness.Ticks(4);

        Assert.Null(f.Stalls.Find(stall));
        Assert.DoesNotContain(f.Harness.World.Entities, e => e.Id == stall);
        var closed = f.Events.OfType<StallClosed>().Single();
        Assert.Equal((short)ShopCloseReason.SoldOut, closed.Reason);
        Assert.Equal((f.ShopId(closed), ShopCloseReason.SoldOut), f.Store.Closes.Single());
        f.TickUntil(() => f.Stalls.IsFree(0)); // the close is written off the tick

        f.Harness.SendStall(2, f.Request(2, StallAction.Buy, stall, listingId: listing, count: 1, unitPrice: 10));
        f.Harness.Ticks(1);
        Assert.Equal(StallReason.StallClosed, f.Transport.StallResultsFor(2)[^1].Reason);
    }

    [Fact]
    public void The_owner_adds_and_closes_and_the_unsold_items_go_back_by_mail()
    {
        var f = new Fixture();
        var stall = f.OpenStall(count: 2, unitPrice: 10);
        f.Harness.SendStall(1, f.Request(5, StallAction.Add, stall, count: 3, unitPrice: 20));
        f.Harness.Ticks(4);
        Assert.Equal(StallReason.None, f.Transport.StallResultsFor(1).Single(r => r.RequestId == 5).Reason);
        Assert.Equal(2, f.Listings.ActiveForStall(f.ShopId(stall)).Count);
        Assert.Equal(0, f.Listings.CountFor(f.Seller, ListingKind.Auction)); // the auction's cap counts apart (user answer 2026-11-13)

        f.Harness.SendStall(1, f.Request(6, StallAction.Close, stall));
        f.TickUntil(() => f.Transport.StallResultsFor(1).Any(r => r.RequestId == 6));
        Assert.Equal(StallReason.None, f.Transport.StallResultsFor(1).Single(r => r.RequestId == 6).Reason);
        f.TickUntil(() => f.Releaser.Calls.Count == 2);
        Assert.All(f.Releaser.Calls, c => Assert.Equal((f.Seller, (short)MailKind.ListingReturn, (short)ListingState.Cancelled),
            (c.To, c.Kind, c.Close!.State)));
        Assert.Equal((short)ShopCloseReason.Owner, f.Events.OfType<StallClosed>().Single().Reason);
        Assert.Equal(SellerGold - 1, f.Inventory.Gold(f.Seller)); // the fees are kept: 0 for 20 gold, 1 for 60
    }

    [Fact]
    public void A_stall_closes_at_its_time_and_its_listings_expire_back_to_the_offline_owner()
    {
        var f = new Fixture();
        var stall = f.OpenStall(count: 4, unitPrice: 10);
        f.Harness.Leave(1);
        f.Harness.Ticks(3);
        f.Harness.Clock.Advance(f.Stalls.Settings.DurationMs);

        f.TickUntil(() => f.Events.OfType<ListingExpired>().Any() && f.Events.OfType<StallClosed>().Any());
        Assert.Equal((short)ShopCloseReason.Time, f.Events.OfType<StallClosed>().Single().Reason);
        Assert.Equal((f.Seller, (short)ListingState.Expired), f.Releaser.Calls.Select(c => (c.To, c.Close!.State)).Single());
        Assert.Null(f.Stalls.Find(stall));
    }

    [Fact]
    public void A_stall_survives_a_restart_on_its_place_and_a_stale_one_closes()
    {
        var f = new Fixture();
        long now = f.Harness.Clock.UtcNowMs;
        var kept = new ShopRow("shop:kept", f.Seller, PlaceA, "Seller", now - 1_000, now + 60_000);
        var stale = new ShopRow("shop:stale", f.Buyer, PlaceB, "Buyer", now - 1_000, now + 60_000); // no listing left
        var listing = new ListingRow(41, "listing:kept", ListingKind.Stall, kept.ShopId, f.Seller, "escrow:kept", EconomyFixture.Ash,
            f.Helpers.NetId(EconomyFixture.Ash), 0, 0, 0, 0, 0, 10, 2, 2, 0, now - 1_000, kept.ClosesUtcMs);
        var restarted = new Fixture(openStalls: [kept, stale], listings: [listing], escrows:
            [new EscrowRecord("escrow:kept", f.Seller, EscrowHolderKind.Stall, "41", 0, [new EscrowItemRow(EconomyFixture.Ash, 0, false, 2, 0, false, null)])]);

        var stall = Assert.Single(restarted.Harness.World.Entities.OfType<ShopStall>());
        Assert.Equal((kept.ShopId, 0, kept.ClosesUtcMs), (stall.ShopId, stall.PlaceOrdinal, stall.ClosesUtcMs));
        Assert.Equal((stale.ShopId, ShopCloseReason.Stale), restarted.Store.Closes.Single());
        restarted.Harness.Ticks(2);
        Assert.True(restarted.Stalls.IsFree(1));
    }

    /// <summary>The marker of the extra place <paramref name="i"/> (ordinal 2 + i: after PlaceA and PlaceB in id order).</summary>
    private static string MorePlace(int i) => $"test.stall.p{i:00}";

    [Fact]
    public void Twelve_open_stalls_and_fifty_players_in_view_fit_the_steady_snapshots()
    {
        const int Stalls = 12;
        long now = new FakeClock().UtcNowMs;
        var shops = Enumerable.Range(0, Stalls).Select(i => new ShopRow($"shop:{i}", 1_000 + i, MorePlace(i), $"Owner{i}", now - 1_000, now + 3_600_000)).ToList();
        var listings = shops.Select((s, i) => new ListingRow(100 + i, $"listing:{i}", ListingKind.Stall, s.ShopId, s.OwnerId, $"escrow:{i}",
            EconomyFixture.Ash, 0, 0, 0, 0, 0, 0, 10, 1, 1, 0, now - 1_000, s.ClosesUtcMs)).ToList();
        var escrows = shops.Select((s, i) => new EscrowRecord($"escrow:{i}", s.OwnerId, EscrowHolderKind.Stall, (100 + i).ToString(),
            0, [new EscrowItemRow(EconomyFixture.Ash, 0, false, 1, 0, false, null)])).ToList();
        var f = new Fixture(players: 50, sellerBags: 0, openStalls: shops, morePlaces: Stalls, listings: listings, escrows: escrows);
        var stalls = f.Harness.World.Entities.OfType<ShopStall>().Select(s => s.Id).ToHashSet();
        Assert.Equal(Stalls, stalls.Count);
        f.Harness.Ticks(20); // the joins settle: every first snapshot went out
        long truncated = f.Harness.World.SnapshotsTruncated;

        f.Harness.Ticks(40);

        Assert.Equal(truncated, f.Harness.World.SnapshotsTruncated); // steady snapshots carry 50 players and 12 stalls in one datagram
        for (int peer = 1; peer <= 50; peer++)
            Assert.Superset(stalls, f.Harness.LastSnapshot(peer).Entities.Select(e => e.EntityId).ToHashSet());
    }

    /// <summary>Closes written by the stall's close pump, recorded.</summary>
    internal sealed class FakeShopStore : IShopStore
    {
        public ConcurrentQueue<(string ShopId, ShopCloseReason Reason)> Closes { get; } = new();

        public Task<bool> CloseAsync(string shopId, ShopCloseReason reason, long atUtcMs, CancellationToken ct)
        {
            Closes.Enqueue((shopId, reason));
            return Task.FromResult(true);
        }
    }

    internal sealed class Fixture : IEventConsumer<ListingSold>, IEventConsumer<ListingExpired>, IEventConsumer<StallOpened>,
        IEventConsumer<StallClosed>
    {
        public Fixture(int players = 2, int sellerBags = 1, IReadOnlyList<ShopRow>? openStalls = null, int morePlaces = 0,
            IReadOnlyList<ListingRow>? listings = null, IReadOnlyList<EscrowRecord>? escrows = null)
        {
            var tables = TestCombat.Tables();
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<ListingSold>(this);
            Bus.Subscribe<ListingExpired>(this);
            Bus.Subscribe<StallOpened>(this);
            Bus.Subscribe<StallClosed>(this);
            var items = ItemRules.From(tables)!;
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(2_000, 0),
                new WorldMarker(PlaceA, MarkerKind.Stall, FixVec2.FromCm(200, 0), 0),
                new WorldMarker(PlaceB, MarkerKind.Stall, FixVec2.FromCm(-3_000, 0), 0),
                .. Enumerable.Range(0, morePlaces).Select(i => new WorldMarker(MorePlace(i), MarkerKind.Stall, FixVec2.FromCm(-2_400 + i * 400, 600), 0)),
            ]);
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus,
                combat: TestCombat.Rules(tables, TestCombat.Point()), items: items, economy: EconomyRules.From(tables)!,
                mail: MailRules.From(tables, items), listings: tables.Single<ListingSettings>(), listingSearch: null,
                escrowReleaser: Releaser, stalls: tables.Single<StallSettings>(), shopStore: Store, openStalls: openStalls,
                escrows: escrows, loadListings: listings);
            for (int i = 1; i <= players; i++)
            {
                var character = Harness.Characters.Add(i, 0, new CharacterState(1, 0, "", 0, 0, 0, 0, 100, 50, 100));
                Harness.Characters.Items[character.CharacterId] = i <= sellerBags
                    ? [EconomyFixture.Row(0, EconomyFixture.Ash, 10), EconomyFixture.Row(1, EconomyFixture.Horn, 5)]
                    : [];
                Harness.Characters.StoreGold(character.CharacterId, i <= sellerBags ? SellerGold : BuyerGold);
                Characters.Add(character);
                Players.Add(Harness.Join(i));
            }
            Harness.Ticks(1);
            Helpers = new ListingWorldTests.EconomyHelpers(items);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public ListingWorldTests.FakeReleaser Releaser { get; } = new();
        public FakeShopStore Store { get; } = new();
        public ListingWorldTests.EconomyHelpers Helpers { get; }
        public List<CharacterRecord> Characters { get; } = [];
        public List<Player> Players { get; } = [];
        public List<object> Events { get; } = [];
        public long Seller => Characters[0].CharacterId;
        public long Buyer => Characters[1].CharacterId;
        public StallService Stalls => Harness.World.Stalls!;
        public ListingService Listings => Harness.World.Listings!;
        public InventoryService Inventory => Harness.World.Inventory!;
        public FakeTransport Transport => Harness.Transport;

        public string ShopId(uint stallEntityId) => Stalls.Find(stallEntityId)!.ShopId;

        public string ShopId(StallClosed closed) => closed.ShopId;

        public StallRequest Open(uint requestId, byte place, ushort count, long unitPrice, byte slot = 0) => new()
        {
            RequestId = requestId, Action = StallAction.Open, Place = place, Container = 1, Slot = slot, Count = count, UnitPrice = unitPrice,
        };

        public StallRequest Request(uint requestId, StallAction action, uint stall, long listingId = 0, ushort count = 0, long unitPrice = 0,
            byte slot = 0) => new()
        {
            RequestId = requestId, Action = action, StallEntityId = stall, ListingId = listingId, Container = 1, Slot = slot, Count = count,
            UnitPrice = unitPrice,
        };

        /// <summary>Player 1 opens a stall on place 0 with ash; returns the stall's entity id once committed.</summary>
        public uint OpenStall(ushort count, long unitPrice)
        {
            uint request = (uint)(100 + Transport.StallResultsFor(1).Count);
            Harness.SendStall(1, Open(request, place: 0, count, unitPrice));
            Harness.Ticks(4);
            var answer = Transport.StallResultsFor(1).Single(r => r.RequestId == request);
            Assert.Equal(StallReason.None, answer.Reason);
            return answer.StallEntityId;
        }

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

        /// <summary>Ticks until <paramref name="done"/> (releases and closes run off the world thread), at most two seconds.</summary>
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

        public void Handle(in EventEnvelope envelope, in ListingSold payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in ListingExpired payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in StallOpened payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in StallClosed payload) => Events.Add(payload);
    }
}
