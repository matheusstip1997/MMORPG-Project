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
using Shared.Simulation.Crafting;
using Shared.Simulation.Jobs;

namespace Game.UI.Tests
{
    /// <summary>
    /// The enchant tab of the station window (spec 5.3 R-5.3-04, 9.13; WP-M3-8): a forge's yes lists the items of the held
    /// job that enchant there with their committed level, the stones held and the odds; an attempt goes out once and waits for
    /// its answer; the outcome and refusals toast; the window's tabs switch the panels; the committed texts.
    /// </summary>
    public sealed class EnchantPanelTests
    {
        private const uint Station = 77;
        private const long SwordId = 501;
        private const byte Safe = 2, Equipped = 5;

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static ushort Item(ConfigSet set, string id) =>
            set.TryGet(typeof(ItemDef), out var table) ? (ushort)table.Rows.Cast<ItemDef>().Single(i => i.Id == id).NetId : (ushort)0;

        private sealed class FakeRequests : IEnchantRequests
        {
            public List<string> Sent { get; } = new List<string>();

            public uint Enchant(uint stationEntityId, byte container, byte slot, long instanceId)
            {
                Sent.Add($"enchant {stationEntityId} {container}:{slot} {instanceId}");
                return (uint)Sent.Count;
            }
        }

        private sealed class Rig
        {
            public Rig()
            {
                Set = Tables();
                Hud = HudConfig.From(Set);
                Model = new EnchantPanelViewModel(EnchantRules.From(Set));
                var settings = PredictionSettings.From(Set);
                Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
                var catalog = ActionCatalog.From(Set);
                Feed = new WorldClientFeed(Set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                    null, null, settings.TickMs) { EnchantRequests = Requests };
                Binder = new EnchantBinder(Hud.Text, Model, Toast, 2_000, () => 0);
                Binder.Bind(Feed);
                var smith = JobDef.Find(Set, "blacksmith")!;
                Feed.RaiseJobs(new JobState { Version = 1, Jobs = new[] { new JobEntry { JobNetId = (byte)smith.NetId, Slot = JobSlot.Main, Level = 12 } } });
            }

            public ConfigSet Set { get; }
            public HudConfig Hud { get; }
            public WorldClientFeed Feed { get; }
            public FakeRequests Requests { get; } = new FakeRequests();
            public EnchantPanelViewModel Model { get; }
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public EnchantBinder Binder { get; }

            public void OpenAt(NpcInteraction kind) =>
                Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 1, NpcEntityId = Station, Kind = kind, Reason = OutpostReason.None });

            /// <summary>The forged sword equipped at <paramref name="level"/>, a copper ring in the Safe bag, the stones in the Normal bag.</summary>
            public void Contents(int level, ushort stones) =>
                Feed.RaiseInventory(new InventoryContents
                {
                    Revision = 1, SlotCount = 30,
                    Slots = stones > 0 ? new[] { new InventorySlotEntry { Slot = 3, ItemNetId = Item(Set, "m3.enchantStone"), Count = stones } } : new InventorySlotEntry[0],
                    Containers = new[]
                    {
                        new InventoryContainerEntry { Code = Safe, SlotCount = 20, Slots = new[]
                            { new InventorySlotEntry { Slot = 0, ItemNetId = Item(Set, "m2.copperRing"), Count = 1, InstanceId = 600 } } },
                        new InventoryContainerEntry { Code = Equipped, SlotCount = 8, Slots = new[]
                            { new InventorySlotEntry { Slot = 0, ItemNetId = Item(Set, "m3.forgedSword"), Count = 1, InstanceId = SwordId, Enchant = (byte)level } } },
                    },
                });
        }

        [Test]
        public void A_forges_yes_lists_the_items_of_the_held_job_with_their_level_stones_and_odds()
        {
            var rig = new Rig();
            rig.Contents(level: 2, stones: 3);
            rig.OpenAt(NpcInteraction.Forge);

            Assert.That(rig.Model.Active, Is.False); // the window opens on the forge tab
            Assert.That(rig.Model.Candidates.Select(c => c.InstanceId), Is.EqualTo(new[] { SwordId })); // the ring is the Jeweler's
            Assert.That((rig.Model.Chosen!.Value.Level, rig.Model.StonesHeld, rig.Model.CanEnchant), Is.EqualTo((2, 3, true)));
            Assert.That(rig.Model.Odds!.Level, Is.EqualTo(2));

            rig.OpenAt(NpcInteraction.JewelerBench);
            Assert.That(rig.Model.Candidates, Is.Empty); // a Blacksmith's sword enchants at the forge only
        }

        [Test]
        public void An_attempt_goes_out_once_and_its_outcome_toasts()
        {
            var rig = new Rig();
            rig.Contents(level: 2, stones: 3);
            rig.OpenAt(NpcInteraction.Forge);
            rig.Binder.ShowEnchantTab(true);

            rig.Binder.Enchant();
            rig.Binder.Enchant(); // one at a time
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { $"enchant {Station} {Equipped}:0 {SwordId}" }));
            Assert.That(rig.Model.CanEnchant, Is.False);

            rig.Feed.RaiseEnchantAnswered(new EnchantResult
            {
                RequestId = 1, Reason = EnchantReason.None, InstanceId = SwordId, ItemNetId = Item(rig.Set, "m3.forgedSword"), FromLevel = 2,
                ToLevel = 3, StonesUsed = 1,
            });
            Assert.That((rig.Toast.Visible, rig.Toast.TextId, rig.Toast.ArgValue, rig.Toast.Arg2Value),
                Is.EqualTo((true, EnchantTextIds.ToastUp, "Forged Sword", "3")));
            Assert.That(rig.Model.PendingRequest, Is.EqualTo(0u));

            rig.Binder.Enchant();
            rig.Feed.RaiseEnchantAnswered(new EnchantResult { RequestId = 2, Reason = EnchantReason.ItemReserved });
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("ItemReserved"));
        }

        [Test]
        public void Without_stones_or_at_plus_nine_the_button_is_off()
        {
            var rig = new Rig();
            rig.Contents(level: 2, stones: 0);
            rig.OpenAt(NpcInteraction.Forge);
            Assert.That(rig.Model.CanEnchant, Is.False);
            rig.Contents(level: 9, stones: 5);
            Assert.That((rig.Model.Chosen!.Value.Maxed, rig.Model.CanEnchant), Is.EqualTo((true, false)));
        }

        [Test]
        public void The_window_tabs_switch_between_the_forge_and_the_enchant_panel()
        {
            var rig = new Rig();
            var forge = ForgeConfig.From(rig.Set);
            var forgeModel = new ForgeWindowViewModel(forge.Rules);
            var forgeBinder = new ForgeBinder(forge, rig.Hud.Text, forgeModel, rig.Toast, 2_000, () => 0);
            forgeBinder.Bind(rig.Feed);
            var panel = new EnchantPanel(rig.Hud.Text, rig.Hud.Theme, rig.Model, rig.Binder);
            var presenter = new ForgeWindowPresenter(rig.Hud, new HudView(rig.Hud), forge, forgeModel, forgeBinder, panel);
            presenter.Bind();
            rig.Contents(level: 2, stones: 3);
            rig.OpenAt(NpcInteraction.Forge);

            Assert.That((presenter.IsShown, panel.IsShown), Is.EqualTo((true, false)));
            Assert.That(presenter.EnchantTab!.text, Is.EqualTo("Enchant"));
            rig.Binder.ShowEnchantTab(true);
            Assert.That(panel.IsShown, Is.True);
            Assert.That(panel.Row(0).text, Is.EqualTo("Forged Sword +2"));
            Assert.That(panel.StonesText, Is.EqualTo("Enchant Stone: 3 held, 1 per try"));
            Assert.That(panel.OddsText, Does.StartWith("+1 "));
            Assert.That(panel.EnchantButton.enabledSelf, Is.True);
            rig.Binder.ShowEnchantTab(false);
            Assert.That(panel.IsShown, Is.False);
        }

        [Test]
        public void Committed_ui_strings_cover_every_enchant_text_id_and_reason()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            var reasons = new HashSet<string>(set.TryGet(typeof(ReasonCodeStrings), out var codes) ? codes.Rows.Cast<ReasonCodeStrings>().Select(r => r.Code) : new string[0]);

            Assert.That(EnchantTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(System.Enum.GetNames(typeof(EnchantReason)).Where(n => n != "None" && !reasons.Contains(n)), Is.Empty);
        }
    }
}
