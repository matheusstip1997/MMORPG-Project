#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Auction;
using Game.UI.Menus.Mail;
using Game.UI.Menus.Stall;
using Game.UI.ViewModels.Auction;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Mail;
using Game.UI.ViewModels.Outpost;
using Game.UI.ViewModels.Stall;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.UI.Tests
{
    /// <summary>
    /// The left-edge windows open one at a time (user decision 2026-11-14, "Yes, one at a time"; spec 9.13): the mail, auction,
    /// stall and loot bag windows. Opening any of them closes the others through their own close path, a trade closes them all, and
    /// none of them opens while a trade is open. The group itself with four members, then the real binders on one feed, wired the
    /// way <see cref="HudRoot"/> wires them.
    /// </summary>
    public sealed class LeftWindowGroupTests
    {
        private const uint Auctioneer = 9;
        private const ushort AuctioneerNetId = 7; // NpcDef m1.auctioneer
        private const uint StallId = 40;
        private const uint Self = 3;

        private sealed class FakeWindow
        {
            public bool Open { get; set; }
            public int Closes { get; private set; }

            public void Close()
            {
                Open = false;
                Closes++;
            }
        }

        [Test]
        public void Each_of_four_windows_closes_the_other_three_when_it_opens()
        {
            var ids = new[] { HudElementIds.MailWindow, HudElementIds.AuctionWindow, HudElementIds.StallWindow, "loot_bag_window" };
            for (int opened = 0; opened < ids.Length; opened++)
            {
                // The other three open (more than the rule ever leaves: the worst case), joined as they are.
                var group = new WindowGroup();
                var windows = new FakeWindow[ids.Length];
                for (int i = 0; i < ids.Length; i++)
                {
                    var window = windows[i] = new FakeWindow { Open = i != opened };
                    group.Join(ids[i], () => window.Open, window.Close);
                }

                windows[opened].Open = true; // opens this frame
                group.Frame();

                for (int i = 0; i < ids.Length; i++)
                    Assert.That((windows[i].Open, windows[i].Closes), Is.EqualTo((i == opened, i == opened ? 0 : 1)), $"{ids[opened]} opened: {ids[i]}");
                Assert.That(group.OpenId(), Is.EqualTo(ids[opened]));
                group.Frame();
                Assert.That(group.OpenId(), Is.EqualTo(ids[opened])); // and it stays open
            }
        }

        [Test]
        public void A_trade_closes_all_four_and_blocks_each_until_it_ends()
        {
            bool trade = false;
            var group = new WindowGroup { Blocked = () => trade };
            var windows = new FakeWindow[4];
            for (int i = 0; i < windows.Length; i++)
            {
                var window = windows[i] = new FakeWindow { Open = i == 0 };
                group.Join("w" + i, () => window.Open, window.Close);
            }

            trade = true;
            group.Frame();
            Assert.That(group.OpenId(), Is.Null);
            Assert.That(group.IsBlocked, Is.True); // what each binder asks before it opens
            windows[2].Open = true; // a window that opened anyway is closed again
            group.Frame();
            Assert.That((windows[2].Open, windows[2].Closes), Is.EqualTo((false, 1)));

            trade = false;
            Assert.That(group.IsBlocked, Is.False);
            windows[1].Open = true;
            group.Frame();
            Assert.That(group.OpenId(), Is.EqualTo("w1"));
        }

        // ---- the real binders

        private sealed class FakeRequests : IMailRequests, IListingRequests, IStallRequests
        {
            public List<string> Sent { get; } = new List<string>();

            public uint RequestMailList() => Add("list");

            public uint OpenMail(long mailId) => Add("open mail " + mailId);

            public uint ClaimMail(long mailId, uint mailboxEntityId) => Add("claim " + mailId);

            public uint Search(uint npcEntityId, ListingSearchRequest filters) => Add("search");

            public uint CreateListing(uint npcEntityId, byte container, byte slot, int count, long unitPrice, int durationIndex) => Add("create");

            public uint BuyListing(uint npcEntityId, long listingId, int count, long unitPrice) => Add("buy listing");

            public uint CancelListing(uint npcEntityId, long listingId) => Add("cancel listing");

            public uint RequestMyListings(uint npcEntityId) => Add("mine");

            public uint OpenStall(int place, byte container, byte slot, int count, long unitPrice) => Add("open stall");

            public uint AddToStall(uint stallEntityId, byte container, byte slot, int count, long unitPrice) => Add("add");

            public uint CancelStallListing(uint stallEntityId, long listingId) => Add("cancel stall listing");

            public uint CloseStall(uint stallEntityId) => Add("close stall");

            public uint BrowseStall(uint stallEntityId) => Add("browse " + stallEntityId);

            public uint LeaveStall(uint stallEntityId) => Add("leave " + stallEntityId);

            public uint BuyFromStall(uint stallEntityId, long listingId, int count, long unitPrice) => Add("buy stall");

            private uint Add(string what)
            {
                Sent.Add(what);
                return (uint)Sent.Count;
            }
        }

        private sealed class Rig
        {
            public Rig()
            {
                Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
                var hud = HudConfig.From(set);
                var settings = PredictionSettings.From(set);
                Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out error), Is.True, error);
                var catalog = ActionCatalog.From(set);
                Feed = new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                    null, null, settings.TickMs);
                Feed.MailRequests = Requests;
                Feed.ListingRequests = Requests;
                Feed.StallRequests = Requests;
                Feed.Stalls = new ClientStalls();
                var toast = new ToastViewModel();
                Mail = new MailWindowBinder(MailConfig.From(set), hud.Text, MailModel, toast, 2_000, () => 0, readToggleKey: false);
                Auction = new AuctionBinder(AuctionConfig.From(set), hud.Text, AuctionModel, toast, 2_000, () => 0);
                Stall = new StallBinder(StallConfig.From(set), hud.Text, StallModel, new NpcPromptViewModel(), toast, 2_000, () => 0,
                    readInteractKey: false);
                Mail.Bind(Feed);
                Auction.Bind(Feed);
                Stall.Bind(Feed);
                // As HudRoot wires them.
                Group = new WindowGroup { Blocked = () => Trade };
                Mail.TradeOpen = () => Group.IsBlocked;
                Auction.TradeOpen = () => Group.IsBlocked;
                Stall.TradeOpen = () => Group.IsBlocked;
                Group.Join(HudElementIds.MailWindow, () => MailModel.Open, Mail.CloseMail);
                Group.Join(HudElementIds.AuctionWindow, () => AuctionModel.Open, Auction.CloseAuction);
                Group.Join(HudElementIds.StallWindow, () => StallModel.Open, Stall.CloseWindow);
            }

            public WorldClientFeed Feed { get; }
            public FakeRequests Requests { get; } = new FakeRequests();
            public MailWindowViewModel MailModel { get; } = new MailWindowViewModel();
            public AuctionWindowViewModel AuctionModel { get; } = new AuctionWindowViewModel();
            public StallWindowViewModel StallModel { get; } = new StallWindowViewModel();
            public MailWindowBinder Mail { get; }
            public AuctionBinder Auction { get; }
            public StallBinder Stall { get; }
            public WindowGroup Group { get; }
            public bool Trade { get; set; }

            /// <summary>The auctioneer in reach says yes.</summary>
            public void OpenAuction()
            {
                Feed.SetStallTarget(0, -1, Self);
                Feed.SetNpcTarget(Auctioneer, AuctioneerNetId);
                Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 1, NpcEntityId = Auctioneer, Kind = NpcInteraction.Auction });
            }

            /// <summary>The interaction key at the stall in reach (no NPC in reach).</summary>
            public void OpenStall()
            {
                Feed.SetNpcTarget(0, 0);
                Feed.SetStallTarget(StallId, -1, Self);
                Stall.Interact();
            }
        }

        [Test]
        public void Each_real_window_opening_closes_the_others_through_their_own_close()
        {
            var rig = new Rig();
            rig.OpenAuction();
            rig.Group.Frame();
            Assert.That(rig.Group.OpenId(), Is.EqualTo(HudElementIds.AuctionWindow));
            rig.Auction.FocusField(true); // a field of the auction window has the keys

            rig.Mail.Toggle();
            rig.Group.Frame();
            Assert.That((rig.MailModel.Open, rig.AuctionModel.Open), Is.EqualTo((true, false)));
            Assert.That(rig.Feed.WindowTextInput, Is.False); // the auction's own close gave the gameplay keys back

            rig.OpenStall();
            rig.Group.Frame();
            Assert.That((rig.StallModel.Open, rig.MailModel.Open), Is.EqualTo((true, false)));

            rig.OpenAuction();
            rig.Group.Frame();
            Assert.That((rig.AuctionModel.Open, rig.StallModel.Open), Is.EqualTo((true, false)));
            Assert.That(rig.Requests.Sent, Does.Contain("leave " + StallId)); // the stall's own close leaves the stall it looked at

            rig.OpenStall();
            rig.Group.Frame();
            Assert.That((rig.StallModel.Open, rig.AuctionModel.Open), Is.EqualTo((true, false)));
            rig.Mail.Toggle();
            rig.Group.Frame();
            Assert.That(rig.Group.OpenId(), Is.EqualTo(HudElementIds.MailWindow));
        }

        [Test]
        public void A_trade_closes_the_real_windows_and_none_opens_until_it_ends()
        {
            var rig = new Rig();
            rig.OpenStall();
            rig.Group.Frame();
            Assert.That(rig.StallModel.Open, Is.True);

            rig.Trade = true;
            rig.Group.Frame();
            Assert.That(rig.Group.OpenId(), Is.Null);
            Assert.That(rig.Requests.Sent, Does.Contain("leave " + StallId));

            int sent = rig.Requests.Sent.Count;
            rig.Mail.Toggle();
            rig.OpenAuction();
            rig.OpenStall();
            rig.Group.Frame();
            Assert.That((rig.MailModel.Open, rig.AuctionModel.Open, rig.StallModel.Open), Is.EqualTo((false, false, false)));
            Assert.That(rig.Requests.Sent.Count, Is.EqualTo(sent)); // the stall was not even looked at

            rig.Trade = false;
            rig.OpenStall();
            rig.Group.Frame();
            Assert.That(rig.Group.OpenId(), Is.EqualTo(HudElementIds.StallWindow));
        }
    }
}
