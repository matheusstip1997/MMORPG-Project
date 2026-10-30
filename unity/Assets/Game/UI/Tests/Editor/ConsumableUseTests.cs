#nullable enable
using System.Collections.Generic;
using System.Linq;
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
using Shared.Protocol;
using Shared.Simulation.Actions;
using UnityEngine;

namespace Game.UI.Tests
{
    /// <summary>
    /// Consumable use in the HUD (spec 3.4 consumable use, 9.13; WP-M3-3): right-click or double-click on a stack asks the
    /// server to use it; refusals (the client's and the server's) show in the toast; the quick-item slot binds a consumable
    /// dragged onto it, keeps it per character and uses the lowest Normal bag stack of it; the client's checks and cooldowns.
    /// </summary>
    public sealed class ConsumableUseTests
    {
        private const ushort Horn = 1, Potion = 113, Bread = 114; // m1.demonHorn, m2.potionHpSmall, m2.breadLoaf

        private sealed class FakeUseRequests : IUseItemRequests
        {
            public List<(byte Container, byte Slot)> Sent { get; } = new List<(byte, byte)>();

            /// <summary>What the client's check answers instead of sending (None: sent).</summary>
            public UseItemReason Answer { get; set; }

            public UseItemReason RequestUseItem(byte container, byte slot, out uint requestId)
            {
                requestId = 0;
                if (Answer != UseItemReason.None)
                    return Answer;
                Sent.Add((container, slot));
                requestId = (uint)Sent.Count;
                return UseItemReason.None;
            }
        }

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static WorldClientFeed Feed(ConfigSet set)
        {
            var settings = PredictionSettings.From(set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
            var catalog = ActionCatalog.From(set);
            return new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs) { Consumables = new ClientConsumables(set) };
        }

        private static InventoryContents Contents(params (byte Slot, ushort Item, ushort Count)[] slots) => new InventoryContents
        {
            Revision = 1,
            SlotCount = 30,
            Slots = slots.Select(s => new InventorySlotEntry { Slot = s.Slot, ItemNetId = s.Item, Count = s.Count }).ToArray(),
        };

        // ---- inventory window

        [Test]
        public void A_use_on_a_stack_asks_the_server_and_its_refusals_show_in_the_toast()
        {
            var set = Tables();
            var inventory = InventoryConfig.From(set);
            var model = new InventoryWindowViewModel(inventory.Containers, inventory.NormalBagCode, inventory.Settings.Columns, inventory);
            var toast = new ToastViewModel();
            var binder = new InventoryWindowBinder(inventory, model, toast, 1500, () => 0, readToggleKey: false);
            var feed = Feed(set);
            var uses = new FakeUseRequests();
            feed.UseItemRequests = uses;
            binder.Bind(feed);
            feed.RaiseInventory(Contents((0, Potion, 3), (4, Bread, 1)));

            binder.Use(4);
            Assert.That(uses.Sent, Is.EqualTo(new[] { (inventory.NormalBagCode, (byte)4) }));
            Assert.That(toast.Visible, Is.False);

            feed.RaiseUseItemAnswered(new UseItemResult { RequestId = 1, ItemNetId = Bread, Reason = UseItemReason.TooFull });
            Assert.That((toast.Visible, toast.ReasonCode), Is.EqualTo((true, nameof(UseItemReason.TooFull))));

            uses.Answer = UseItemReason.OnCooldown; // the client's own check
            binder.Use(0);
            Assert.That(toast.ReasonCode, Is.EqualTo(nameof(UseItemReason.OnCooldown)));
            Assert.That(uses.Sent.Count, Is.EqualTo(1));

            binder.Use(9); // an empty slot asks nothing
            Assert.That(uses.Sent.Count, Is.EqualTo(1));
            binder.Dispose();
        }

        [Test]
        public void A_use_drops_the_selection_and_never_asks_for_a_slot_still_moving()
        {
            var set = Tables();
            var inventory = InventoryConfig.From(set);
            var model = new InventoryWindowViewModel(inventory.Containers, inventory.NormalBagCode, inventory.Settings.Columns, inventory);
            model.SetContents(Contents((0, Potion, 3)));
            model.Click(0); // selected

            Assert.That(model.TryUse(0, out var at, out ushort item), Is.True);
            Assert.That((at.Slot, item, model.Selected), Is.EqualTo((0, Potion, InventoryWindowViewModel.None)));
            model.AddPending(1, new MoveIntent(inventory.NormalBagCode, 0, inventory.NormalBagCode, 5), 0);
            Assert.That(model.TryUse(5, out _, out _), Is.False); // a previewed move: the server's bag may differ
            Assert.That(model.TryUse(model.SlotCount, out _, out _), Is.False); // the equipment strip
        }

        // ---- quick-item slot

        private sealed class QuickRig
        {
            public QuickRig(params (byte Slot, ushort Item, ushort Count)[] bag)
            {
                var set = Tables();
                Inventory = InventoryConfig.From(set);
                Feed = Feed(set);
                Feed.UseItemRequests = Uses;
                Feed.CharacterKey = "r1c7";
                Binder = new QuickItemBinder(set, Inventory, HudConfig.From(set).Text, Model, Toast, 1500, () => 0, _ => Store,
                    readKey: false);
                Binder.Bind(Feed);
                Feed.RaiseInventory(Contents(bag));
                Binder.Frame(0);
            }

            public InventoryConfig Inventory { get; }
            public WorldClientFeed Feed { get; }
            public FakeUseRequests Uses { get; } = new FakeUseRequests();
            public MemoryQuickItemStore Store { get; } = new MemoryQuickItemStore();
            public QuickItemViewModel Model { get; } = new QuickItemViewModel();
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public QuickItemBinder Binder { get; }
        }

        [Test]
        public void A_consumable_dropped_on_the_slot_is_bound_saved_and_the_key_uses_its_lowest_stack()
        {
            var rig = new QuickRig((3, Potion, 2), (1, Potion, 4), (0, Horn, 9));

            Assert.That(rig.Binder.TryBind(Horn), Is.False); // not a consumable: the window refuses the drop as usual
            Assert.That(rig.Binder.TryBind(Potion), Is.True);
            Assert.That(rig.Store.Load(), Is.EqualTo("m2.potionHpSmall"));
            Assert.That((rig.Toast.Visible, rig.Toast.TextId, rig.Toast.ArgValue),
                Is.EqualTo((true, HudTextIds.ToastQuickItemBound, "Small Health Potion")));

            rig.Binder.Frame(0);
            Assert.That((rig.Model.ItemNetId, rig.Model.Held, rig.Model.KeyName), Is.EqualTo((Potion, 6, "X")));
            rig.Binder.Use();
            Assert.That(rig.Uses.Sent, Is.EqualTo(new[] { ((byte)0, (byte)1) })); // the Normal bag's lowest stack
        }

        [Test]
        public void The_binding_comes_back_for_the_character_and_a_key_with_none_left_shows_why()
        {
            var rig = new QuickRig((2, Bread, 1));
            rig.Store.Save("m2.potionHpSmall");
            rig.Feed.RaiseDisconnected(nameof(ClientReasonCode.ConnectionLost));
            rig.Feed.RaiseInventory(Contents((2, Bread, 1)));
            rig.Binder.Frame(0); // the character is known again: the binding is read from its store

            Assert.That((rig.Model.ItemNetId, rig.Model.Held), Is.EqualTo((Potion, 0)));
            rig.Binder.Use();
            Assert.That(rig.Uses.Sent, Is.Empty);
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo(nameof(UseItemReason.ItemSlotEmpty)));
        }

        [Test]
        public void The_cooldown_sweep_runs_from_full_to_empty()
        {
            var model = new QuickItemViewModel();
            model.SetCooldown(readyAtMs: 6_000, cooldownMs: 5_000);

            Assert.That(model.CooldownLeft(1_000), Is.EqualTo(1f));
            Assert.That(model.CooldownLeft(3_500), Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(model.CooldownLeft(6_000), Is.EqualTo(0f));
        }

        // ---- the engine-free client side (also the bots')

        [Test]
        public void The_client_check_knows_the_consumables_one_use_at_a_time_and_the_cooldown_after_an_accepted_use()
        {
            var consumables = new ClientConsumables(Tables());

            Assert.That(consumables.Check(Horn, null, 0), Is.EqualTo(UseItemReason.ItemNotUsable));
            Assert.That(consumables.Check(Potion, null, 0), Is.EqualTo(UseItemReason.None));
            var message = consumables.UseRequest(0, 3, Potion, 1_000, out uint id);
            Assert.That(ReliableMessages.TryRead(message, out UseItemRequest request), Is.True);
            Assert.That((request.RequestId, request.Container, request.Slot), Is.EqualTo((id, (byte)0, (byte)3)));
            Assert.That(consumables.Check(Bread, null, 1_100), Is.EqualTo(UseItemReason.UseInProgress));

            consumables.Apply(ReliableMessages.Write(ReliableMessageId.UseItemResult,
                new UseItemResult { RequestId = id, ItemNetId = Potion, Reason = UseItemReason.None }), out _);

            // Placeholders: the drink's 600 ms startup, then the potion's 5 s cooldown.
            Assert.That(consumables.ReadyAtMs(Potion), Is.EqualTo(1_000 + 600 + 5_000));
            Assert.That(consumables.Check(Potion, null, 2_000), Is.EqualTo(UseItemReason.OnCooldown));
            Assert.That(consumables.Check(Bread, null, 2_000), Is.EqualTo(UseItemReason.None));
            Assert.That(consumables.Check(Potion, null, 6_600), Is.EqualTo(UseItemReason.None));
        }

        [Test]
        public void The_quick_item_key_and_slot_texts_exist()
        {
            var text = HudConfig.From(Tables()).Text;
            foreach (var id in new[] { HudTextIds.QuickItemEmpty, HudTextIds.ToastQuickItemBound, InventoryTextIds.TooltipUse })
                Assert.That(text.Has(id), Is.True, id);
            Assert.That(Shared.Data.Tables.Hotkeys.Find(Tables(), Shared.Data.Tables.Hotkeys.QuickItem)?.Binding, Is.EqualTo("<Keyboard>/x"));
        }
    }
}
