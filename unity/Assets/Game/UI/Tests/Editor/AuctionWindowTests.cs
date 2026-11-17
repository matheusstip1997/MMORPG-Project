#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Auction;
using Game.UI.ViewModels.Auction;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.UI.Tests
{
    /// <summary>
    /// The auction window (spec 4.5, 9.13; M4 decision 6; WP-M4-7): the view model's pages and cursor stack, the one pending action,
    /// the notices of a sold-out, busy or changed listing and the sell form that follows the bag; the binder's opening at the
    /// auctioneer, the name filter turned into item net ids, the requests and the toasts and asks after an answer; the presenter's
    /// rows (no seller), notice, fee and duration; and the committed texts, reasons and layout row.
    /// </summary>
    public sealed class AuctionWindowTests
    {
        private const ushort Ash = 2; // ItemDef m1.demonAsh
        private const uint Auctioneer = 9;
        private const ushort AuctioneerNetId = 7; // NpcDef m1.auctioneer
        private const long HourMs = 3_600_000L;

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static ListingEntry Entry(long id, long unitPrice, int count = 4, ushort item = Ash, long expiresUtcMs = 2 * HourMs + 1) =>
            new ListingEntry
            {
                ListingId = id, UnitPrice = unitPrice, ExpiresUtcMs = expiresUtcMs,
                Item = new InventorySlotEntry { ItemNetId = item, Count = (ushort)count },
            };

        private static ListingSearchResponse Page(uint requestId, bool more, params ListingEntry[] rows) =>
            new ListingSearchResponse { RequestId = requestId, Rows = rows, HasMore = more };

        // ---- view model

        [Test]
        public void Pages_walk_a_cursor_stack_and_only_the_pending_search_is_shown()
        {
            var model = new AuctionWindowViewModel();
            model.OpenFor(Auctioneer);
            Assert.That(model.FirstPage(new[] { Ash }, 2, 1, 0, false, out var first), Is.True);
            Assert.That((first.Category, first.RarityMin, first.AfterUnitPrice, first.AfterListingId), Is.EqualTo(((byte)2, (byte)1, 0L, 0L)));
            model.SearchSent(1);
            Assert.That(model.CanSearch, Is.False);
            Assert.That(model.OnPage(Page(7, false, Entry(1, 5))), Is.Null); // not ours
            Assert.That(model.HasPage, Is.False);
            model.OnPage(Page(1, true, Entry(1, 5), Entry(2, 9)));
            Assert.That((model.Rows.Count, model.CanNext, model.CanPrevious), Is.EqualTo((2, true, false)));

            Assert.That(model.NextPage(first, out var second), Is.True);
            Assert.That((second.AfterUnitPrice, second.AfterListingId), Is.EqualTo((9L, 2L)));
            Assert.That(second.ItemNetIds, Is.EqualTo(new[] { Ash }));
            model.SearchSent(2);
            model.OnPage(Page(2, false, Entry(3, 12)));
            Assert.That((model.PageIndex, model.CanNext, model.CanPrevious), Is.EqualTo((1, false, true)));
            Assert.That(model.PreviousPage(first, out var back), Is.True);
            Assert.That((back.AfterUnitPrice, back.AfterListingId, model.PageIndex), Is.EqualTo((0L, 0L, 1))); // moves when the page comes
            model.SearchSent(3);
            model.OnPage(Page(3, true, Entry(1, 5), Entry(2, 9)));
            Assert.That((model.PageIndex, model.CanPrevious), Is.EqualTo((0, false)));
        }

        [Test]
        public void A_typed_name_that_matches_no_item_asks_nothing_and_says_so()
        {
            var model = new AuctionWindowViewModel();
            model.OpenFor(Auctioneer);
            model.SetName("zzz");
            Assert.That(model.FirstPage(new ushort[0], 0, 0, 0, false, out _), Is.False);
            Assert.That((model.NoMatch, model.HasPage, model.Rows.Count), Is.EqualTo((true, true, 0)));
            model.SetName("");
            Assert.That(model.FirstPage(new ushort[0], 0, 0, 0, false, out _), Is.True); // no name: every item
            Assert.That(model.NoMatch, Is.True); // the shown page's flag until the new page arrives
            model.SearchSent(1);
            model.OnPage(Page(1, false, Entry(1, 5)));
            Assert.That(model.NoMatch, Is.False);
        }

        [Test]
        public void Sold_changed_and_busy_listings_stay_in_the_window_as_a_notice_until_the_next_page()
        {
            var model = new AuctionWindowViewModel();
            model.OpenFor(Auctioneer);
            model.FirstPage(new ushort[0], 0, 0, 0, false, out _);
            model.SearchSent(1);
            model.OnPage(Page(1, false, Entry(1, 5), Entry(2, 6)));
            model.Select(1);
            Assert.That(model.TryBuy(out var buy), Is.True);
            Assert.That((buy.ListingId, buy.Count, buy.UnitPrice), Is.EqualTo((1L, 1, 5L)));
            model.ActionSent(4);
            Assert.That(model.CanBuy, Is.False); // one at a time

            Assert.That(model.OnResult(new ListingResult { RequestId = 4, Action = ListingAction.Buy, Reason = ListingReason.ListingBusy }, out bool applied),
                Is.EqualTo("ListingBusy"));
            Assert.That((applied, model.Notice, model.Selected), Is.EqualTo((false, "ListingBusy", 1L))); // busy: try the same again
            model.ActionSent(5);
            model.OnResult(new ListingResult { RequestId = 5, Action = ListingAction.Buy, Reason = ListingReason.ListingSold }, out _);
            Assert.That((model.Notice, model.Selected), Is.EqualTo(("ListingSold", 0L)));
            model.Select(2);
            model.ActionSent(6);
            model.OnResult(new ListingResult { RequestId = 6, Action = ListingAction.Buy, Reason = ListingReason.ListingChanged }, out _);
            Assert.That((model.Notice, model.Selected), Is.EqualTo(("ListingChanged", 0L)));

            model.SearchSent(7); // the binder's own refresh of the shown page: the notice stays
            model.OnPage(Page(7, false, Entry(2, 8)));
            Assert.That(model.Notice, Is.EqualTo("ListingChanged"));
            model.Select(2); // the player's own next action: it goes
            Assert.That(model.Notice, Is.Empty);
        }

        [Test]
        public void The_buy_count_stays_within_the_rows_units_and_the_total_follows_it()
        {
            var model = new AuctionWindowViewModel();
            model.OpenFor(Auctioneer);
            model.FirstPage(new ushort[0], 0, 0, 0, false, out _);
            model.SearchSent(1);
            model.OnPage(Page(1, false, Entry(1, 15, count: 4)));
            model.Select(1);
            model.SetBuyCount("9");
            Assert.That((model.BuyCount, model.BuyTotal), Is.EqualTo((4, 60L)));
            model.SetBuyCount("x");
            Assert.That(model.BuyCount, Is.EqualTo(1));
        }

        [Test]
        public void The_sell_form_follows_the_bag_and_an_applied_listing_clears_it()
        {
            var model = new AuctionWindowViewModel();
            model.OpenFor(Auctioneer);
            var stack = new InventorySlotEntry { Slot = 3, ItemNetId = Ash, Count = 5 };
            model.SetSellItem(1, stack);
            Assert.That((model.Tab, model.SellCount, model.CanList), Is.EqualTo((AuctionTab.Sell, 5, false))); // no price yet
            model.SetSellCount("2");
            model.SetSellPrice("15");
            Assert.That(model.TryList(out var sale), Is.True);
            Assert.That((sale.Container, sale.Slot, sale.Count, sale.UnitPrice, sale.DurationIndex), Is.EqualTo(((byte)1, (byte)3, 2, 15L, 0)));

            model.RefreshSellItem(new InventorySlotEntry { Slot = 3, ItemNetId = Ash, Count = 1 }); // one was used
            Assert.That(model.SellCount, Is.EqualTo(1));
            model.RefreshSellItem(new InventorySlotEntry { Slot = 3, ItemNetId = Ash + 1, Count = 1 }); // another item in the slot
            Assert.That(model.SellItem, Is.Null);

            model.SetSellItem(1, stack);
            model.ActionSent(2);
            Assert.That(model.OnResult(new ListingResult { RequestId = 2, Action = ListingAction.Create, Count = 5 }, out bool applied), Is.Null);
            Assert.That((applied, model.SellItem), Is.EqualTo((true, (InventorySlotEntry?)null)));
            Assert.That(model.SellPrice, Is.EqualTo(15)); // the price stays for the next stack
        }

        // ---- binder

        private sealed class FakeListingRequests : IListingRequests
        {
            public List<string> Sent { get; } = new List<string>();
            public List<ListingSearchRequest> Searches { get; } = new List<ListingSearchRequest>();

            public uint Search(uint npcEntityId, ListingSearchRequest filters)
            {
                Searches.Add(filters);
                return Add($"search at {npcEntityId} after {filters.AfterUnitPrice}:{filters.AfterListingId}");
            }

            public uint CreateListing(uint npcEntityId, byte container, byte slot, int count, long unitPrice, int durationIndex) =>
                Add($"create {container}:{slot} x{count} at {unitPrice} for {durationIndex}");

            public uint BuyListing(uint npcEntityId, long listingId, int count, long unitPrice) => Add($"buy {listingId} x{count} at {unitPrice}");

            public uint CancelListing(uint npcEntityId, long listingId) => Add($"cancel {listingId}");

            public uint RequestMyListings(uint npcEntityId) => Add("mine");

            /// <summary>Not connected: nothing is sent, every request answers 0.</summary>
            public bool Offline { get; set; }

            private uint Add(string what)
            {
                if (Offline)
                    return 0;
                Sent.Add(what);
                return (uint)Sent.Count;
            }
        }

        private static WorldClientFeed Feed(ConfigSet set)
        {
            var settings = PredictionSettings.From(set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
            var catalog = ActionCatalog.From(set);
            return new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs);
        }

        private sealed class Rig
        {
            public Rig()
            {
                var set = Tables();
                Hud = HudConfig.From(set);
                Config = AuctionConfig.From(set);
                Feed = AuctionWindowTests.Feed(set);
                Feed.ListingRequests = Requests;
                Binder = new AuctionBinder(Config, Hud.Text, Model, Toast, 2_000, () => 0);
                Binder.Bind(Feed);
            }

            public HudConfig Hud { get; }
            public AuctionConfig Config { get; }
            public WorldClientFeed Feed { get; }
            public FakeListingRequests Requests { get; } = new FakeListingRequests();
            public AuctionWindowViewModel Model { get; } = new AuctionWindowViewModel();
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public AuctionBinder Binder { get; }

            /// <summary>The auctioneer in reach says yes: the window opens there with the own listings pushed along.</summary>
            public void Open()
            {
                Feed.SetNpcTarget(Auctioneer, AuctioneerNetId);
                Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 1, NpcEntityId = Auctioneer, Kind = NpcInteraction.Auction });
                Feed.RaiseMyListings(new MyListings { RequestId = 0, Cap = 10, Rows = new[] { Entry(30, 99) } });
                Binder.Frame(0);
            }

            /// <summary>Searches with no filter and answers it with <paramref name="rows"/>.</summary>
            public void SearchAll(params ListingEntry[] rows)
            {
                Binder.Search();
                Feed.RaiseListingPage(Page((uint)Requests.Sent.Count, false, rows));
            }
        }

        [Test]
        public void The_auctioneers_yes_opens_the_window_there_and_it_closes_when_the_auctioneer_leaves_reach()
        {
            var rig = new Rig();
            rig.Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 1, NpcEntityId = Auctioneer, Kind = NpcInteraction.Mail });
            rig.Feed.RaiseOutpostInteracted(new OutpostInteractResult
            {
                RequestId = 2, NpcEntityId = Auctioneer, Kind = NpcInteraction.Auction, Reason = OutpostReason.OutOfRange,
            });
            Assert.That(rig.Model.Open, Is.False);

            rig.Open();
            Assert.That((rig.Model.Open, rig.Model.NpcEntityId, rig.Model.Tab), Is.EqualTo((true, Auctioneer, AuctionTab.Search)));
            Assert.That((rig.Model.HasMine, rig.Model.Mine.Count, rig.Model.MineCap), Is.EqualTo((true, 1, 10)));
            Assert.That(rig.Requests.Sent, Is.Empty); // nothing asked until the player searches

            rig.Feed.SetNpcTarget(0, 0);
            rig.Binder.Frame(1);
            Assert.That(rig.Model.Open, Is.False);
        }

        [Test]
        public void Search_turns_the_typed_name_into_item_net_ids_and_never_sends_the_text()
        {
            var rig = new Rig();
            rig.Open();
            rig.Binder.EditName("demon ASH");
            rig.Binder.CycleCategory(); // Materials
            rig.Binder.Search();
            var request = rig.Requests.Searches.Single();
            Assert.That(request.ItemNetIds, Does.Contain(Ash));
            Assert.That(request.Category, Is.EqualTo((byte)((byte)ItemCategory.Material + 1)));

            rig.Feed.RaiseListingPage(Page(1, true, Entry(1, 5), Entry(2, 7)));
            rig.Binder.Next();
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo($"search at {Auctioneer} after 7:2"));
            Assert.That(rig.Requests.Searches.Last().ItemNetIds, Does.Contain(Ash)); // the same filters

            rig.Feed.RaiseListingPage(Page(2, false));
            rig.Binder.EditName("no item is called this");
            rig.Binder.Search();
            Assert.That(rig.Requests.Sent.Count, Is.EqualTo(2));
            Assert.That(rig.Model.NoMatch, Is.True);
        }

        [Test]
        public void An_applied_buy_toasts_what_was_bought_and_asks_for_the_shown_page_again()
        {
            var rig = new Rig();
            rig.Open();
            rig.SearchAll(Entry(1, 15, count: 4));
            rig.Binder.SelectRow(1);
            rig.Binder.EditBuyCount("2");
            rig.Binder.Buy();
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("buy 1 x2 at 15"));
            rig.Binder.Buy();
            Assert.That(rig.Requests.Sent.Count, Is.EqualTo(2)); // one at a time

            rig.Feed.RaiseListingAnswered(new ListingResult { RequestId = 2, Action = ListingAction.Buy, ListingId = 1, Count = 2, Price = 30, Tax = 1 });
            Assert.That((rig.Toast.TextId, rig.Toast.ArgValue, rig.Toast.Arg2Value), Is.EqualTo((AuctionTextIds.ToastBought, "Demon Ash x2", "30")));
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo($"search at {Auctioneer} after 0:0"));

            rig.Feed.RaiseListingPage(Page(3, false, Entry(1, 15, count: 2)));
            rig.Binder.SelectRow(1);
            rig.Binder.Buy();
            rig.Feed.RaiseListingAnswered(new ListingResult { RequestId = 4, Action = ListingAction.Buy, ListingId = 1, Count = 1, Price = 15, ToMail = true });
            Assert.That((rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((AuctionTextIds.ToastBoughtMail, "Demon Ash x1")));
        }

        [Test]
        public void A_sold_out_or_changed_listing_toasts_its_reason_and_asks_again_but_a_busy_one_only_says_so()
        {
            var rig = new Rig();
            rig.Open();
            rig.SearchAll(Entry(1, 15), Entry(2, 16));
            rig.Binder.SelectRow(1);
            rig.Binder.Buy();
            rig.Feed.RaiseListingAnswered(new ListingResult { RequestId = 2, Action = ListingAction.Buy, Reason = ListingReason.ListingBusy });
            Assert.That((rig.Toast.ReasonCode, rig.Model.Notice, rig.Requests.Sent.Count), Is.EqualTo(("ListingBusy", "ListingBusy", 2)));

            rig.Binder.Buy(); // the same row again, a moment later
            rig.Feed.RaiseListingAnswered(new ListingResult { RequestId = 3, Action = ListingAction.Buy, Reason = ListingReason.ListingSold });
            Assert.That((rig.Toast.ReasonCode, rig.Model.Notice), Is.EqualTo(("ListingSold", "ListingSold")));
            Assert.That(rig.Requests.Sent.Last(), Does.StartWith("search")); // the shown page again

            rig.Feed.RaiseListingPage(Page(4, false, Entry(2, 18)));
            rig.Binder.SelectRow(2);
            rig.Binder.Buy();
            rig.Feed.RaiseListingAnswered(new ListingResult { RequestId = 5, Action = ListingAction.Buy, Reason = ListingReason.ListingChanged });
            Assert.That((rig.Toast.ReasonCode, rig.Model.Notice), Is.EqualTo(("ListingChanged", "ListingChanged")));
            Assert.That(rig.Requests.Sent.Last(), Does.StartWith("search"));
        }

        [Test]
        public void Listing_and_cancelling_send_their_requests_and_an_applied_one_asks_for_the_own_listings()
        {
            var rig = new Rig();
            rig.Open();
            rig.Feed.RaiseInventory(new InventoryContents
            {
                Revision = 1, SlotCount = 8, Slots = new[] { new InventorySlotEntry { Slot = 3, ItemNetId = Ash, Count = 5 } },
            });
            Assert.That(rig.Model.Sellable.Count, Is.EqualTo(1));
            rig.Binder.PickSell(rig.Model.SellableContainer, rig.Model.Sellable[0]);
            rig.Binder.EditSellCount("4");
            rig.Binder.EditSellPrice("15");
            rig.Binder.CycleDuration();
            rig.Binder.List();
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo($"create {rig.Config.Inventory.NormalBagCode}:3 x4 at 15 for 1"));
            rig.Feed.RaiseListingAnswered(new ListingResult { RequestId = 1, Action = ListingAction.Create, ListingId = 31, Count = 4, Fee = 1 });
            Assert.That((rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((AuctionTextIds.ToastListed, "Demon Ash x4")));
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("mine"));
            rig.Feed.RaiseMyListings(new MyListings { RequestId = 2, Cap = 10, Rows = new[] { Entry(30, 99), Entry(31, 15) } });
            Assert.That(rig.Model.Mine.Count, Is.EqualTo(2));

            rig.Binder.CancelListing(31);
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("cancel 31"));
            rig.Feed.RaiseListingAnswered(new ListingResult { RequestId = 3, Action = ListingAction.Cancel, ListingId = 31, Count = 4 });
            Assert.That(rig.Toast.TextId, Is.EqualTo(AuctionTextIds.ToastCancelled));
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("mine"));

            rig.Feed.RaiseDisconnected("Disconnected");
            Assert.That((rig.Model.Open, rig.Model.HasMine, rig.Model.Sellable.Count), Is.EqualTo((false, false, 0)));
        }

        [Test]
        public void A_text_field_with_focus_turns_the_gameplay_keys_off_until_it_loses_it_or_the_window_closes()
        {
            var rig = new Rig();
            rig.Open();
            rig.Binder.FocusField(true);
            Assert.That(rig.Feed.TextInputActive, Is.True);
            rig.Binder.CloseAuction();
            Assert.That((rig.Feed.TextInputActive, rig.Model.Open), Is.EqualTo((false, false)));
        }

        // ---- presenter

        private static AuctionWindowPresenter Presenter(out Rig rig, System.Func<long>? utcNowMs = null)
        {
            rig = new Rig();
            var presenter = new AuctionWindowPresenter(rig.Hud, new HudView(rig.Hud), rig.Config, rig.Model, rig.Binder, utcNowMs ?? (() => 0));
            presenter.Bind();
            return presenter;
        }

        [Test]
        public void The_window_lists_rows_without_a_seller_and_shows_a_notice_in_plain_words()
        {
            var presenter = Presenter(out var rig);
            Assert.That(presenter.IsShown, Is.False);
            rig.Open();
            Assert.That(presenter.IsShown, Is.True);
            rig.SearchAll(Entry(1, 15, count: 4, expiresUtcMs: 2 * HourMs + 1), Entry(2, 16, count: 1, expiresUtcMs: 20 * 60_000L));
            Assert.That(presenter.RowCount, Is.EqualTo(2));
            Assert.That(presenter.RowLabel(0), Is.EqualTo("Demon Ash x4   15 gold each   2 h left"));
            Assert.That(presenter.RowLabel(1), Is.EqualTo("Demon Ash x1   16 gold each   20 min left"));
            Assert.That(presenter.BuyButton.enabledSelf, Is.False); // nothing selected

            rig.Binder.SelectRow(1);
            rig.Binder.EditBuyCount("3");
            Assert.That((presenter.BuyButton.enabledSelf, presenter.TotalText), Is.EqualTo((true, "Total 45 gold")));
            rig.Binder.Buy();
            Assert.That((presenter.BuyButton.enabledSelf, presenter.WaitingShown), Is.EqualTo((false, true)));
            rig.Feed.RaiseListingAnswered(new ListingResult { RequestId = 2, Action = ListingAction.Buy, Reason = ListingReason.ListingSold });
            Assert.That(presenter.NoticeText, Is.EqualTo("That listing just sold out."));
            rig.Feed.RaiseListingPage(Page(3, false)); // the automatic refresh after the notice
            Assert.That((presenter.NoticeText, presenter.EmptyShown, presenter.EmptyText),
                Is.EqualTo(("That listing just sold out.", true, "Nothing is listed that matches.")));
        }

        [Test]
        public void The_sell_tab_shows_the_stack_the_duration_and_the_listing_fee_and_my_listings_the_cap()
        {
            var presenter = Presenter(out var rig);
            rig.Open();
            rig.Feed.RaiseInventory(new InventoryContents
            {
                Revision = 1, SlotCount = 8, Slots = new[] { new InventorySlotEntry { Slot = 3, ItemNetId = Ash, Count = 5 } },
            });
            rig.Binder.SelectTab(AuctionTab.Sell);
            Assert.That(presenter.PickCount, Is.EqualTo(1));
            Assert.That(presenter.ListButton.enabledSelf, Is.False);
            rig.Binder.PickSell(rig.Model.SellableContainer, rig.Model.Sellable[0]);
            rig.Binder.EditSellCount("4");
            rig.Binder.EditSellPrice("15");
            Assert.That(presenter.SellItemText, Is.EqualTo("Demon Ash x5"));
            Assert.That(presenter.DurationButton.text, Is.EqualTo("For 12 h"));
            Assert.That(presenter.FeeText, Is.EqualTo("Listing fee 1 gold")); // 2 % of 60, rounded down (FeeRates listing)
            Assert.That(presenter.ListButton.enabledSelf, Is.True);
            rig.Binder.CycleDuration();
            Assert.That(presenter.DurationButton.text, Is.EqualTo("For 24 h"));

            rig.Binder.SelectTab(AuctionTab.Mine);
            Assert.That(presenter.MineCountText, Is.EqualTo("1 of 10 listings"));
            Assert.That(presenter.MineRowCount, Is.EqualTo(1));
            Assert.That(presenter.MineRowLabel(0), Does.StartWith("Demon Ash x4   99 gold each"));
            Assert.That(presenter.MineCancel(0).enabledSelf, Is.True);
        }

        [Test]
        public void A_drop_onto_the_open_window_chooses_the_stack_to_list()
        {
            var presenter = Presenter(out var rig);
            var stack = new InventorySlotEntry { Slot = 2, ItemNetId = Ash, Count = 3 };
            Assert.That(presenter.DropAt(new UnityEngine.Vector2(10, 10), 1, stack), Is.False); // closed
            Assert.That(rig.Binder.Drop(1, stack), Is.False);
            rig.Open();
            Assert.That(rig.Binder.Drop(1, stack), Is.True);
            Assert.That((rig.Model.Tab, rig.Model.SellItem, rig.Model.SellCount), Is.EqualTo((AuctionTab.Sell, stack, 3)));
        }

        // ---- #245 review

        [Test]
        public void A_notice_outlives_the_automatic_refresh_and_goes_with_the_players_next_search_or_action()
        {
            var rig = new Rig();
            rig.Open();
            rig.SearchAll(Entry(1, 15), Entry(2, 16));
            rig.Binder.SelectRow(1);
            rig.Binder.Buy();
            rig.Feed.RaiseListingAnswered(new ListingResult { RequestId = 2, Action = ListingAction.Buy, Reason = ListingReason.ListingSold });
            rig.Feed.RaiseListingPage(Page(3, false, Entry(2, 16))); // the binder's own refresh
            Assert.That(rig.Model.Notice, Is.EqualTo("ListingSold"));

            rig.Binder.Search(); // the player's own search
            Assert.That(rig.Model.Notice, Is.Empty);

            rig.Feed.RaiseListingPage(Page(4, false, Entry(2, 16)));
            rig.Binder.SelectRow(2);
            rig.Binder.Buy();
            rig.Feed.RaiseListingAnswered(new ListingResult { RequestId = 5, Action = ListingAction.Buy, Reason = ListingReason.ListingBusy });
            Assert.That(rig.Model.Notice, Is.EqualTo("ListingBusy"));
            rig.Binder.Buy(); // the next buy: the notice has been read
            Assert.That(rig.Model.Notice, Is.Empty);
        }

        /// <summary>What typing does: the field shows the text and its change event reaches the binder (no panel in EditMode).</summary>
        private static void Type(UnityEngine.UIElements.TextField field, System.Action<string> edit, string text)
        {
            field.SetValueWithoutNotify(text);
            edit(text);
        }

        [Test]
        public void The_buy_count_field_shows_the_models_count_after_a_selection_change_and_a_clamp()
        {
            var presenter = Presenter(out var rig);
            rig.Open();
            rig.SearchAll(Entry(1, 15, count: 4), Entry(2, 16, count: 4));
            rig.Binder.SelectRow(1);
            Type(presenter.BuyCountField, rig.Binder.EditBuyCount, "3");
            Assert.That((rig.Model.BuyCount, presenter.BuyCountField.value), Is.EqualTo((3, "3")));
            rig.Binder.SelectRow(2); // another row: its count starts at 1, and the field says so
            Assert.That((rig.Model.BuyCount, presenter.BuyCountField.value), Is.EqualTo((1, "1")));
            Type(presenter.BuyCountField, rig.Binder.EditBuyCount, "99"); // more than the row's 4 units
            Assert.That((rig.Model.BuyCount, presenter.BuyCountField.value, presenter.TotalText), Is.EqualTo((4, "4", "Total 64 gold")));
            rig.Binder.Buy();
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("buy 2 x4 at 16")); // what the field shows is what is sent
        }

        [Test]
        public void A_cancel_refused_as_sold_and_each_visit_to_my_listings_ask_for_the_own_listings_again()
        {
            var rig = new Rig();
            rig.Open();
            rig.Binder.SelectTab(AuctionTab.Mine);
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "mine" })); // asked each time the tab is chosen
            rig.Binder.SelectTab(AuctionTab.Mine);
            Assert.That(rig.Requests.Sent.Count, Is.EqualTo(1)); // one at a time
            rig.Feed.RaiseMyListings(new MyListings { RequestId = 1, Cap = 10, Rows = new[] { Entry(30, 99) } });
            rig.Binder.SelectTab(AuctionTab.Search);
            rig.Binder.SelectTab(AuctionTab.Mine);
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("mine"));
            rig.Feed.RaiseMyListings(new MyListings { RequestId = 2, Cap = 10, Rows = new[] { Entry(30, 99) } });

            rig.Binder.CancelListing(30);
            rig.Feed.RaiseListingAnswered(new ListingResult { RequestId = 3, Action = ListingAction.Cancel, Reason = ListingReason.ListingSold });
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("ListingSold"));
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("mine")); // the sold listing leaves My listings
        }

        [Test]
        public void An_answer_after_the_window_closed_still_toasts_and_asks_for_nothing()
        {
            var rig = new Rig();
            rig.Open();
            rig.SearchAll(Entry(1, 15, count: 4));
            rig.Binder.SelectRow(1);
            rig.Binder.Buy();
            rig.Binder.CloseAuction(); // or the auctioneer left reach
            Assert.That((rig.Model.Open, rig.Model.PendingAction), Is.EqualTo((false, 2u)));
            int sent = rig.Requests.Sent.Count;

            rig.Feed.RaiseListingAnswered(new ListingResult { RequestId = 2, Action = ListingAction.Buy, ListingId = 1, Count = 1, Price = 15 });
            Assert.That((rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((AuctionTextIds.ToastBought, "Demon Ash x1")));
            Assert.That((rig.Model.PendingAction, rig.Requests.Sent.Count), Is.EqualTo((0u, sent))); // no refresh while closed

            rig.Open();
            rig.Binder.SelectTab(AuctionTab.Sell);
            rig.Feed.RaiseInventory(new InventoryContents
            {
                Revision = 1, SlotCount = 8, Slots = new[] { new InventorySlotEntry { Slot = 3, ItemNetId = Ash, Count = 5 } },
            });
            rig.Binder.PickSell(rig.Model.SellableContainer, rig.Model.Sellable[0]);
            rig.Binder.EditSellPrice("15");
            rig.Binder.List();
            rig.Binder.CloseAuction();
            rig.Feed.RaiseListingAnswered(new ListingResult
            {
                RequestId = (uint)rig.Requests.Sent.Count, Action = ListingAction.Create, Reason = ListingReason.ListingCap,
            });
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("ListingCap")); // a refusal after the close is still shown
        }

        [Test]
        public void A_refused_or_unsent_page_leaves_the_cursor_stack_where_it_was()
        {
            var rig = new Rig();
            rig.Open();
            rig.Binder.Search();
            rig.Feed.RaiseListingPage(Page(1, true, Entry(1, 5), Entry(2, 7)));
            rig.Binder.Next();
            rig.Feed.RaiseListingPage(new ListingSearchResponse { RequestId = 2, Reason = ListingReason.ListingRateLimited });
            Assert.That((rig.Model.PageIndex, rig.Model.CanPrevious, rig.Toast.ReasonCode), Is.EqualTo((0, false, "ListingRateLimited")));

            rig.Requests.Offline = true;
            rig.Binder.Next(); // not sent
            Assert.That((rig.Model.PageIndex, rig.Model.PendingSearch), Is.EqualTo((0, 0u)));
            rig.Requests.Offline = false;

            rig.Binder.Next(); // the same cursor again, never pushed twice
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo($"search at {Auctioneer} after 7:2"));
            rig.Feed.RaiseListingPage(Page(3, false, Entry(3, 9)));
            Assert.That((rig.Model.PageIndex, rig.Model.CanPrevious), Is.EqualTo((1, true)));
            rig.Binder.Previous();
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo($"search at {Auctioneer} after 0:0"));
            rig.Feed.RaiseListingPage(Page(4, true, Entry(1, 5), Entry(2, 7)));
            Assert.That((rig.Model.PageIndex, rig.Model.CanPrevious), Is.EqualTo((0, false)));
        }

        [Test]
        public void A_name_that_matches_more_items_than_one_search_names_says_so_with_the_page()
        {
            var set = Tables();
            var config = new AuctionConfig(InventoryConfig.From(set), new ListingSettings { SearchItemIdsMax = 2 }, 2);
            for (int id = 1; id <= 3; id++)
                config.AddItem(new ItemDef { Id = "ash" + id, NetId = id, Category = ItemCategory.Material, NameId = "Ash " + id });
            var ids = config.MatchNames("ash", name => name, out bool tooMany);
            Assert.That(ids, Is.EqualTo(new ushort[] { 1, 2 }));
            Assert.That(tooMany, Is.True);
            config.MatchNames("ash 3", name => name, out tooMany);
            Assert.That(tooMany, Is.False);

            var presenter = Presenter(out var rig);
            rig.Open();
            rig.Model.SetName("ash");
            rig.Model.FirstPage(new ushort[] { 1, 2 }, 0, 0, 0, true, out _);
            rig.Model.SearchSent(1);
            rig.Model.OnPage(Page(1, false));
            Assert.That(presenter.TooManyText, Is.EqualTo(
                $"Many items have a name like that: only the first {rig.Config.SearchItemIdsMax} were searched. Type more of the name."));
            Assert.That(presenter.EmptyShown, Is.True);
        }

        [Test]
        public void Below_the_auction_level_the_auctioneers_yes_toasts_the_level_and_opens_nothing()
        {
            var rig = new Rig();
            bool unlocked = false;
            rig.Binder.Unlocked = () => unlocked; // WP-M4-11 (M4 decision 14; the contract board's pattern)
            rig.Binder.UnlockLevel = 22;
            rig.Open();
            Assert.That((rig.Model.Open, rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((false, AuctionTextIds.LevelLocked, "22")));
            unlocked = true;
            rig.Open();
            Assert.That(rig.Model.Open, Is.True);
        }

        [Test]
        public void A_trade_keeps_the_auction_window_closed_and_closes_an_open_one_like_the_mail_window()
        {
            var rig = new Rig();
            bool trading = true;
            rig.Binder.TradeOpen = () => trading; // user decision 2026-11-14 ("like mail")
            rig.Open();
            Assert.That(rig.Model.Open, Is.False); // the auctioneer's yes opens nothing during a trade
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo(nameof(ClientReasonCode.WindowBlockedByTrade))); // WP-M4-11: and says why

            trading = false;
            rig.Open();
            Assert.That(rig.Model.Open, Is.True);
            rig.Binder.FocusField(true);
            trading = true; // a trade opens while the auction window is open
            rig.Binder.Frame(1);
            Assert.That((rig.Model.Open, rig.Feed.TextInputActive), Is.EqualTo((false, false)));
        }

        [Test]
        public void A_refused_first_page_keeps_the_shown_pages_filters_for_next_and_previous()
        {
            var rig = new Rig();
            rig.Open();
            rig.Binder.EditName("demon ash");
            rig.Binder.Search();
            rig.Feed.RaiseListingPage(Page(1, true, Entry(1, 5), Entry(2, 7)));
            Assert.That(rig.Binder.Filters.ItemNetIds, Does.Contain(Ash));

            rig.Binder.EditName("");
            rig.Binder.CycleCategory(); // Materials
            rig.Binder.Search();
            rig.Feed.RaiseListingPage(new ListingSearchResponse { RequestId = 2, Reason = ListingReason.ListingSearchFailed });
            Assert.That(rig.Binder.Filters.Category, Is.EqualTo((byte)0)); // the shown page's, not the refused search's
            Assert.That(rig.Binder.Filters.ItemNetIds, Does.Contain(Ash));

            rig.Binder.Next(); // the shown page's filters with its own cursor
            var next = rig.Requests.Searches.Last();
            Assert.That((next.Category, next.AfterUnitPrice, next.AfterListingId), Is.EqualTo(((byte)0, 7L, 2L)));
            Assert.That(next.ItemNetIds, Does.Contain(Ash));

            rig.Feed.RaiseListingPage(Page(3, false, Entry(3, 9)));
            rig.Model.FirstPage(new ushort[] { 1, 2 }, 0, 0, 0, true, out _); // too many matches: shown only with its page
            rig.Model.SearchSent(4);
            Assert.That(rig.Model.TooManyMatches, Is.False);
            rig.Model.OnPage(new ListingSearchResponse { RequestId = 4, Reason = ListingReason.ListingRateLimited });
            Assert.That(rig.Model.TooManyMatches, Is.False);
        }

        [Test]
        public void A_focused_count_field_shows_the_kept_count_when_the_typed_number_is_above_the_units()
        {
            var presenter = Presenter(out var rig);
            rig.Open();
            rig.SearchAll(Entry(1, 15, count: 4));
            rig.Binder.SelectRow(1);
            presenter.CountFocus(presenter.BuyCountField, true); // typing: Buy takes no focus
            Type(presenter.BuyCountField, rig.Binder.EditBuyCount, "");
            Assert.That(presenter.BuyCountField.value, Is.Empty); // empty text is left as typed
            Type(presenter.BuyCountField, rig.Binder.EditBuyCount, "2");
            Assert.That(presenter.BuyCountField.value, Is.EqualTo("2"));
            Type(presenter.BuyCountField, rig.Binder.EditBuyCount, "99");
            Assert.That((rig.Model.BuyCount, presenter.BuyCountField.value), Is.EqualTo((4, "4")));
            rig.Binder.Buy();
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("buy 1 x4 at 15"));

            rig.Feed.RaiseInventory(new InventoryContents
            {
                Revision = 1, SlotCount = 8, Slots = new[] { new InventorySlotEntry { Slot = 3, ItemNetId = Ash, Count = 5 } },
            });
            rig.Binder.PickSell(rig.Model.SellableContainer, rig.Model.Sellable[0]);
            presenter.CountFocus(presenter.SellCountField, true);
            Type(presenter.SellCountField, rig.Binder.EditSellCount, "50");
            Assert.That((rig.Model.SellCount, presenter.SellCountField.value), Is.EqualTo((5, "5")));
            Type(presenter.SellCountField, rig.Binder.EditSellCount, "");
            Assert.That(presenter.SellCountField.value, Is.Empty);
            presenter.CountFocus(presenter.SellCountField, false);
        }

        // ---- data

        [Test]
        public void Every_text_reason_category_and_the_layout_row_is_committed()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            var reasons = new HashSet<string>(set.TryGet(typeof(ReasonCodeStrings), out var codes) ? codes.Rows.Cast<ReasonCodeStrings>().Select(r => r.Code) : new string[0]);

            Assert.That(AuctionTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(AuctionConfig.Categories.Select(AuctionTextIds.CategoryOf).Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(System.Enum.GetNames(typeof(ListingReason)).Where(n => n != "None" && !reasons.Contains(n)), Is.Empty);
            Assert.That(ui.Contains(OutpostTextIds.PromptOf(NpcKind.Auctioneer)), Is.True);

            var config = AuctionConfig.From(set);
            Assert.That(config.IsAuctioneer(AuctioneerNetId), Is.True);
            Assert.That(config.DurationsMs.Count, Is.EqualTo(3));
            Assert.That((config.PageSize, config.ListingFeePercent), Is.EqualTo((20, 2)));
            Assert.That(config.RarityTiers, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(HudLayoutRow(set, HudElementIds.AuctionWindow), Is.True);
        }

        private static bool HudLayoutRow(ConfigSet set, string id) =>
            set.TryGet(typeof(HudLayout), out var layout) && layout.Rows.Cast<HudLayout>().Any(r => r.Id == id);
    }
}
