#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Book;
using Game.UI.ViewModels.Book;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// The Bystial book window (spec 14.2, 9.13 M2; WP-M2-8): the view model's collection, slots, selection and the one
    /// pending change; the binder's requests, refusal and news toasts; the presenter's slots, rows and bonus line; the
    /// layout on a real panel; and the committed texts and key.
    /// </summary>
    public sealed class BookWindowTests
    {
        private const ushort Demon = 1, Alien = 2, Cyclops = 3;

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static BookCard Card(ushort netId, BystialTier tier, long kills, long next, byte slot = BookLimits.NoSlot) =>
            new BookCard { MonsterNetId = netId, Tier = tier, Kills = kills, NextTierKills = next, Slot = slot };

        private static BookState State(int slots, BookCause cause, params BookCard[] cards) =>
            new BookState { Version = 1, Cause = cause, SlotCount = (byte)slots, Cards = cards };

        // ---- view model

        [Test]
        public void A_card_can_go_into_an_open_slot_once_earned_and_one_change_waits_at_a_time()
        {
            var model = new BookWindowViewModel();
            model.SetState(State(1, BookCause.Initial, Card(Demon, BystialTier.Normal, 100, 500), Card(Alien, BystialTier.None, 37, 100)));

            Assert.That(model.CanPlace(0), Is.False); // nothing selected
            model.Select(Alien);
            Assert.That(model.CanPlace(0), Is.False); // not earned
            model.Select(Demon);
            Assert.That((model.CanPlace(0), model.CanPlace(1)), Is.EqualTo((true, false))); // rank F: one slot
            model.SetPending(4);
            Assert.That(model.CanPlace(0), Is.False);
            Assert.That(model.OnResult(new BookChangeResult { RequestId = 3, Reason = BookReason.InCombat }), Is.Null); // not ours
            Assert.That(model.OnResult(new BookChangeResult { RequestId = 4, Reason = BookReason.InCombat }), Is.EqualTo("InCombat"));
            Assert.That((model.Pending, model.Selected), Is.EqualTo((0u, Demon))); // a refusal keeps the selection
        }

        [Test]
        public void A_placed_card_is_taken_out_and_an_occupied_slot_takes_another_card_as_a_swap()
        {
            var model = new BookWindowViewModel();
            model.SetState(State(1, BookCause.Initial, Card(Demon, BystialTier.Normal, 100, 500, slot: 0), Card(Alien, BystialTier.Normal, 100, 500)));

            Assert.That(model.InSlot(0)!.MonsterNetId, Is.EqualTo(Demon));
            model.Select(Demon);
            Assert.That((model.CanTakeOut, model.CanPlace(0)), Is.EqualTo((true, false)));
            model.Select(Alien);
            Assert.That((model.CanTakeOut, model.CanPlace(0)), Is.EqualTo((false, true)));
        }

        [Test]
        public void New_and_evolved_cards_are_news_but_the_first_state_is_not()
        {
            var model = new BookWindowViewModel();
            Assert.That(model.SetState(State(1, BookCause.Initial, Card(Demon, BystialTier.Normal, 499, 500))), Is.Empty);

            var news = model.SetState(State(1, BookCause.CardEarned, Card(Demon, BystialTier.Rare, 500, 2000), Card(Alien, BystialTier.Normal, 100, 500)))
                .Select(n => (n.MonsterNetId, n.Tier, n.Earned)).ToList();

            Assert.That(news, Is.EquivalentTo(new[] { (Demon, BystialTier.Rare, false), (Alien, BystialTier.Normal, true) }));
        }

        // ---- binder

        private sealed class FakeBookRequests : IBookRequests
        {
            public List<(ushort Monster, byte Slot)> Sent { get; } = new List<(ushort, byte)>();

            public uint RequestBookChange(ushort monsterNetId, byte slot)
            {
                Sent.Add((monsterNetId, slot));
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
                Config = BookConfig.From(set);
                Feed = Feed(set);
                Feed.BookRequests = Requests;
                Binder = new BookWindowBinder(Config, Hud.Text, Model, Toast, 2_000, () => 0, readToggleKey: false);
                Binder.Bind(Feed);
            }

            public HudConfig Hud { get; }
            public BookConfig Config { get; }
            public WorldClientFeed Feed { get; }
            public FakeBookRequests Requests { get; } = new FakeBookRequests();
            public BookWindowViewModel Model { get; } = new BookWindowViewModel();
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public BookWindowBinder Binder { get; }
        }

        [Test]
        public void Placing_sends_the_selected_card_and_slot_and_a_refusal_shows_its_reason()
        {
            var rig = new Rig();
            rig.Feed.RaiseBook(State(1, BookCause.Initial, Card(Demon, BystialTier.Normal, 100, 500)));

            rig.Binder.SelectCard(Demon);
            rig.Binder.PlaceInto(0);
            rig.Binder.PlaceInto(0); // one change at a time
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { (Demon, (byte)0) }));
            Assert.That(rig.Model.Pending, Is.EqualTo(1u));

            rig.Feed.RaiseBookChangeAnswered(new BookChangeResult { RequestId = 1, Reason = BookReason.InCombat });
            Assert.That((rig.Toast.Visible, rig.Toast.ReasonCode), Is.EqualTo((true, "InCombat")));
            Assert.That(rig.Model.Pending, Is.EqualTo(0u));
        }

        [Test]
        public void Taking_out_sends_the_no_slot_value()
        {
            var rig = new Rig();
            rig.Feed.RaiseBook(State(1, BookCause.Initial, Card(Demon, BystialTier.Normal, 100, 500, slot: 0)));
            rig.Binder.SelectCard(Demon);
            rig.Binder.TakeOut();

            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { (Demon, BookLimits.NoSlot) }));
        }

        [Test]
        public void A_new_card_shows_a_news_toast_with_its_name_and_tier()
        {
            var rig = new Rig();
            rig.Feed.RaiseBook(State(1, BookCause.Initial, Card(Demon, BystialTier.None, 99, 100)));
            rig.Feed.RaiseBook(State(1, BookCause.CardEarned, Card(Demon, BystialTier.Normal, 100, 500)));

            Assert.That((rig.Toast.Visible, rig.Toast.TextId, rig.Toast.ArgName), Is.EqualTo((true, BookTextIds.ToastEarned, "card")));
            Assert.That(rig.Toast.ArgValue, Is.EqualTo("Demon (Normal)"));
        }

        [Test]
        public void A_disconnect_clears_the_book()
        {
            var rig = new Rig();
            rig.Feed.RaiseBook(State(1, BookCause.Initial, Card(Demon, BystialTier.Normal, 100, 500)));
            rig.Model.SetOpen(true);
            rig.Feed.RaiseDisconnected("Disconnected");

            Assert.That((rig.Model.HasState, rig.Model.Open, rig.Model.Cards.Count), Is.EqualTo((false, false, 0)));
        }

        // ---- presenter

        private static BookWindowPresenter Presenter(out BookWindowViewModel model, out BookWindowBinder binder, out FakeBookRequests requests)
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var book = BookConfig.From(set);
            model = new BookWindowViewModel();
            binder = new BookWindowBinder(book, hud.Text, model, new ToastViewModel(), 1500, () => 0, readToggleKey: false);
            var feed = Feed(set);
            requests = new FakeBookRequests();
            feed.BookRequests = requests;
            binder.Bind(feed);
            var presenter = new BookWindowPresenter(hud, new HudView(hud), book, model, binder);
            presenter.Bind();
            return presenter;
        }

        [Test]
        public void The_window_starts_closed_and_shows_the_slots_the_rank_opens_and_one_row_per_monster()
        {
            var presenter = Presenter(out var model, out _, out _);
            Assert.That(presenter.IsShown, Is.False);

            model.SetState(State(2, BookCause.Initial, Card(Demon, BystialTier.Normal, 100, 500, slot: 1), Card(Alien, BystialTier.None, 37, 100),
                Card(Cyclops, BystialTier.Legendary, 60, 0)));
            model.SetOpen(true);

            Assert.That(presenter.IsShown, Is.True);
            Assert.That(presenter.SlotButtonCount, Is.EqualTo(2));
            Assert.That((presenter.SlotButton(0).text, presenter.SlotButton(1).text), Is.EqualTo(("Empty", "Demon")));
            Assert.That(presenter.RowCount, Is.EqualTo(3));
            Assert.That(presenter.RowLabel(0), Is.EqualTo("Demon (Normal)   100 / 500 kills   in slot 2"));
            Assert.That(presenter.RowLabel(1), Is.EqualTo("Alien (not earned)   37 / 100 kills"));
            Assert.That(presenter.RowLabel(2), Is.EqualTo("Cyclops (Legendary)   60 kills, top tier"));
            Assert.That(presenter.DetailText, Is.EqualTo("Select a card, then a slot."));
        }

        [Test]
        public void Selecting_a_card_shows_its_tiers_bonus_and_choosing_a_slot_places_it()
        {
            var presenter = Presenter(out var model, out var binder, out var requests);
            model.SetState(State(1, BookCause.Initial, Card(Demon, BystialTier.Normal, 100, 500)));
            model.SetOpen(true);

            binder.SelectCard(Demon); // the row's click
            Assert.That(presenter.DetailText, Is.EqualTo("Max HP +10, Fire resistance +5%")); // BystialCardDef m1.demon.normal
            Assert.That(presenter.TakeOutEnabled, Is.False);
            binder.PlaceInto(0); // the slot's click

            Assert.That(requests.Sent, Is.EqualTo(new[] { (Demon, (byte)0) }));
        }

        // ---- layout measured on a real panel (spec 9.8, 9.13)

        private const string ThemePath = "Assets/Game/UI/Login/LoginTheme.tss"; // the theme HudSceneInstaller gives the HUD

        [UnityTest]
        public System.Collections.IEnumerator The_window_fits_its_layout_row_and_holds_eight_slots_and_its_rows()
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var book = BookConfig.From(set);
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            settings.scaleMode = PanelScaleMode.ConstantPixelSize; // reference pixels (spec 9.2: 1280 x 720)
            var target = new RenderTexture(1280, 720, 24);
            settings.targetTexture = target;
            var host = new GameObject("book-layout-test");
            BookWindowPresenter? presenter = null;
            try
            {
                var document = host.AddComponent<UIDocument>();
                document.panelSettings = settings;
                yield return null;
                var root = document.rootVisualElement;
                Assert.That(root, Is.Not.Null, "the UIDocument made no panel");
                root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(Game.UI.Editor.HudSceneInstaller.StylesPath));
                root.style.unityFontDefinition = FontDefinition.FromFont(AssetDatabase.LoadAssetAtPath<Font>(Game.UI.Editor.HudSceneInstaller.FontPath));
                root.style.fontSize = hud.Theme.Tokens.FontSizeBody;

                var model = new BookWindowViewModel();
                var binder = new BookWindowBinder(book, hud.Text, model, new ToastViewModel(), 1500, () => 0, readToggleKey: false);
                var view = new HudView(hud);
                root.Add(view.Root);
                presenter = new BookWindowPresenter(hud, view, book, model, binder);
                presenter.Bind();
                // Rank SS: eight slots, all full, and the three M1 cards.
                model.SetState(State(BookLimits.MaxSlots / 2, BookCause.Initial, Card(Demon, BystialTier.Legendary, 5000, 0, slot: 0),
                    Card(Alien, BystialTier.Epic, 2500, 5000, slot: 7), Card(Cyclops, BystialTier.Rare, 7, 20, slot: 3)));
                model.SetOpen(true);
                Layout(root);

                var window = presenter.Window;
                Assert.That(hud.TryGetLayout(HudElementIds.BookWindow, out var row), Is.True);
                Assert.That((window.layout.width, window.layout.height), Is.EqualTo(((float)row.Size[0], (float)row.Size[1])),
                    "the window keeps the size of its HudLayout row");
                var inner = Inner(window);
                for (int slot = 0; slot < presenter.SlotButtonCount; slot++)
                    AssertInside(presenter.SlotButton(slot).worldBound, inner, "slot " + slot);
                Assert.That(presenter.SlotButton(4).worldBound.y, Is.GreaterThan(presenter.SlotButton(3).worldBound.y),
                    "four slots per line");
                for (int i = 0; i < presenter.RowCount; i++)
                    AssertInside(presenter.Row(i).worldBound, inner, "row " + i);
            }
            finally
            {
                presenter?.Dispose();
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(settings);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        /// <summary>Runs the panel's style and layout pass now (an edit-mode panel is not updated by the player loop).</summary>
        private static void Layout(VisualElement element)
        {
            var update = element.panel.GetType().GetMethod("UpdateWithoutRepaint",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null);
            Assert.That(update, Is.Not.Null, "UI Toolkit's panel has no UpdateWithoutRepaint");
            update!.Invoke(element.panel, null);
        }

        private static Rect Inner(VisualElement element)
        {
            var bounds = element.worldBound;
            var style = element.resolvedStyle;
            return Rect.MinMaxRect(bounds.xMin + style.borderLeftWidth, bounds.yMin + style.borderTopWidth,
                bounds.xMax - style.borderRightWidth, bounds.yMax - style.borderBottomWidth);
        }

        private static void AssertInside(Rect rect, Rect box, string what)
        {
            const float slack = 0.5f;
            Assert.That(rect.xMin >= box.xMin - slack && rect.xMax <= box.xMax + slack && rect.yMin >= box.yMin - slack
                        && rect.yMax <= box.yMax + slack, Is.True, $"{what} {rect} is not inside {box}");
        }

        // ---- committed texts

        [Test]
        public void Committed_ui_strings_cover_every_book_text_id_and_reason_and_the_toggle_key_is_bound()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            var reasons = new HashSet<string>(set.TryGet(typeof(ReasonCodeStrings), out var codes) ? codes.Rows.Cast<ReasonCodeStrings>().Select(r => r.Code) : new string[0]);

            Assert.That(BookTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(System.Enum.GetNames(typeof(BookReason)).Where(n => n != "None" && !reasons.Contains(n)), Is.Empty);
            Assert.That(BookConfig.From(set).ToggleBinding, Is.EqualTo("<Keyboard>/b"));
            // The M1 three and the field zone's seven (WP-M2-7: MonsterDef net ids 4 to 10, imp to the warlord).
            Assert.That(BookConfig.From(set).Cards.Keys,
                Is.EquivalentTo(new ushort[] { Demon, Alien, Cyclops, 4, 5, 6, 7, 8, 9, 10 }));
        }
    }
}
