#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Trade;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Trade;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.UI.Tests
{
    /// <summary>
    /// The trade window and its invite prompt (spec 4.5 "M4 trade window", 9.13; WP-M4-4): the server's states open and close
    /// them; accept, decline, cancel, offers, gold, lock and confirm go out as requests (lock and confirm once allowed); refusals
    /// and the end of a trade toast; the countdowns tick; a drop on the own column offers the stack and anything else falls
    /// through; the committed texts.
    /// </summary>
    public sealed class TradeWindowTests
    {
        private const byte Normal = 1;

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static ushort Item(ConfigSet set, string id) =>
            set.TryGet(typeof(ItemDef), out var table) ? (ushort)table.Rows.Cast<ItemDef>().Single(i => i.Id == id).NetId : (ushort)0;

        private sealed class FakeTradeRequests : ITradeRequests
        {
            public List<string> Sent { get; } = new List<string>();

            public uint RequestTrade(TradeAction action, uint targetEntityId = 0, byte container = 0, ushort slot = 0, ushort count = 0,
                byte offerIndex = 0, long gold = 0)
            {
                Sent.Add($"{action} {container}:{slot} x{count} #{offerIndex} {gold}g");
                return (uint)Sent.Count;
            }
        }

        private sealed class Rig
        {
            public Rig()
            {
                Set = Tables();
                Hud = HudConfig.From(Set);
                Inventory = InventoryConfig.From(Set);
                var settings = PredictionSettings.From(Set);
                Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
                var catalog = ActionCatalog.From(Set);
                Feed = new WorldClientFeed(Set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                    null, null, settings.TickMs) { TradeRequests = Requests };
                Binder = new TradeBinder(Window, Invite, Toast, 2_000, () => Now)
                {
                    InventoryOpen = () => InventoryShown,
                    SetInventoryOpen = open => InventoryShown = open,
                };
                Binder.Bind(Feed);
            }

            public ConfigSet Set { get; }
            public HudConfig Hud { get; }
            public InventoryConfig Inventory { get; }
            public WorldClientFeed Feed { get; }
            public FakeTradeRequests Requests { get; } = new FakeTradeRequests();
            public TradeWindowViewModel Window { get; } = new TradeWindowViewModel();
            public TradeInviteViewModel Invite { get; } = new TradeInviteViewModel();
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public TradeBinder Binder { get; }
            public double Now { get; set; }
            public bool InventoryShown { get; set; }

            public void State(TradePhase phase, uint revision = 1, int msLeft = 0, bool ownLocked = false, bool partnerLocked = false,
                long ownGold = 0, long partnerGold = 0, TradeReason reason = TradeReason.None, params TradeOffer[] own) =>
                Feed.RaiseTradeChanged(new TradeState
                {
                    TradeId = 1, Revision = revision, Phase = phase, PartnerEntityId = 9, PartnerName = "Mira", OwnOffer = own,
                    PartnerOffer = new[] { new TradeOffer { ItemNetId = Item(Set, "m2.trainingSword"), Count = 1, Enchant = 3 } },
                    OwnGold = ownGold, PartnerGold = partnerGold, OwnLocked = ownLocked, PartnerLocked = partnerLocked,
                    MsLeft = msLeft, Reason = reason,
                });

            public TradeOffer Ash(ushort count) => new TradeOffer { ItemNetId = Item(Set, "m1.demonAsh"), Count = count, Container = Normal, Slot = 2 };

            public InventorySlotEntry Entry(byte slot, string item, ushort count, long instance = 0) =>
                new InventorySlotEntry { Slot = slot, ItemNetId = Item(Set, item), Count = count, InstanceId = instance };
        }

        [Test]
        public void An_invite_shows_the_prompt_with_its_countdown_and_accept_sends_accept()
        {
            var rig = new Rig();
            rig.State(TradePhase.Invited, msLeft: 30_000);
            Assert.That((rig.Invite.Visible, rig.Invite.Incoming, rig.Invite.SecondsLeft, rig.Window.Open), Is.EqualTo((true, true, 30, false)));
            rig.Now = 10_500;
            rig.Binder.Frame(rig.Now);
            Assert.That(rig.Invite.SecondsLeft, Is.EqualTo(20));

            rig.Binder.Accept();
            Assert.That(rig.Invite.Visible, Is.False); // hidden at once; the server's next state opens the window
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "Accept 0:0 x0 #0 0g" }));
        }

        [Test]
        public void An_own_invite_shows_waiting_with_cancel_and_a_decline_ends_it_with_a_toast()
        {
            var rig = new Rig();
            rig.State(TradePhase.Inviting, msLeft: 30_000);
            Assert.That((rig.Invite.Visible, rig.Invite.Incoming), Is.EqualTo((true, false)));

            rig.State(TradePhase.Cancelled, reason: TradeReason.TradeDeclined);
            Assert.That(rig.Invite.Visible, Is.False);
            Assert.That((rig.Toast.Visible, rig.Toast.ReasonCode), Is.EqualTo((true, "TradeDeclined")));
        }

        [Test]
        public void The_window_opens_with_the_inventory_and_sends_offers_gold_lock_and_confirm()
        {
            var rig = new Rig();
            rig.State(TradePhase.Open, msLeft: 300_000);
            Assert.That((rig.Window.Open, rig.InventoryShown, rig.Window.CanLock, rig.Window.CanConfirm), Is.EqualTo((true, true, true, false)));

            rig.Binder.DropOffer(Normal, rig.Entry(2, "m1.demonAsh", 10));
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("Add 1:2 x10 #0 0g"));
            Assert.That(rig.Window.CanEdit, Is.False); // one request at a time
            rig.Feed.RaiseTradeAnswered(new TradeResult { RequestId = 1, Action = TradeAction.Add });
            rig.State(TradePhase.Open, revision: 2, own: rig.Ash(10));
            rig.Binder.DropOffer(Normal, rig.Entry(0, "m2.trainingSword", 1, instance: 77));
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("Add 1:0 x1 #0 0g"));
            rig.Feed.RaiseTradeAnswered(new TradeResult { RequestId = 2, Action = TradeAction.Add });

            rig.Binder.SetGold("abc"); // not a number: nothing sent
            rig.Binder.SetGold("25");
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("SetGold 0:0 x0 #0 25g"));
            rig.Feed.RaiseTradeAnswered(new TradeResult { RequestId = 3, Action = TradeAction.SetGold });
            rig.Binder.RemoveOffer(0);
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("Remove 0:0 x0 #0 0g"));
            rig.Feed.RaiseTradeAnswered(new TradeResult { RequestId = 4, Action = TradeAction.Remove });

            rig.Binder.Confirm(); // not both locked: nothing sent
            Assert.That(rig.Requests.Sent.Count, Is.EqualTo(4));
            rig.Binder.Lock();
            Assert.That(rig.Requests.Sent.Last(), Does.StartWith("Lock"));
            rig.Feed.RaiseTradeAnswered(new TradeResult { RequestId = 5, Action = TradeAction.Lock });
            rig.State(TradePhase.Open, revision: 3, msLeft: 60_000, ownLocked: true, partnerLocked: true);
            Assert.That((rig.Window.CanLock, rig.Window.CanConfirm, rig.Window.SecondsLeft), Is.EqualTo((false, true, 60)));
            rig.Binder.Confirm();
            Assert.That(rig.Requests.Sent.Last(), Does.StartWith("Confirm"));
        }

        [Test]
        public void A_refusal_toasts_its_reason_and_frees_the_window()
        {
            var rig = new Rig();
            rig.State(TradePhase.Open);
            rig.Binder.DropOffer(Normal, rig.Entry(1, "m1.demonHorn", 5));
            rig.Feed.RaiseTradeAnswered(new TradeResult { RequestId = 1, Action = TradeAction.Add, Reason = TradeReason.TradeSellOnly });
            Assert.That((rig.Toast.Visible, rig.Toast.ReasonCode), Is.EqualTo((true, "TradeSellOnly")));
            Assert.That(rig.Window.CanEdit, Is.True);
        }

        [Test]
        public void Saving_freezes_the_window_and_done_closes_it_with_a_toast()
        {
            var rig = new Rig();
            rig.State(TradePhase.Settling, ownLocked: true, partnerLocked: true);
            Assert.That((rig.Window.Open, rig.Window.Saving, rig.Window.CanEdit, rig.Window.CanCancel), Is.EqualTo((true, true, false, false)));

            rig.State(TradePhase.Done, ownLocked: true, partnerLocked: true);
            Assert.That(rig.Window.Open, Is.False);
            Assert.That((rig.Toast.Visible, rig.Toast.TextId), Is.EqualTo((true, TradeTextIds.ToastDone)));
        }

        [Test]
        public void A_disconnect_clears_the_window_and_the_prompt()
        {
            var rig = new Rig();
            rig.State(TradePhase.Open);
            rig.Feed.RaiseDisconnected("Timeout");
            Assert.That((rig.Window.Open, rig.Invite.Visible), Is.EqualTo((false, false)));
        }

        [Test]
        public void The_presenters_show_both_offers_the_gold_and_the_buttons()
        {
            var rig = new Rig();
            var view = new HudView(rig.Hud);
            var window = new TradeWindowPresenter(rig.Hud, view, rig.Inventory, rig.Window, rig.Binder);
            var invite = new TradeInvitePresenter(rig.Hud, view, rig.Invite, rig.Binder);
            window.Bind();
            invite.Bind();
            Assert.That((window.IsShown, invite.IsShown), Is.EqualTo((false, false)));

            rig.State(TradePhase.Invited, msLeft: 30_000);
            Assert.That(invite.IsShown, Is.True);
            Assert.That(invite.Text, Is.EqualTo("Mira wants to trade (30 s)"));

            rig.State(TradePhase.Open, partnerGold: 40, own: rig.Ash(4));
            Assert.That((window.IsShown, invite.IsShown), Is.EqualTo((true, false)));
            Assert.That(window.Title, Is.EqualTo("Trade with Mira"));
            Assert.That(window.OwnRow(0).text, Does.EndWith("x4"));
            Assert.That(window.PartnerRow(0).text, Does.Contain("+3"));
            Assert.That(window.PartnerGoldText, Is.EqualTo("Gold: 40"));
            Assert.That((window.LockButton.enabledSelf, window.ConfirmButton.enabledSelf), Is.EqualTo((true, false)));

            rig.State(TradePhase.Open, revision: 2, msLeft: 60_000, ownLocked: true, partnerLocked: true, own: rig.Ash(4));
            Assert.That(window.ConfirmButton.text, Is.EqualTo("Confirm (60 s)"));
            Assert.That((window.OwnStateText, window.PartnerStateText), Is.EqualTo(("Locked", "Locked")));
            rig.State(TradePhase.Settling, revision: 2, ownLocked: true, partnerLocked: true, own: rig.Ash(4));
            Assert.That(window.SavingShown, Is.True);
        }

        [UnityTest]
        public System.Collections.IEnumerator A_drop_on_the_own_column_offers_the_stack_and_elsewhere_falls_through()
        {
            var rig = new Rig();
            var panel = new HudTestPanel("trade-layout-test");
            TradeWindowPresenter? presenter = null;
            try
            {
                yield return null;
                panel.Ready(rig.Hud);
                var view = new HudView(rig.Hud);
                panel.Root.Add(view.Root);
                presenter = new TradeWindowPresenter(rig.Hud, view, rig.Inventory, rig.Window, rig.Binder);
                presenter.Bind();
                rig.State(TradePhase.Open);
                panel.Layout();

                Assert.That(rig.Hud.TryGetLayout(HudElementIds.TradeWindow, out var row), Is.True);
                Assert.That((presenter.Panel.layout.width, presenter.Panel.layout.height), Is.EqualTo(((float)row.Size[0], (float)row.Size[1])));
                Assert.That(presenter.DropAt(presenter.OwnColumn.worldBound.center, Normal, rig.Entry(2, "m1.demonAsh", 6)), Is.True);
                Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("Add 1:2 x6 #0 0g"));
                var outside = new Vector2(presenter.Panel.worldBound.xMax + 50, presenter.Panel.worldBound.center.y);
                Assert.That(presenter.DropAt(outside, Normal, rig.Entry(2, "m1.demonAsh", 6)), Is.False);

                rig.State(TradePhase.Cancelled, reason: TradeReason.TradeCancelled);
                Assert.That(presenter.DropAt(presenter.OwnColumn.worldBound.center, Normal, rig.Entry(2, "m1.demonAsh", 6)), Is.False);
            }
            finally
            {
                presenter?.Dispose();
                panel.Dispose();
            }
        }

        [Test]
        public void Committed_ui_strings_and_layout_cover_every_trade_text_id_reason_and_element()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            var reasons = new HashSet<string>(set.TryGet(typeof(ReasonCodeStrings), out var codes) ? codes.Rows.Cast<ReasonCodeStrings>().Select(r => r.Code) : new string[0]);
            Assert.That(TradeTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(System.Enum.GetNames(typeof(TradeReason)).Where(n => n != "None" && !reasons.Contains(n)), Is.Empty);
            var hud = HudConfig.From(set);
            Assert.That(hud.TryGetLayout(HudElementIds.TradeWindow, out _) && hud.TryGetLayout(HudElementIds.TradeInvite, out _), Is.True);
        }
    }
}
