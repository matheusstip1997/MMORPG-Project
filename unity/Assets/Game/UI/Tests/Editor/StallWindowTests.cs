#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Stall;
using Game.UI.ViewModels.Hud;
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
    /// The stall window and prompt (spec 4.5, 9.13; M4 decision 7; WP-M4-8): the interaction key at a free place opens the window
    /// to open a stall with one stack (the user answer of 2026-11-13), at a stall asks to look at it; the prompt's text per case;
    /// Buy and Cancel name the listing; one request at a time; the window closes when its stall leaves reach or closes; refusals
    /// toast the listing model's reason; the presenter's rows and the owner's part; and the committed texts and layout rows.
    /// </summary>
    public sealed class StallWindowTests
    {
        private const ushort Ash = 2; // ItemDef m1.demonAsh
        private const uint Stall = 40;

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private sealed class FakeStallRequests : IStallRequests
        {
            public List<string> Sent { get; } = new List<string>();

            public uint OpenStall(int place, byte container, byte slot, int count, long unitPrice) =>
                Add($"open {place} {container}:{slot} x{count} at {unitPrice}");

            public uint AddToStall(uint stallEntityId, byte container, byte slot, int count, long unitPrice) =>
                Add($"add {stallEntityId} {container}:{slot} x{count} at {unitPrice}");

            public uint CancelStallListing(uint stallEntityId, long listingId) => Add($"cancel {stallEntityId} {listingId}");

            public uint CloseStall(uint stallEntityId) => Add($"close {stallEntityId}");

            public uint BrowseStall(uint stallEntityId) => Add($"browse {stallEntityId}");

            public uint LeaveStall(uint stallEntityId) => Add($"leave {stallEntityId}");

            public uint BuyFromStall(uint stallEntityId, long listingId, int count, long unitPrice) =>
                Add($"buy {stallEntityId} {listingId} x{count} at {unitPrice}");

            private uint Add(string what)
            {
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

        private static StallState State(bool own = false, bool open = true, params ListingEntry[] rows) => new StallState
        {
            StallEntityId = Stall, Place = 3, Own = own, Open = open, ClosesUtcMs = 5 * 3_600_000L, Cap = 8, Rows = rows,
        };

        private static ListingEntry Row(long id, int count, long price) => new ListingEntry
        {
            ListingId = id, UnitPrice = price, ExpiresUtcMs = 0, Item = new InventorySlotEntry { ItemNetId = Ash, Count = (ushort)count },
        };

        private sealed class Rig
        {
            public Rig()
            {
                var set = Tables();
                Hud = HudConfig.From(set);
                Config = StallConfig.From(set);
                Feed = StallWindowTests.Feed(set);
                Feed.StallRequests = Requests;
                Feed.Stalls = new ClientStalls();
                Binder = new StallBinder(Config, Hud.Text, Model, Prompt, Toast, 2_000, () => 0, readInteractKey: false);
                Binder.Bind(Feed);
            }

            public HudConfig Hud { get; }
            public StallConfig Config { get; }
            public WorldClientFeed Feed { get; }
            public FakeStallRequests Requests { get; } = new FakeStallRequests();
            public StallWindowViewModel Model { get; } = new StallWindowViewModel();
            public NpcPromptViewModel Prompt { get; } = new NpcPromptViewModel();
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public StallBinder Binder { get; }
        }

        [Test]
        public void At_a_free_place_the_key_opens_the_window_and_one_stack_opens_the_stall()
        {
            var rig = new Rig();
            rig.Feed.SetStallTarget(0, 3, ownEntityId: 9);
            rig.Binder.Frame(0);
            Assert.That((rig.Prompt.NpcEntityId, rig.Prompt.TextId), Is.EqualTo((9u, StallTextIds.PromptOpen))); // over the own character

            rig.Binder.Interact();
            Assert.That((rig.Model.Open, rig.Model.Mode, rig.Model.Place), Is.EqualTo((true, StallWindowMode.Open, 3)));
            rig.Model.SetChoices(new[] { new StallChoice(1, 4, Ash, 10) });
            rig.Binder.SelectChoice(0);
            Assert.That(rig.Model.CountText, Is.EqualTo("10")); // the whole stack by default
            rig.Binder.EditCount("2");
            rig.Binder.EditPrice("5");
            rig.Binder.List();
            rig.Binder.List(); // one request at a time

            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "open 3 1:4 x2 at 5" }));
            rig.Binder.Frame(16);
            Assert.That(rig.Prompt.Visible, Is.False); // no prompt while the window is open
        }

        [Test]
        public void At_a_stall_the_key_looks_at_it_and_buy_takes_every_unit_at_the_price_seen()
        {
            var rig = new Rig();
            rig.Feed.SetStallTarget(Stall, -1, ownEntityId: 9);
            rig.Binder.Frame(0);
            Assert.That((rig.Prompt.NpcEntityId, rig.Prompt.TextId), Is.EqualTo((Stall, StallTextIds.PromptBrowse)));

            rig.Binder.Interact();
            rig.Feed.RaiseStallState(State(rows: Row(7, 3, 20)));
            Assert.That((rig.Model.Mode, rig.Model.Rows.Count), Is.EqualTo((StallWindowMode.Browse, 1)));
            rig.Binder.Buy(7);
            rig.Binder.Buy(7);
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "browse 40", "buy 40 7 x3 at 20" }));

            rig.Feed.RaiseStallAnswered(new StallResult
            {
                RequestId = 2, Action = StallAction.Buy, Reason = StallReason.StallListingRefused, ListingReason = ListingReason.NotEnoughGold,
            });
            Assert.That((rig.Toast.ReasonCode, rig.Model.Pending), Is.EqualTo(("NotEnoughGold", 0u))); // the listing model's reason
        }

        [Test]
        public void The_window_closes_when_its_stall_leaves_reach_or_closes()
        {
            var rig = new Rig();
            rig.Feed.SetStallTarget(Stall, -1, 9);
            rig.Binder.Interact();
            rig.Feed.SetStallTarget(0, -1, 9);
            rig.Binder.Frame(16);
            Assert.That(rig.Model.Open, Is.False);
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("leave 40"));

            rig.Feed.SetStallTarget(Stall, -1, 9);
            rig.Binder.Interact();
            rig.Feed.RaiseStallState(State(open: false));
            Assert.That((rig.Model.Open, rig.Toast.TextId), Is.EqualTo((false, StallTextIds.Closed)));
        }

        [Test]
        public void The_owners_state_turns_the_window_to_manage_with_cancel_and_close()
        {
            var rig = new Rig();
            rig.Feed.SetStallTarget(Stall, -1, 9);
            rig.Binder.Interact();
            rig.Feed.RaiseStallState(State(own: true, rows: Row(7, 3, 20)));
            Assert.That(rig.Model.Mode, Is.EqualTo(StallWindowMode.Manage));
            rig.Binder.CancelListing(7);
            rig.Feed.RaiseStallAnswered(new StallResult { RequestId = 2, Action = StallAction.Cancel, ListingId = 7 });
            rig.Binder.CloseStall();
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "browse 40", "cancel 40 7", "close 40" }));
            rig.Feed.RaiseStallAnswered(new StallResult { RequestId = 3, Action = StallAction.Close });
            Assert.That(rig.Model.Open, Is.False);
        }

        [Test]
        public void The_presenter_lists_the_rows_with_buy_and_shows_the_owners_part_only_to_the_owner()
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var stall = StallConfig.From(set);
            var model = new StallWindowViewModel();
            var binder = new StallBinder(stall, hud.Text, model, new NpcPromptViewModel(), new ToastViewModel(), 1500, () => 0, readInteractKey: false);
            var presenter = new StallWindowPresenter(hud, new HudView(hud), stall, model, binder, () => 0);
            presenter.Bind();

            model.ShowStall(Stall, "Mira", own: false, cap: 8);
            model.SetState(State(rows: Row(7, 3, 20)));
            Assert.That(presenter.TitleText, Is.EqualTo("Stall of Mira"));
            Assert.That(presenter.RowCount, Is.EqualTo(1));
            Assert.That(presenter.RowText(0), Is.EqualTo("Demon Ash x3   20 gold each"));
            Assert.That((presenter.RowButton(0).text, presenter.ListingShown), Is.EqualTo(("Buy", false)));

            model.ShowPlace(3, 8);
            model.SetChoices(new[] { new StallChoice(1, 0, Ash, 10) });
            Assert.That((presenter.ListingShown, presenter.ChoiceCount, presenter.SubmitButton.text), Is.EqualTo((true, 1, "Open stall")));
            Assert.That(presenter.SubmitButton.enabledSelf, Is.False); // nothing picked yet
        }

        private const ushort Sword = 701; // ItemDef m3.forgedSword
        private const ushort Horn = 1; // ItemDef m1.demonHorn (sell-only)

        private static ListingEntry SwordRow(long id, byte enchant, byte rarity, ushort quality) => new ListingEntry
        {
            ListingId = id, UnitPrice = 50, ExpiresUtcMs = 0,
            Item = new InventorySlotEntry { ItemNetId = Sword, Count = 1, Enchant = enchant, Rarity = rarity, Quality = quality },
        };

        [Test]
        public void A_row_and_a_pick_show_the_enchant_level_the_rarity_and_the_quality()
        {
            // #246 review: a +7 sword of quality 80 and a +0 of quality 20 never read the same (the auction row's text, shared).
            var rig = new Rig();
            var presenter = new StallWindowPresenter(rig.Hud, new HudView(rig.Hud), rig.Config, rig.Model, rig.Binder, () => 0);
            presenter.Bind();
            rig.Model.ShowStall(Stall, "Mira", own: false, cap: 8);
            rig.Model.SetState(State(rows: new[] { SwordRow(8, 7, 2, 80), SwordRow(9, 0, 0, 20) }));

            Assert.That(presenter.RowText(0), Is.EqualTo("Forged Sword +7 x1 (Rare, Quality 80)   50 gold each"));
            Assert.That(presenter.RowText(1), Is.EqualTo("Forged Sword x1 (Quality 20)   50 gold each"));

            rig.Model.ShowPlace(3, 8);
            var entry = new InventorySlotEntry { Slot = 2, ItemNetId = Sword, Count = 1, Enchant = 7, Rarity = 2, Quality = 80 };
            rig.Model.SetChoices(new[] { new StallChoice(1, 2, Sword, 1, entry), new StallChoice(1, 0, Ash, 10) });
            Assert.That(presenter.Choice(0).text, Is.EqualTo("Forged Sword +7 x1 (Rare, Quality 80)"));
            Assert.That(presenter.Choice(1).text, Is.EqualTo("Demon Ash x10")); // from the stall.itemCount text, not built in code
        }

        [Test]
        public void The_picker_offers_only_stacks_the_listing_model_takes()
        {
            // #246 review: the auction's filter (shared): never a reserved, stolen, non-tradable, tainted or sell-only stack.
            var rig = new Rig();
            rig.Feed.RaiseInventory(new InventoryContents
            {
                Revision = 1, SlotCount = 8, Slots = new[]
                {
                    new InventorySlotEntry { Slot = 0, ItemNetId = Ash, Count = 10 },
                    new InventorySlotEntry { Slot = 1, ItemNetId = Horn, Count = 5 },
                    new InventorySlotEntry { Slot = 2, ItemNetId = Ash, Count = 3, Stolen = true },
                    new InventorySlotEntry { Slot = 3, ItemNetId = Ash, Count = 3, NonTradable = true },
                    new InventorySlotEntry { Slot = 4, ItemNetId = Ash, Count = 3, Tainted = true },
                    new InventorySlotEntry { Slot = 5, ItemNetId = Ash, Count = 3, Reserved = true },
                    new InventorySlotEntry { Slot = 6, ItemNetId = Sword, Count = 1, Enchant = 2 },
                },
            });
            rig.Feed.SetStallTarget(0, 3, ownEntityId: 9);
            rig.Binder.Interact();

            Assert.That(rig.Model.Choices.Select(c => c.Slot), Is.EqualTo(new byte[] { 0, 6 }));
            Assert.That(rig.Model.Choices[1].Entry!.Enchant, Is.EqualTo(2)); // the entry rides along for the picker's text
        }

        [Test]
        public void An_applied_open_clears_the_count_in_a_focused_field_too_so_add_works_without_retyping()
        {
            // #246 review: the buttons never take the focus, so the count field kept "5" while the model was empty.
            var rig = new Rig();
            var presenter = new StallWindowPresenter(rig.Hud, new HudView(rig.Hud), rig.Config, rig.Model, rig.Binder, () => 0);
            presenter.Bind();
            rig.Feed.RaiseInventory(new InventoryContents
            {
                Revision = 1, SlotCount = 8, Slots = new[] { new InventorySlotEntry { Slot = 0, ItemNetId = Ash, Count = 10 } },
            });
            rig.Feed.SetStallTarget(0, 3, ownEntityId: 9);
            rig.Binder.Interact();
            rig.Binder.SelectChoice(0);
            Assert.That(presenter.CountField.value, Is.EqualTo("10")); // the picked stack's count, set by the window

            presenter.FieldFocus(presenter.CountField, true);
            presenter.CountField.SetValueWithoutNotify("5"); // typed
            rig.Binder.EditCount("5");
            presenter.FieldFocus(presenter.PriceField, true);
            presenter.PriceField.SetValueWithoutNotify("7");
            rig.Binder.EditPrice("7");
            presenter.FieldFocus(presenter.PriceField, false);
            presenter.FieldFocus(presenter.CountField, true); // the count field has the focus again; List is a click
            rig.Binder.List();
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { "open 3 1:0 x5 at 7" }));

            rig.Feed.RaiseStallAnswered(new StallResult { RequestId = 1, Action = StallAction.Open, Count = 5 });
            Assert.That((rig.Model.CountText, presenter.CountField.value), Is.EqualTo(("", ""))); // field and model agree
            Assert.That(presenter.PriceField.value, Is.EqualTo("7")); // the price stays for the next listing

            rig.Binder.SelectChoice(0);
            Assert.That(presenter.CountField.value, Is.EqualTo("10")); // set by the window: written even though the field has focus
            Assert.That(presenter.SubmitButton.enabledSelf, Is.True);
        }

        [Test]
        public void Committed_ui_strings_cover_every_stall_text_and_reason_and_the_layout_has_its_rows()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            var reasons = new HashSet<string>(set.TryGet(typeof(ReasonCodeStrings), out var codes) ? codes.Rows.Cast<ReasonCodeStrings>().Select(r => r.Code) : new string[0]);
            Assert.That(StallTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(System.Enum.GetNames(typeof(StallReason)).Where(n => n != "None" && !reasons.Contains(n)), Is.Empty);
            var hud = HudConfig.From(set);
            Assert.That(hud.TryGetLayout(HudElementIds.StallWindow, out _) && hud.TryGetLayout(HudElementIds.StallPrompt, out _), Is.True);
            Assert.That(StallSettings.Find(set)!.VisualId, Is.EqualTo("stall.default"));
        }
    }
}
