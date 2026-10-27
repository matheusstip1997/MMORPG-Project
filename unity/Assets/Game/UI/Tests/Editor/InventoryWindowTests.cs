#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Inventory;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Inventory;
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
    /// The inventory window (spec 9.13, backlog G client part, WP16): the view model's slot layout and its previews of
    /// moves, merges and swaps through the shared Bag rules, the revert on a refusal or a timeout, click-to-move; the
    /// binder's requests and toasts; the presenter's grid, counts, icons and tooltip from the committed tables.
    /// </summary>
    public sealed class InventoryWindowTests
    {
        private const ushort Horn = 1, Ash = 2; // m1.demonHorn, m1.demonAsh (maxStack 99)

        private static InventoryContents Contents(params (byte Slot, ushort Item, ushort Count)[] slots) => new InventoryContents
        {
            Revision = 1,
            SlotCount = 30,
            Slots = slots.Select(s => new InventorySlotEntry { Slot = s.Slot, ItemNetId = s.Item, Count = s.Count }).ToArray(),
        };

        private static InventoryWindowViewModel Model(out List<int> changes)
        {
            var model = new InventoryWindowViewModel(30, 6, _ => 99);
            var counter = new List<int>();
            model.Changed += () => counter.Add(counter.Count);
            changes = counter;
            return model;
        }

        private static (ushort Item, int Count, bool Preview) At(InventoryWindowViewModel model, int slot)
        {
            var view = model.Slot(slot);
            return (view.ItemNetId, view.Count, view.Preview);
        }

        private static InventoryMoveResult Accepted(uint id, InventoryMoveOutcome outcome = InventoryMoveOutcome.Moved) =>
            new InventoryMoveResult { RequestId = id, Outcome = outcome };

        private static InventoryMoveResult Refused(uint id, InventoryMoveReason reason) =>
            new InventoryMoveResult { RequestId = id, Reason = reason };

        // ---- view model

        [Test]
        public void The_grid_has_the_bag_slots_in_rows_of_the_configured_columns()
        {
            var model = Model(out _);

            Assert.That((model.SlotCount, model.Columns, model.Rows), Is.EqualTo((30, 6, 5)));
            Assert.That(model.HasContents, Is.False);
            Assert.That(Enumerable.Range(0, 30).All(i => model.Slot(i).IsEmpty), Is.True);
            Assert.That(new InventoryWindowViewModel(31, 6, _ => 1).Rows, Is.EqualTo(6));
        }

        [Test]
        public void Contents_show_as_confirmed_slots_and_a_repeat_raises_nothing()
        {
            var model = Model(out var changes);

            model.SetContents(Contents((0, Horn, 5), (7, Ash, 2)));
            int raised = changes.Count;
            model.SetContents(Contents((0, Horn, 5), (7, Ash, 2)));

            Assert.That(At(model, 0), Is.EqualTo((Horn, 5, false)));
            Assert.That(At(model, 7), Is.EqualTo((Ash, 2, false)));
            Assert.That(raised, Is.EqualTo(1));
            Assert.That(changes.Count, Is.EqualTo(raised));
        }

        [Test]
        public void A_move_into_an_empty_slot_is_previewed_until_the_contents_that_include_it()
        {
            var model = Model(out _);
            model.SetContents(Contents((0, Horn, 5)));

            model.AddPending(1, new MoveIntent(0, 4), 0);

            Assert.That(At(model, 0), Is.EqualTo(((ushort)0, 0, true)));
            Assert.That(At(model, 4), Is.EqualTo((Horn, 5, true)));
            Assert.That(model.OnMoveResult(Accepted(1)), Is.Null);
            Assert.That(At(model, 4), Is.EqualTo((Horn, 5, true))); // still shown: the new bag comes right after
            model.SetContents(Contents((4, Horn, 5)));
            Assert.That(At(model, 4), Is.EqualTo((Horn, 5, false)));
            Assert.That(model.Pending, Is.Empty);
        }

        [Test]
        public void A_merge_preview_moves_what_fits_and_leaves_the_rest()
        {
            var model = Model(out _);
            model.SetContents(Contents((0, Horn, 60), (1, Horn, 50)));

            model.AddPending(1, new MoveIntent(0, 1), 0);

            Assert.That(At(model, 1), Is.EqualTo((Horn, 99, true)));
            Assert.That(At(model, 0), Is.EqualTo((Horn, 11, true)));
        }

        [Test]
        public void A_swap_preview_trades_two_different_stacks()
        {
            var model = Model(out _);
            model.SetContents(Contents((0, Horn, 5), (3, Ash, 2)));

            model.AddPending(1, new MoveIntent(0, 3), 0);

            Assert.That(At(model, 0), Is.EqualTo((Ash, 2, true)));
            Assert.That(At(model, 3), Is.EqualTo((Horn, 5, true)));
        }

        [Test]
        public void A_refusal_reverts_the_preview_and_gives_its_reason_code()
        {
            var model = Model(out _);
            model.SetContents(Contents((0, Horn, 5), (3, Ash, 2)));
            model.AddPending(1, new MoveIntent(0, 3), 0);

            string? reason = model.OnMoveResult(Refused(1, InventoryMoveReason.ItemSlotEmpty));

            Assert.That(reason, Is.EqualTo("ItemSlotEmpty"));
            Assert.That(At(model, 0), Is.EqualTo((Horn, 5, false)));
            Assert.That(At(model, 3), Is.EqualTo((Ash, 2, false)));
            Assert.That(model.Pending, Is.Empty);
        }

        [Test]
        public void A_move_with_no_answer_is_dropped_after_the_timeout()
        {
            var model = Model(out _);
            model.SetContents(Contents((0, Horn, 5)));
            model.AddPending(1, new MoveIntent(0, 4), 1_000);

            model.Expire(5_999, 5_000);
            Assert.That(At(model, 4).Count, Is.EqualTo(5));
            model.Expire(6_000, 5_000);

            Assert.That(At(model, 0), Is.EqualTo((Horn, 5, false)));
            Assert.That(model.Slot(4).IsEmpty, Is.True);
        }

        [Test]
        public void New_contents_keep_the_moves_still_waiting_on_top()
        {
            var model = Model(out _);
            model.SetContents(Contents((0, Horn, 5), (1, Ash, 2)));
            model.AddPending(1, new MoveIntent(0, 4), 0);
            model.OnMoveResult(Accepted(1));
            model.AddPending(2, new MoveIntent(1, 5), 0);

            model.SetContents(Contents((4, Horn, 5), (1, Ash, 2))); // the save had move 1 only

            Assert.That(model.Pending.Select(m => m.RequestId), Is.EqualTo(new[] { 2u }));
            Assert.That(At(model, 4), Is.EqualTo((Horn, 5, false)));
            Assert.That(At(model, 5), Is.EqualTo((Ash, 2, true)));
            Assert.That(model.Slot(1).IsEmpty, Is.True);
        }

        [Test]
        public void Click_to_move_selects_deselects_and_moves()
        {
            var model = Model(out _);
            model.SetContents(Contents((0, Horn, 5)));

            Assert.That(model.Click(3), Is.Null); // an empty slot with nothing selected
            Assert.That(model.Selected, Is.EqualTo(InventoryWindowViewModel.None));
            Assert.That(model.Click(0), Is.Null);
            Assert.That(model.Selected, Is.EqualTo(0));
            Assert.That(model.Click(0), Is.Null); // deselect
            Assert.That(model.Selected, Is.EqualTo(InventoryWindowViewModel.None));
            model.Click(0);
            var intent = model.Click(9);

            Assert.That(intent.HasValue, Is.True);
            Assert.That((intent!.Value.FromSlot, intent.Value.ToSlot), Is.EqualTo(((byte)0, (byte)9)));
            Assert.That(model.Selected, Is.EqualTo(InventoryWindowViewModel.None));
        }

        [Test]
        public void Drops_onto_the_same_slot_or_from_an_empty_one_send_nothing_and_a_drop_outside_is_refused()
        {
            var model = Model(out _);
            model.SetContents(Contents((0, Horn, 5)));

            Assert.That(model.Drop(0, 0), Is.Null);
            Assert.That(model.Drop(2, 0), Is.Null);
            Assert.That(model.Drop(0, 30), Is.Null);
            Assert.That(model.Drop(0, 29).HasValue, Is.True);
            Assert.That(model.DropOutside(0), Is.EqualTo(nameof(ClientReasonCode.ItemDropUnavailable)));
        }

        [Test]
        public void Closing_clears_the_selection_and_a_disconnect_clears_everything()
        {
            var model = Model(out _);
            model.SetContents(Contents((0, Horn, 5)));
            model.SetOpen(true);
            model.Click(0);
            model.SetHovered(0);

            model.Toggle();
            Assert.That((model.Open, model.Selected, model.Hovered), Is.EqualTo((false, -1, -1)));

            model.AddPending(1, new MoveIntent(0, 1), 0);
            model.Clear();
            Assert.That(model.HasContents, Is.False);
            Assert.That(model.Pending, Is.Empty);
            Assert.That(model.Slot(0).IsEmpty, Is.True);
        }

        // ---- binder

        private sealed class FakeRequests : IWorldRequests
        {
            public List<(byte From, byte To)> Sent { get; } = new List<(byte, byte)>();

            /// <summary>The same moves with their container codes (WP-M2-1).</summary>
            public List<(byte FromContainer, byte From, byte ToContainer, byte To)> Moves { get; } = new List<(byte, byte, byte, byte)>();

            public uint RequestInventoryMove(byte fromContainer, byte fromSlot, byte toContainer, byte toSlot)
            {
                Sent.Add((fromSlot, toSlot));
                Moves.Add((fromContainer, fromSlot, toContainer, toSlot));
                return (uint)Sent.Count;
            }

            public uint RequestOutpostInteract(uint npcEntityId, NpcInteraction kind) => 0;

            public uint RequestSellQuote(uint npcEntityId, byte slot, ushort count) => 0;

            public uint RequestSell(uint npcEntityId, byte slot, ushort count, long quotedTotal) => 0;

            public uint RequestVendorBuy(uint npcEntityId, ushort itemNetId, ushort count, long quotedTotal) => 0;
        }

        private static WorldClientFeed Feed(ConfigSet set)
        {
            var settings = PredictionSettings.From(set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
            var catalog = ActionCatalog.From(set);
            return new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs);
        }

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        [Test]
        public void The_binder_sends_moves_previews_them_and_toasts_refusals()
        {
            var set = Tables();
            var inventory = InventoryConfig.From(set);
            var model = new InventoryWindowViewModel(inventory.BagSlots, inventory.Settings.Columns, inventory.MaxStack);
            var toast = new ToastViewModel();
            var binder = new InventoryWindowBinder(inventory, model, toast, 1500, () => 0, readToggleKey: false);
            var feed = Feed(set);
            var requests = new FakeRequests();
            feed.Requests = requests;
            feed.RaiseInventory(Contents((0, Horn, 5), (1, Ash, 2)));
            binder.Bind(feed); // the bag came before the HUD bound

            Assert.That(At(model, 0), Is.EqualTo((Horn, 5, false)));
            binder.Click(0);
            binder.Click(6);
            binder.Drop(1, 7);

            Assert.That(requests.Sent, Is.EqualTo(new[] { ((byte)0, (byte)6), ((byte)1, (byte)7) }));
            Assert.That(At(model, 6), Is.EqualTo((Horn, 5, true)));
            feed.RaiseInventoryMoveAnswered(Refused(2, InventoryMoveReason.ItemStackFull));
            Assert.That((toast.Visible, toast.ReasonCode), Is.EqualTo((true, "ItemStackFull")));
            Assert.That(At(model, 1), Is.EqualTo((Ash, 2, false)));

            binder.DropOutside(0);
            Assert.That(toast.ReasonCode, Is.EqualTo(nameof(ClientReasonCode.ItemDropUnavailable)));
            Assert.That(requests.Sent.Count, Is.EqualTo(2));

            feed.RaiseDisconnected(nameof(ClientReasonCode.ConnectionLost));
            Assert.That(model.HasContents, Is.False);
            binder.Dispose();
        }

        [Test]
        public void Without_a_connection_nothing_is_previewed()
        {
            var set = Tables();
            var inventory = InventoryConfig.From(set);
            var model = new InventoryWindowViewModel(inventory.BagSlots, inventory.Settings.Columns, inventory.MaxStack);
            var binder = new InventoryWindowBinder(inventory, model, new ToastViewModel(), 1500, () => 0, readToggleKey: false);
            var feed = Feed(set); // no Requests
            binder.Bind(feed);
            feed.RaiseInventory(Contents((0, Horn, 5)));

            binder.Drop(0, 3);

            Assert.That(model.Pending, Is.Empty);
            Assert.That(At(model, 0), Is.EqualTo((Horn, 5, false)));
            binder.Dispose();
        }

        // ---- presenter and config

        private static InventoryWindowPresenter Presenter(out InventoryWindowViewModel model, out VisualElement root)
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var inventory = InventoryConfig.From(set);
            model = new InventoryWindowViewModel(inventory.BagSlots, inventory.Settings.Columns, inventory.MaxStack);
            var binder = new InventoryWindowBinder(inventory, model, new ToastViewModel(), 1500, () => 0, readToggleKey: false);
            var view = new HudView(hud);
            root = view.Root;
            var presenter = new InventoryWindowPresenter(hud, view, inventory, model, binder, icons: null);
            presenter.Bind();
            return presenter;
        }

        [Test]
        public void The_window_starts_closed_and_shows_one_element_per_bag_slot()
        {
            var presenter = Presenter(out var model, out var root);

            Assert.That(presenter.IsShown, Is.False);
            Assert.That(presenter.SlotElementCount, Is.EqualTo(30)); // ContainerDef normalBag
            model.SetOpen(true);
            Assert.That(presenter.IsShown, Is.True);
            Assert.That(root.Q(HudElementIds.InventoryWindow), Is.Not.Null);
            Assert.That(root.Q("inv_slot_29"), Is.Not.Null);
            LogAssert.NoUnexpectedReceived();
            presenter.Dispose();
        }

        [Test]
        public void Slots_show_counts_and_each_item_its_own_placeholder_icon()
        {
            var presenter = Presenter(out var model, out _);
            model.SetOpen(true);

            model.SetContents(Contents((0, Horn, 5), (1, Ash, 1)));

            Assert.That(presenter.CountText(0), Is.EqualTo("5"));
            Assert.That(presenter.CountText(1), Is.EqualTo("")); // a single item shows no count
            Assert.That(presenter.Icon(2).IsEmpty, Is.True);
            Assert.That((presenter.Icon(0).Shape, presenter.Icon(1).Shape), Is.EqualTo((IconShape.Triangle, IconShape.Square)));
            Assert.That(presenter.Icon(0).Color, Is.Not.EqualTo(presenter.Icon(1).Color));
            Assert.That(presenter.Icon(0).Color, Is.Not.EqualTo(Color.magenta));
            presenter.Dispose();
        }

        [Test]
        public void Hovering_a_stack_shows_its_tooltip_from_the_tables()
        {
            var presenter = Presenter(out var model, out _);
            model.SetOpen(true);
            model.SetContents(Contents((0, Horn, 5)));

            model.SetHovered(0);
            Assert.That(presenter.TooltipText, Is.EqualTo("Demon Horn\nRarity: Common\nType: Sell-only\n5 / 99"));
            model.SetHovered(3); // empty
            Assert.That(presenter.TooltipText, Is.EqualTo(""));
            presenter.Dispose();
        }

        [Test]
        public void Committed_ui_strings_cover_every_inventory_text_id_and_reason()
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var ids = typeof(InventoryTextIds).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()).ToList();

            Assert.That(ids.Where(id => !hud.Text.Has(id)), Is.Empty);
            foreach (var reason in System.Enum.GetNames(typeof(InventoryMoveReason)).Where(n => n != "None"))
                Assert.That(hud.Reasons.Has(reason), Is.True, reason);
            Assert.That(hud.Reasons.Has(nameof(ClientReasonCode.ItemDropUnavailable)), Is.True);
            Assert.That(InventoryConfig.From(set).ToggleBinding, Is.EqualTo("<Keyboard>/i"));
        }

        // ---- containers and equipment (WP-M2-1, spec 4.1 M2 item model, 9.13)

        private const byte NormalBag = 1, SafeGear = 2, Equipped = 5;
        private const ushort Sword = 101, Daggers = 103, Bow = 102; // m2.trainingSword, m2.twinDaggers, m2.trainingBow

        private static InventoryWindowViewModel FullModel(InventoryConfig inventory) =>
            new InventoryWindowViewModel(inventory.Containers, inventory.NormalBagCode, inventory.Settings.Columns, inventory);

        private static InventorySlotEntry Gear(byte slot, ushort item, long id) =>
            new InventorySlotEntry { Slot = slot, ItemNetId = item, Count = 1, InstanceId = id };

        private static InventoryContents WithContainers(InventorySlotEntry[] safe, InventorySlotEntry[] equipped) => new InventoryContents
        {
            Revision = 1,
            SlotCount = 30,
            Containers = new[]
            {
                new InventoryContainerEntry { Code = SafeGear, SlotCount = 20, Slots = safe },
                new InventoryContainerEntry { Code = 3, SlotCount = 10 },
                new InventoryContainerEntry { Code = 4, SlotCount = 20 },
                new InventoryContainerEntry { Code = Equipped, SlotCount = 8, Slots = equipped },
            },
        };

        [Test]
        public void The_window_has_a_tab_per_bag_container_and_the_equipment_strip_after_the_grid()
        {
            var model = FullModel(InventoryConfig.From(Tables()));

            Assert.That(model.Tabs, Is.EqualTo(new byte[] { NormalBag, SafeGear, 3, 4 }));
            Assert.That((model.ActiveContainer, model.SlotCount, model.EquipSlotCount), Is.EqualTo((NormalBag, 30, 8)));
            model.SetActiveContainer(SafeGear);
            Assert.That((model.ActiveContainer, model.SlotCount), Is.EqualTo((SafeGear, 20)));
            model.SetActiveContainer(Equipped); // the equipped set is the strip, not a tab
            Assert.That(model.ActiveContainer, Is.EqualTo(SafeGear));
        }

        [Test]
        public void Equipping_from_the_safe_bag_is_a_move_into_the_equipped_container_and_previews_at_once()
        {
            var model = FullModel(InventoryConfig.From(Tables()));
            model.SetContents(WithContainers(new[] { Gear(0, Sword, 7) }, new InventorySlotEntry[0]));
            model.SetActiveContainer(SafeGear);

            Assert.That(model.Click(0), Is.Null); // selected
            var move = model.Click(model.SlotCount + 0); // the main weapon slot of the strip

            Assert.That(move.HasValue, Is.True);
            Assert.That((move!.Value.FromContainer, move.Value.FromSlot, move.Value.ToContainer, move.Value.ToSlot),
                Is.EqualTo((SafeGear, (byte)0, Equipped, (byte)0)));
            model.AddPending(1, move.Value, 0);
            Assert.That((model.Slot(model.SlotCount).ItemNetId, model.Slot(model.SlotCount).Preview), Is.EqualTo((Sword, true)));
            Assert.That(model.Slot(0).IsEmpty, Is.True);

            model.OnMoveResult(Refused(1, InventoryMoveReason.InCombat)); // the server says no: back where it was
            Assert.That(model.Slot(0).ItemNetId, Is.EqualTo(Sword));
            Assert.That(model.Slot(model.SlotCount).IsEmpty, Is.True);
        }

        [Test]
        public void A_dual_weapon_shows_the_second_weapon_slot_covered()
        {
            var model = FullModel(InventoryConfig.From(Tables()));

            model.SetContents(WithContainers(new InventorySlotEntry[0], new[] { Gear(0, Daggers, 9) }));

            Assert.That(model.Slot(model.SlotCount + 0).ItemNetId, Is.EqualTo(Daggers));
            Assert.That((model.Slot(model.SlotCount + 1).IsEmpty, model.Slot(model.SlotCount + 1).Covered), Is.EqualTo((true, true)));
            Assert.That(model.Slot(model.SlotCount + 2).Covered, Is.False);
        }

        [Test]
        public void The_selection_keeps_its_container_across_tabs_and_the_binder_sends_the_container_codes()
        {
            var set = Tables();
            var inventory = InventoryConfig.From(set);
            var model = FullModel(inventory);
            var binder = new InventoryWindowBinder(inventory, model, new ToastViewModel(), 1500, () => 0, readToggleKey: false);
            var feed = Feed(set);
            var requests = new FakeRequests();
            feed.Requests = requests;
            binder.Bind(feed);
            feed.RaiseInventory(WithContainers(new[] { Gear(3, Bow, 4) }, new[] { Gear(0, Sword, 5) }));

            binder.SelectTab(SafeGear);
            binder.Click(3);
            binder.SelectTab(NormalBag);
            Assert.That(model.Selected, Is.EqualTo(InventoryWindowViewModel.None)); // not on this tab
            binder.SelectTab(SafeGear);
            Assert.That(model.Selected, Is.EqualTo(3));
            binder.Drop(model.SlotCount + 0, 5); // unequip the sword onto slot 5 of the Safe bag

            Assert.That(requests.Moves, Is.EqualTo(new[] { (Equipped, (byte)0, SafeGear, (byte)5) }));
            Assert.That(model.Slot(5).ItemNetId, Is.EqualTo(Sword));
            binder.Dispose();
        }

        [Test]
        public void The_presenter_shows_the_tabs_and_one_element_per_equipment_slot()
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var inventory = InventoryConfig.From(set);
            var model = FullModel(inventory);
            var binder = new InventoryWindowBinder(inventory, model, new ToastViewModel(), 1500, () => 0, readToggleKey: false);
            var view = new HudView(hud);
            var presenter = new InventoryWindowPresenter(hud, view, inventory, model, binder, icons: null);
            presenter.Bind();
            model.SetOpen(true);
            model.SetContents(WithContainers(new InventorySlotEntry[0], new[] { Gear(0, Sword, 5) }));

            Assert.That(presenter.EquipElementCount, Is.EqualTo(8));
            Assert.That(presenter.EquipIcon(0).IsEmpty, Is.False);
            Assert.That(presenter.EquipIcon(1).IsEmpty, Is.True);
            Assert.That(presenter.TabButton(NormalBag)!.text, Is.EqualTo("Bag"));
            Assert.That(presenter.TabButton(SafeGear)!.text, Is.EqualTo("Safe: gear"));
            Assert.That(view.Root.Q("inv_equip_7"), Is.Not.Null);
            model.SetHovered(model.SlotCount + 0);
            Assert.That(presenter.TooltipText, Does.StartWith("Training Sword"));
            LogAssert.NoUnexpectedReceived();
            presenter.Dispose();
        }

        // ---- layout measured on a real panel (QA M2 wave 0 F1; spec 9.8, 9.13)

        private const string ThemePath = "Assets/Game/UI/Login/LoginTheme.tss"; // the theme HudSceneInstaller gives the HUD

        [UnityTest]
        public System.Collections.IEnumerator The_window_fits_its_layout_row_with_one_tab_line_the_strip_below_the_grid_and_readable_labels()
        {
            var set = Tables();
            var hud = HudConfig.From(set);
            var inventory = InventoryConfig.From(set);
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            settings.scaleMode = PanelScaleMode.ConstantPixelSize; // reference pixels (spec 9.2: 1280 x 720)
            var target = new RenderTexture(1280, 720, 24);
            settings.targetTexture = target;
            var host = new GameObject("inventory-layout-test");
            InventoryWindowPresenter? presenter = null;
            try
            {
                var document = host.AddComponent<UIDocument>();
                document.panelSettings = settings;
                yield return null;
                var root = document.rootVisualElement;
                Assert.That(root, Is.Not.Null, "the UIDocument made no panel");
                // As HudRoot: the HUD styles, font and body size on the root.
                root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(Game.UI.Editor.HudSceneInstaller.StylesPath));
                root.style.unityFontDefinition = FontDefinition.FromFont(AssetDatabase.LoadAssetAtPath<Font>(Game.UI.Editor.HudSceneInstaller.FontPath));
                root.style.fontSize = hud.Theme.Tokens.FontSizeBody;

                var model = FullModel(inventory);
                var binder = new InventoryWindowBinder(inventory, model, new ToastViewModel(), 1500, () => 0, readToggleKey: false);
                var view = new HudView(hud);
                root.Add(view.Root);
                presenter = new InventoryWindowPresenter(hud, view, inventory, model, binder, icons: null);
                presenter.Bind();
                model.SetOpen(true);
                model.SetContents(WithContainers(new InventorySlotEntry[0], new InventorySlotEntry[0]));
                Layout(root);

                var window = presenter.Window;
                Assert.That(hud.TryGetLayout(HudElementIds.InventoryWindow, out var row), Is.True);
                Assert.That((window.layout.width, window.layout.height), Is.EqualTo(((float)row.Size[0], (float)row.Size[1])),
                    "the window keeps the size of its HudLayout row");
                var inner = Inner(window);

                // The tab row is one line inside the window, and every tab shows its whole text.
                var tabs = model.Tabs.Select(code => presenter.TabButton(code)!).ToList();
                foreach (var tab in tabs)
                {
                    Assert.That(tab.worldBound.y, Is.EqualTo(tabs[0].worldBound.y).Within(0.5f), tab.text + " is on the first tab line");
                    AssertInside(tab.worldBound, inner, tab.text);
                    var text = tab.MeasureTextSize(tab.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined);
                    Assert.That(tab.contentRect.width, Is.GreaterThanOrEqualTo(text.x - 0.5f), tab.text + " is not clipped");
                }

                // The whole grid is visible and the equipment strip starts below its last row.
                var grid = window.Q("inv_slot_0").parent;
                var lastSlot = presenter.SlotElement(presenter.SlotElementCount - 1);
                Assert.That(grid.worldBound.yMax, Is.GreaterThanOrEqualTo(lastSlot.worldBound.yMax - 0.5f), "the grid holds its last row");
                Assert.That(tabs[0].worldBound.yMax, Is.LessThanOrEqualTo(presenter.SlotElement(0).worldBound.y + 0.5f),
                    "the tab row ends above the grid");
                var strip = window.Q("inv_equip");
                Assert.That(strip.worldBound.y, Is.GreaterThanOrEqualTo(lastSlot.worldBound.yMax - 0.5f), "the strip starts below the grid");
                for (int i = 0; i < presenter.SlotElementCount; i++)
                    AssertInside(presenter.SlotElement(i).worldBound, inner, "grid slot " + i);

                // Every equipment slot is inside the window; an empty one names its slot in body-size text inside the slot.
                for (int i = 0; i < presenter.EquipElementCount; i++)
                {
                    var slot = presenter.EquipElement(i);
                    AssertInside(slot.worldBound, inner, "equipment slot " + i);
                    var name = slot.Q<Label>(className: "inv-slot__name");
                    Assert.That(name.resolvedStyle.fontSize, Is.GreaterThanOrEqualTo((float)hud.Theme.Tokens.FontSizeBody),
                        name.text + " is at least the body font size (spec 9.8)");
                    AssertInside(name.worldBound, Inner(slot), name.text);
                    var text = name.MeasureTextSize(name.text, name.contentRect.width, VisualElement.MeasureMode.Exactly, 0,
                        VisualElement.MeasureMode.Undefined);
                    Assert.That(text.y, Is.LessThanOrEqualTo(name.contentRect.height + 0.5f), name.text + " fits its slot");
                }
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

        /// <summary>The element's box inside its border, in panel space.</summary>
        private static Rect Inner(VisualElement element)
        {
            var bounds = element.worldBound;
            var style = element.resolvedStyle;
            return Rect.MinMaxRect(bounds.xMin + style.borderLeftWidth, bounds.yMin + style.borderTopWidth,
                bounds.xMax - style.borderRightWidth, bounds.yMax - style.borderBottomWidth);
        }

        private static void AssertInside(Rect rect, Rect box, string what)
        {
            const float Tolerance = 0.5f; // layout rounding
            Assert.That(rect.xMin >= box.xMin - Tolerance && rect.yMin >= box.yMin - Tolerance
                && rect.xMax <= box.xMax + Tolerance && rect.yMax <= box.yMax + Tolerance, Is.True,
                $"{what} {rect} is inside {box}");
        }
    }
}
