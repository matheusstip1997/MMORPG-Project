#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.Menus.Forge;
using Game.UI.ViewModels.Forge;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Game.UI.Tests
{
    /// <summary>
    /// The forge window (spec 5.3, 9.13; WP-M3-6, Forge v2 WP-M3-14 / M3 decision 13): a station's yes opens it on the Forge
    /// tab with an empty mold slot (and the inventory window to drag from); a mold from the Safe bag's equipment area and a
    /// Normal bag stack per material slot, dropped or chosen in the slot's picker, select what a start sends (nothing moves);
    /// wrong drops toast their reason; the stat preview shows ranges, never a rolled number; the Recipes tab keeps the list
    /// with Next; a start, actions and a finish go out one at a time, refusals and results toast, leaving the station closes
    /// it; the layout on a real panel; the committed texts.
    /// </summary>
    public sealed class ForgeWindowTests
    {
        private const uint Station = 77;
        private const byte Normal = 1;

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static ushort Item(ConfigSet set, string id) =>
            set.TryGet(typeof(ItemDef), out var table) ? (ushort)table.Rows.Cast<ItemDef>().Single(i => i.Id == id).NetId : (ushort)0;

        private sealed class FakeForgeRequests : IForgeRequests
        {
            public List<string> Sent { get; } = new List<string>();
            public List<ForgeInput> LastInputs { get; private set; } = new List<ForgeInput>();

            public uint StartForge(uint stationEntityId, ForgeKind kind, int defNetId, IReadOnlyList<ForgeInput> inputs)
            {
                LastInputs = inputs.ToList();
                Sent.Add($"start {stationEntityId} {kind} {defNetId}");
                return (uint)Sent.Count;
            }

            public uint ForgeAction(int actionNetId)
            {
                Sent.Add($"action {actionNetId}");
                return (uint)Sent.Count;
            }

            public uint FinishForge()
            {
                Sent.Add("finish");
                return (uint)Sent.Count;
            }

            public uint CancelForge()
            {
                Sent.Add("cancel");
                return (uint)Sent.Count;
            }
        }

        private sealed class Rig
        {
            public Rig()
            {
                Set = Tables();
                Hud = HudConfig.From(Set);
                Config = ForgeConfig.From(Set);
                Model = new ForgeWindowViewModel(Config.Rules);
                var settings = PredictionSettings.From(Set);
                Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
                var catalog = ActionCatalog.From(Set);
                Feed = new WorldClientFeed(Set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                    null, null, settings.TickMs) { ForgeRequests = Requests };
                Binder = new ForgeBinder(Config, Hud.Text, Model, Toast, 2_000, () => 0);
                Binder.Bind(Feed);
            }

            public ConfigSet Set { get; }
            public HudConfig Hud { get; }
            public ForgeConfig Config { get; }
            public WorldClientFeed Feed { get; }
            public FakeForgeRequests Requests { get; } = new FakeForgeRequests();
            public ForgeWindowViewModel Model { get; }
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public ForgeBinder Binder { get; }

            /// <summary>The Safe bag's equipment area: the sword mold and the ring mold (as a Blacksmith who bought one).</summary>
            public InventorySlotEntry[] Molds = new InventorySlotEntry[0];

            public void OpenAtForge() =>
                Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 1, NpcEntityId = Station, Kind = NpcInteraction.Forge, Reason = OutpostReason.None });

            public void HoldMolds(params string[] molds)
            {
                Molds = molds.Select((id, i) => new InventorySlotEntry { Slot = (byte)i, ItemNetId = Item(Set, id), Count = 1, InstanceId = 100 + i })
                    .ToArray();
            }

            public void Bag(params (byte Slot, string Item, ushort Count)[] stacks) =>
                Bag(stacks.Select(s => (s.Slot, s.Item, s.Count, false)).ToArray());

            /// <summary>The Normal bag with the reserved flag per stack (a running session holds its inputs), and the molds.</summary>
            public void Bag(params (byte Slot, string Item, ushort Count, bool Reserved)[] stacks) =>
                Feed.RaiseInventory(new InventoryContents
                {
                    Revision = ++_revision, SlotCount = 30,
                    Slots = stacks.Select(s => new InventorySlotEntry
                    {
                        Slot = s.Slot, ItemNetId = Item(Set, s.Item), Count = s.Count, Reserved = s.Reserved,
                    }).ToArray(),
                    Containers = new[] { new InventoryContainerEntry { Code = Config.MoldContainer, SlotCount = 20, Slots = Molds } },
                });

            private uint _revision;

            public InventorySlotEntry Entry(byte slot, string item, ushort count) =>
                new InventorySlotEntry { Slot = slot, ItemNetId = Item(Set, item), Count = count };

            public InventorySlotEntry MoldEntry(string mold) => Molds.Single(m => m.ItemNetId == Item(Set, mold));

            public int Row(string id) => Model.Recipes.ToList().FindIndex(d => d.Id == id);

            /// <summary>Puts the sword mold in the mold slot the way a drag from the Safe bag does.</summary>
            public void DropSwordMold() => Binder.DropOnMold(Config.MoldContainer, MoldEntry("m3.moldSword"));

            public void State(uint requestId, ForgePhase phase, int defNetId, byte[]? actions = null, ForgeReason reason = ForgeReason.None,
                ForgeKind kind = ForgeKind.Mold) =>
                Feed.RaiseForgeState(new ForgeState
                {
                    Version = requestId, RequestId = requestId, Reason = reason, Phase = phase, Kind = kind, DefNetId = (ushort)defNetId,
                    StationEntityId = Station, Actions = actions ?? new byte[0], MaxActions = 5, QualityMin = 50, QualityMax = 300,
                    RarityMin = 0, RarityMax = 1,
                });
        }

        [Test]
        public void A_forges_yes_opens_the_forge_tab_with_an_empty_mold_slot_and_lists_its_recipes()
        {
            var rig = new Rig();
            rig.HoldMolds("m3.moldSword");
            rig.Bag((0, "m3.ingotT1", 3));
            rig.OpenAtForge();

            Assert.That((rig.Model.Open, rig.Model.Station, rig.Model.Tab), Is.EqualTo((true, NpcInteraction.Forge, ForgeTab.Forge)));
            Assert.That(rig.Model.Mold, Is.Null, "the mold slot starts empty: the player puts a mold in");
            Assert.That(rig.Model.CanStart, Is.False);
            Assert.That(rig.Model.Molds.Select(m => m.Id), Does.Contain("m3.moldSword").And.Not.Contain("m3.moldRing")); // the bench's
            Assert.That(rig.Model.Recipes.All(d => d.Kind == ForgeKind.Recipe && d.Station == NpcInteraction.Forge), Is.True);
            Assert.That(rig.Row("m3.refine.ingotT1"), Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void A_mold_and_two_demon_ash_dropped_on_the_slots_start_the_sword_with_those_stacks()
        {
            // The user's check of decision 13: the sword mold, two Demon Ash, a Forged Sword from them.
            var rig = new Rig();
            rig.HoldMolds("m3.moldSword");
            rig.Bag((0, "m3.ingotT1", 3), (4, "m1.demonAsh", 2));
            rig.OpenAtForge();

            rig.DropSwordMold();
            Assert.That(rig.Model.Mold!.Id, Is.EqualTo("m3.moldSword"));
            Assert.That(rig.Model.InputCount, Is.EqualTo(2));
            Assert.That(rig.Model.Chosen(0), Is.Null, "the slots start empty: nothing is picked for the player");
            var ash = rig.Entry(4, "m1.demonAsh", 2);
            rig.Binder.DropOnInput(0, Normal, ash);
            rig.Binder.DropOnInput(1, Normal, ash); // both from the one stack of two
            Assert.That((rig.Model.Chosen(0)!.Slot, rig.Model.Chosen(1)!.Slot), Is.EqualTo(((byte)4, (byte)4)));
            Assert.That(rig.Toast.Visible, Is.False);
            Assert.That(rig.Model.CanStart, Is.True);

            rig.Binder.StartForge();
            rig.Binder.StartForge(); // one at a time
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { $"start {Station} Mold 1" }));
            Assert.That(rig.Requests.LastInputs.Select(i => (i.Container, i.Slot)), Is.EqualTo(new[] { (Normal, (byte)4), (Normal, (byte)4) }));
        }

        [Test]
        public void Wrong_drops_toast_their_reason_and_assign_nothing()
        {
            var rig = new Rig();
            rig.HoldMolds("m3.moldSword", "m3.moldRing");
            rig.Bag((0, "m3.enchantStone", 3), (4, "m1.demonAsh", 1), (5, "m3.moldSword", 1));
            rig.OpenAtForge();

            rig.Binder.DropOnMold(Normal, rig.Entry(5, "m3.moldSword", 1)); // not from the Safe bag's equipment area
            Assert.That((rig.Model.Mold, rig.Toast.ReasonCode), Is.EqualTo(((Shared.Simulation.Crafting.ForgeDefinition?)null, "MoldRequired")));
            rig.Binder.DropOnMold(rig.Config.MoldContainer, rig.MoldEntry("m3.moldRing")); // the jeweler's bench's mold
            Assert.That((rig.Model.Mold, rig.Toast.ReasonCode), Is.EqualTo(((Shared.Simulation.Crafting.ForgeDefinition?)null, "WrongStation")));
            rig.Binder.DropOnMold(rig.Config.MoldContainer, rig.Entry(0, "m3.enchantStone", 3)); // no mold at all
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("MoldRequired"));

            rig.DropSwordMold();
            rig.Binder.DropOnInput(0, Normal, rig.Entry(0, "m3.enchantStone", 3)); // no forge input (MaterialTagDef.forgeInput)
            Assert.That((rig.Model.Chosen(0), rig.Toast.ReasonCode), Is.EqualTo(((InventorySlotEntry?)null, "SlotMaterialInvalid")));
            rig.Binder.DropOnInput(0, rig.Config.MoldContainer, rig.MoldEntry("m3.moldSword")); // not the Normal bag
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("SlotMaterialInvalid"));
            rig.Binder.DropOnInput(0, Normal, rig.Entry(4, "m1.demonAsh", 1));
            rig.Binder.DropOnInput(1, Normal, rig.Entry(4, "m1.demonAsh", 1)); // one unit for two slots
            Assert.That((rig.Model.Chosen(1), rig.Toast.ReasonCode), Is.EqualTo(((InventorySlotEntry?)null, "MaterialsMissing")));
            Assert.That(rig.Model.Chosen(0)!.Slot, Is.EqualTo(4));
            Assert.That(rig.Requests.Sent, Is.Empty);
        }

        [Test]
        public void The_pickers_list_what_each_slot_takes_and_choose_without_a_drag()
        {
            var rig = new Rig();
            rig.HoldMolds("m3.moldSword");
            rig.Bag((0, "m3.enchantStone", 3), (1, "m3.ingotT1", 1), (2, "m1.demonHorn", 4), (4, "m1.demonAsh", 2));
            rig.OpenAtForge();

            rig.Binder.Pick(ForgeWindowViewModel.PickMold);
            Assert.That(rig.Model.PickerTarget, Is.EqualTo(ForgeWindowViewModel.PickMold));
            Assert.That(rig.Model.MoldCandidates().Select(i => rig.Model.Molds[i].Id), Is.EqualTo(new[] { "m3.moldSword" }));
            rig.Binder.PickEntry(0);
            Assert.That((rig.Model.Mold!.Id, rig.Model.PickerTarget), Is.EqualTo(("m3.moldSword", ForgeWindowViewModel.PickNone)));

            rig.Binder.Pick(0);
            Assert.That(rig.Model.Candidates(0).Select(e => e.Slot), Is.EqualTo(new byte[] { 1, 4 })); // no stone, no sell-only drop
            rig.Binder.PickEntry(1);
            Assert.That(rig.Model.Chosen(0)!.Slot, Is.EqualTo(4));
            rig.Binder.Pick(1);
            Assert.That(rig.Model.Candidates(1).Select(e => e.Slot), Is.EqualTo(new byte[] { 1, 4 })); // one ash unit left
            rig.Binder.ClosePicker();
            Assert.That(rig.Model.PickerTarget, Is.EqualTo(ForgeWindowViewModel.PickNone));
            rig.Binder.Pick(1);
            rig.Binder.PickEntry(0);
            Assert.That(rig.Model.Chosen(1)!.Slot, Is.EqualTo(1));
            rig.Binder.ClearInput(1);
            Assert.That(rig.Model.Chosen(1), Is.Null);
        }

        [Test]
        public void An_assignment_follows_its_stack_and_drops_when_the_stack_goes()
        {
            var rig = new Rig();
            rig.HoldMolds("m3.moldSword");
            rig.Bag((1, "m3.ingotT1", 1), (4, "m1.demonAsh", 2));
            rig.OpenAtForge();
            rig.DropSwordMold();
            rig.Binder.DropOnInput(0, Normal, rig.Entry(4, "m1.demonAsh", 2));
            rig.Binder.DropOnInput(1, Normal, rig.Entry(4, "m1.demonAsh", 2));

            rig.Bag((1, "m3.ingotT1", 1), (4, "m1.demonAsh", 1)); // a unit went elsewhere: the later slot lets go
            Assert.That((rig.Model.Chosen(0)?.Slot, rig.Model.Chosen(1)), Is.EqualTo(((byte?)4, (InventorySlotEntry?)null)));
            rig.Bag((1, "m3.ingotT1", 1), (4, "m3.oreT1", 5)); // another item in the slot
            Assert.That(rig.Model.Chosen(0), Is.Null);

            rig.Molds = new InventorySlotEntry[0];
            rig.Bag((1, "m3.ingotT1", 1)); // the mold left the Safe bag's equipment area
            Assert.That(rig.Model.Mold, Is.Null);
        }

        [Test]
        public void The_recipes_tab_keeps_the_list_and_next()
        {
            var rig = new Rig();
            rig.Bag((0, "m3.oreT1", 2), (1, "m3.oreT1", 4));
            rig.OpenAtForge();
            rig.Binder.ShowTab(ForgeTab.Recipes);
            Assert.That(rig.Model.Tab, Is.EqualTo(ForgeTab.Recipes));
            rig.Binder.Choose(rig.Row("m3.refine.ingotT1"));
            Assert.That(rig.Model.Chosen(0)!.Slot, Is.EqualTo(0)); // picked from the bag, as the WP-M3-6 list did
            rig.Binder.NextInput(0);
            Assert.That(rig.Model.Chosen(0)!.Slot, Is.EqualTo(1));
            Assert.That(rig.Model.CanStart, Is.True);
            rig.Binder.StartForge();
            Assert.That(rig.Requests.Sent.Single(), Does.StartWith($"start {Station} Recipe "));
            Assert.That(rig.Requests.LastInputs.Select(i => (i.Container, i.Slot)), Is.EqualTo(new[] { (Normal, (byte)1) }));

            rig.State(1, ForgePhase.Running, rig.Model.Recipe!.NetId, kind: ForgeKind.Recipe);
            rig.Binder.ShowTab(ForgeTab.Forge); // a session keeps its own tab
            Assert.That(rig.Model.Tab, Is.EqualTo(ForgeTab.Recipes));
        }

        [Test]
        public void A_session_takes_actions_finishes_once_and_toasts_the_committed_item()
        {
            var rig = new Rig();
            rig.HoldMolds("m3.moldSword");
            rig.Bag((0, "m3.ingotT1", 2));
            rig.OpenAtForge();
            rig.DropSwordMold();
            rig.Binder.DropOnInput(0, Normal, rig.Entry(0, "m3.ingotT1", 2));
            rig.Binder.DropOnInput(1, Normal, rig.Entry(0, "m3.ingotT1", 2));
            rig.Binder.StartForge();
            rig.State(1, ForgePhase.Running, 1);
            Assert.That(rig.Model.InSession, Is.True);

            rig.Binder.UseAction(1); // hit
            rig.Binder.UseAction(1); // waits for the first action's answer
            Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("action 2"));
            rig.State(2, ForgePhase.Running, 1, new byte[] { 2 });
            rig.Binder.FinishForge();
            rig.Binder.FinishForge();
            Assert.That(rig.Requests.Sent.Count(s => s == "finish"), Is.EqualTo(1));

            rig.Feed.RaiseForgeAnswered(new ForgeResult
            {
                RequestId = 3, Reason = ForgeReason.None, Kind = ForgeKind.Mold, DefNetId = 1, Count = 1, Success = true,
                OutputItemNetId = Item(rig.Set, "m3.forgedSword"), Quality = 180,
            });
            Assert.That((rig.Toast.Visible, rig.Toast.TextId, rig.Toast.ArgValue), Is.EqualTo((true, ForgeTextIds.ToastMade, "Forged Sword")));
        }

        [Test]
        public void Refusals_and_the_servers_cancel_toast_their_reason_and_leaving_the_station_closes_the_window()
        {
            var rig = new Rig();
            rig.Bag((1, "m3.oreT1", 4));
            rig.OpenAtForge();
            rig.Binder.ShowTab(ForgeTab.Recipes);
            rig.Binder.Choose(rig.Row("m3.refine.ingotT1"));
            rig.Binder.StartForge();
            rig.State(1, ForgePhase.None, 0, reason: ForgeReason.JobRequired);
            Assert.That((rig.Toast.Visible, rig.Toast.ReasonCode), Is.EqualTo((true, "JobRequired")));

            rig.Feed.RaiseForgeState(new ForgeState { Version = 5, Phase = ForgePhase.Cancelled, Reason = ForgeReason.LeftStation });
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("LeftStation"));

            rig.Binder.Frame(0); // the station is not the NPC in reach (none is)
            Assert.That(rig.Model.Open, Is.False);
        }

        [Test]
        public void The_station_opens_the_inventory_window_and_closes_it_only_if_it_opened_it()
        {
            var rig = new Rig();
            bool inventoryOpen = false;
            rig.Binder.InventoryOpen = () => inventoryOpen;
            rig.Binder.SetInventoryOpen = open => inventoryOpen = open;

            rig.OpenAtForge();
            Assert.That(inventoryOpen, Is.True, "the bags are there to drag from");
            rig.Binder.CloseForge();
            Assert.That(inventoryOpen, Is.False, "the forge opened it, the forge closes it");

            inventoryOpen = true; // the player had it open
            rig.OpenAtForge();
            rig.Binder.CloseForge();
            Assert.That(inventoryOpen, Is.True);

            inventoryOpen = false;
            rig.OpenAtForge();
            rig.Binder.Frame(0); // leaving the station closes both
            Assert.That((rig.Model.Open, inventoryOpen), Is.EqualTo((false, false)));
        }

        [Test]
        public void The_window_shows_the_slots_the_stat_ranges_and_the_quality_range()
        {
            var rig = new Rig();
            var presenter = new ForgeWindowPresenter(rig.Hud, new HudView(rig.Hud), rig.Config, rig.Model, rig.Binder);
            presenter.Bind();
            Assert.That(presenter.IsShown, Is.False);

            rig.HoldMolds("m3.moldSword");
            rig.Bag((4, "m1.demonAsh", 2));
            rig.OpenAtForge();
            Assert.That(presenter.IsShown, Is.True);
            Assert.That(presenter.MoldSlot.text, Is.EqualTo("Mold: drag a mold here or click to choose"));
            rig.DropSwordMold();
            Assert.That(presenter.MoldSlot.text, Is.EqualTo("Mold: Sword Mold"));
            Assert.That(presenter.SlotButton(0).text, Is.EqualTo("Material 1: drag a material here or click to choose"));
            Assert.That(presenter.StatsText, Is.Empty, "no preview until every slot has a material");
            Assert.That(presenter.StartButton.enabledSelf, Is.False);

            var ash = rig.Entry(4, "m1.demonAsh", 2);
            rig.Binder.DropOnInput(0, Normal, ash);
            rig.Binder.DropOnInput(1, Normal, ash);
            Assert.That(presenter.SlotButton(1).text, Is.EqualTo("Material 2: Demon Ash x1"));
            Assert.That(presenter.StartButton.enabledSelf, Is.True);
            // What the two ash can reach (maxHp 8..12 and atk 1..2 a unit, placed by the quality, the band's bonus on top).
            var rules = rig.Config.Rules!;
            var ashDef = rules.Item("m1.demonAsh")!;
            rules.ReachRange(rig.Model.Mold!, new[] { ashDef, ashDef }, out int min, out int max);
            var ranges = rules.StatRange(rig.Model.Mold!, new[] { ashDef, ashDef }, min, max);
            Assert.That(ranges.Select(r => r.StatId), Is.EqualTo(new[] { "maxHp", "atk" }));
            var lines = presenter.StatsText.Split('\n');
            Assert.That(lines, Is.EqualTo(new[]
            {
                $"Max HP {ranges[0].FlatLow} to {ranges[0].FlatHigh}", $"Atk {ranges[1].FlatLow} to {ranges[1].FlatHigh}",
            }));
            Assert.That(ranges[0].FlatLow, Is.LessThan(ranges[0].FlatHigh), "a range, never a rolled number");

            rig.Binder.Pick(0);
            Assert.That(presenter.PickerShown, Is.True);
            Assert.That(presenter.PickerCount, Is.EqualTo(1)); // the ash stack: its unit for this slot (the other slot keeps its own)
            Assert.That(presenter.PickerEntry(0).text, Is.EqualTo("Demon Ash x2"));
            rig.Binder.ClosePicker();

            rig.Binder.StartForge();
            rig.State(1, ForgePhase.Running, 1, new byte[] { 1 });
            Assert.That(presenter.SessionShown, Is.True);
            Assert.That(presenter.ActionsText, Is.EqualTo("Actions 1/5"));
            Assert.That(presenter.QualityText, Is.EqualTo("Quality 50 to 300 (Common to Uncommon)"));
            Assert.That(presenter.ActionButton(1).text, Is.EqualTo("Hit"));
            var session = rules.StatRange(rig.Model.Mold!, new[] { ashDef, ashDef }, 50, 300);
            Assert.That(presenter.StatsText.Split('\n')[0], Is.EqualTo($"Max HP {session[0].FlatLow} to {session[0].FlatHigh}"));
            presenter.Dispose();
        }

        [Test]
        public void During_a_session_the_slots_show_the_sessions_reserved_inputs()
        {
            // Wave 1 QA F2 (low): the session holds its inputs reserved, so a free-units check would read "no material".
            var rig = new Rig();
            var presenter = new ForgeWindowPresenter(rig.Hud, new HudView(rig.Hud), rig.Config, rig.Model, rig.Binder);
            presenter.Bind();
            rig.HoldMolds("m3.moldSword");
            rig.Bag((0, "m3.ingotT1", 2), (2, "m1.cyclopsHide", 3));
            rig.OpenAtForge();
            rig.DropSwordMold();
            rig.Binder.DropOnInput(0, Normal, rig.Entry(0, "m3.ingotT1", 2));
            rig.Binder.DropOnInput(1, Normal, rig.Entry(2, "m1.cyclopsHide", 3));
            rig.Binder.StartForge();
            rig.Bag((0, "m3.ingotT1", 2, true), (2, "m1.cyclopsHide", 3, true)); // the start reserved them (R-5.3-03)
            rig.State(1, ForgePhase.Running, 1);

            Assert.That((presenter.SlotButton(0).text, presenter.SlotButton(1).text),
                Is.EqualTo(("Material 1: Copper Ingot x1", "Material 2: Cyclops Hide x1")));
            Assert.That(presenter.SlotButton(0).enabledSelf, Is.False);

            rig.State(2, ForgePhase.Cancelled, 0); // the session ended: the slots show the player's choice
            rig.Bag((0, "m3.ingotT1", 2), (2, "m1.cyclopsHide", 3));
            Assert.That(presenter.SlotButton(1).text, Is.EqualTo("Material 2: Cyclops Hide x1"));
            rig.Bag((0, "m3.ingotT1", 2));
            Assert.That(presenter.SlotButton(1).text, Is.EqualTo("Material 2: drag a material here or click to choose"));
            presenter.Dispose();
        }

        [UnityTest]
        public System.Collections.IEnumerator The_slots_take_drops_at_their_place_and_the_session_controls_fit_on_a_real_panel()
        {
            // Wave 1 QA F2 and WP-M3-14: at the reference 1280 x 720 the Forge tab's slots, Start and the session (actions, the
            // quality and stat lines, the timing bar, Finish and Cancel) sit inside the window's HudLayout row; the Recipes
            // tab's list scrolls above its inputs; a drop at a slot's place is that slot's.
            var rig = new Rig();
            var panel = new HudTestPanel("forge-layout-test");
            ForgeWindowPresenter? presenter = null;
            ForgeBarPresenter? bar = null;
            try
            {
                yield return null;
                panel.Ready(rig.Hud);
                var view = new HudView(rig.Hud);
                panel.Root.Add(view.Root);
                var enchant = new EnchantPanel(rig.Hud.Text, rig.Hud.Theme, new EnchantPanelViewModel(Shared.Simulation.Crafting.EnchantRules.From(rig.Set)),
                    new NoEnchantActions());
                presenter = new ForgeWindowPresenter(rig.Hud, view, rig.Config, rig.Model, rig.Binder, enchant);
                presenter.Bind();
                var barModel = new ForgeBarModel();
                bar = new ForgeBarPresenter(rig.Hud, view, barModel);
                bar.Bind();
                presenter.SessionPanel.Add(bar.Root);
                var others = new[] { HudElementIds.Minimap, HudElementIds.RunTracker, HudElementIds.Chat, HudElementIds.InventoryWindow }
                    .Select(id => (Id: id, Box: view.Place(id, HudWidgets.Panel("layout-test-other", rig.Hud.Theme)))).ToList();
                rig.HoldMolds("m3.moldChest"); // the most material slots of the forge's molds
                rig.Bag((0, "m3.ingotT1", 2), (2, "m1.cyclopsHide", 1), (4, "m1.demonAsh", 1));
                rig.OpenAtForge();
                rig.Binder.DropOnMold(rig.Config.MoldContainer, rig.MoldEntry("m3.moldChest"));
                panel.Layout();

                var window = presenter.Window;
                Assert.That(rig.Hud.TryGetLayout(HudElementIds.ForgeWindow, out var row), Is.True);
                Assert.That((window.layout.width, window.layout.height), Is.EqualTo(((float)row.Size[0], (float)row.Size[1])),
                    "the window keeps the size of its HudLayout row");
                var inner = Inner(window);
                foreach (var tab in new VisualElement[] { presenter.ForgeTab!, presenter.RecipesTab, presenter.EnchantTab! })
                    AssertInside(tab.worldBound, inner, "a tab");

                // Drops at the slots' places (what the inventory window's drag ends with).
                presenter.DropAt(presenter.SlotButton(0).worldBound.center, Normal, rig.Entry(0, "m3.ingotT1", 2));
                presenter.DropAt(presenter.SlotButton(1).worldBound.center, Normal, rig.Entry(2, "m1.cyclopsHide", 1));
                Assert.That(presenter.DropAt(new Vector2(window.worldBound.xMax + 50, window.worldBound.center.y), Normal,
                    rig.Entry(4, "m1.demonAsh", 1)), Is.False, "outside the window: not the forge's");
                presenter.DropAt(presenter.SlotButton(2).worldBound.center, Normal, rig.Entry(4, "m1.demonAsh", 1));
                Assert.That(Enumerable.Range(0, 3).Select(i => rig.Model.Chosen(i)!.Slot), Is.EqualTo(new byte[] { 0, 2, 4 }));
                panel.Layout();
                for (int i = 0; i < 3; i++)
                    AssertInside(presenter.SlotButton(i).worldBound, presenter.ForgeViewport.worldBound, "slot " + i + " before a session");
                AssertInside(presenter.StartButton.worldBound, inner, "Start");

                // The tallest state: a chest session with its actions, the quality and stat lines, the bar and Finish/Cancel.
                rig.Binder.StartForge();
                rig.Bag((0, "m3.ingotT1", 2, true), (2, "m1.cyclopsHide", 1, true), (4, "m1.demonAsh", 1, true));
                rig.State(1, ForgePhase.Running, 2, new byte[] { 1 });
                barModel.SetState(new ForgeState
                {
                    Version = 2, Phase = ForgePhase.Running, Kind = ForgeKind.Mold, DefNetId = 2, ChallengeKind = ForgeChallengeKind.TimingBar,
                    ChallengeMax = 150, BarRound = 1, BarSpeed = 500, BarTarget = 500, BarHalfWidth = 100, BarDurationMs = 4_000, BarHitMax = 30,
                });
                Assert.That(presenter.SessionShown, Is.True);
                panel.Layout();
                var viewport = presenter.ForgeViewport.worldBound;
                AssertInside(viewport, inner, "the Forge tab");
                var below = new (string What, VisualElement Element)[]
                {
                    ("the session panel", presenter.SessionPanel), ("the timing bar", bar.Root), ("Finish", presenter.FinishButton),
                    ("Cancel", presenter.CancelButton), ("the first action", presenter.ActionButton(0)),
                };
                foreach (var (what, element) in below)
                {
                    var rect = element.worldBound;
                    AssertInside(rect, inner, what);
                    Assert.That(rect.yMin, Is.GreaterThanOrEqualTo(viewport.yMax - 0.5f), what + " starts below the Forge tab");
                }
                foreach (var (id, box) in others)
                    Assert.That(window.worldBound.Overlaps(box.worldBound), Is.False, $"the window at {window.worldBound} covers {id} at {box.worldBound}");
                panel.Click(presenter.FinishButton);
                Assert.That(rig.Requests.Sent.Last(), Is.EqualTo("finish"), "Finish takes the click");

                // The Recipes tab after the session: its list scrolls above the inputs.
                rig.State(3, ForgePhase.Cancelled, 0);
                rig.Binder.ShowTab(ForgeTab.Recipes);
                panel.Layout();
                Assert.That(presenter.RowCount, Is.GreaterThanOrEqualTo(5), "the forge's recipes");
                var list = presenter.ListViewport.worldBound;
                AssertInside(list, inner, "the list");
                Assert.That(presenter.InputBox.worldBound.yMin, Is.GreaterThanOrEqualTo(list.yMax - 0.5f), "the inputs below the list");
                int last = presenter.RowCount - 1;
                presenter.List.ScrollTo(presenter.Row(last));
                panel.Layout();
                AssertInside(presenter.Row(last).worldBound, presenter.ListViewport.worldBound, "the last row after scrolling");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                bar?.Dispose();
                presenter?.Dispose();
                panel.Dispose();
            }
        }

        private sealed class NoEnchantActions : IEnchantPanelActions
        {
            public void ChooseEnchant(int row) { }

            public void Enchant() { }

            public void ShowEnchantTab(bool active) { }
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

        [Test]
        public void Committed_ui_strings_cover_every_forge_text_id_reason_slot_action_and_prompt()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            var reasons = new HashSet<string>(set.TryGet(typeof(ReasonCodeStrings), out var codes) ? codes.Rows.Cast<ReasonCodeStrings>().Select(r => r.Code) : new string[0]);

            Assert.That(ForgeTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(System.Enum.GetNames(typeof(ForgeReason)).Where(n => n != "None" && !reasons.Contains(n)), Is.Empty);
            var rules = ForgeConfig.From(set).Rules!;
            foreach (var definition in rules.Definitions)
            {
                Assert.That(definition.Inputs.Where(i => i.NameId.Length > 0 && !ui.Contains(i.NameId)).Select(i => i.NameId), Is.Empty);
                Assert.That(definition.Actions.Where(a => !ui.Contains(a.NameId)).Select(a => a.NameId), Is.Empty);
            }
            Assert.That(ui.Contains(OutpostTextIds.PromptOf(NpcKind.Forge)) && ui.Contains(OutpostTextIds.PromptOf(NpcKind.JewelerBench)), Is.True);
        }
    }
}
