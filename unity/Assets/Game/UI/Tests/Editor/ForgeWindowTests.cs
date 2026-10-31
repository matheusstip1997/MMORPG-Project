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

namespace Game.UI.Tests
{
    /// <summary>
    /// The forge window (spec 5.3, 9.13; WP-M3-6): a station's yes opens it with that station's molds and recipes, the input
    /// picker takes bag stacks by the slot's tags and tier, a start, actions and a finish go out one at a time, refusals and
    /// results toast, leaving the station closes it; the presenter's rows, inputs, actions and quality range; the committed texts.
    /// </summary>
    public sealed class ForgeWindowTests
    {
        private const uint Station = 77;

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

            public void OpenAtForge() =>
                Feed.RaiseOutpostInteracted(new OutpostInteractResult { RequestId = 1, NpcEntityId = Station, Kind = NpcInteraction.Forge, Reason = OutpostReason.None });

            public void Bag(params (byte Slot, string Item, ushort Count)[] stacks) =>
                Feed.RaiseInventory(new InventoryContents
                {
                    Revision = 1, SlotCount = 30,
                    Slots = stacks.Select(s => new InventorySlotEntry { Slot = s.Slot, ItemNetId = Item(Set, s.Item), Count = s.Count }).ToArray(),
                });

            public int Row(string id) => Model.Definitions.ToList().FindIndex(d => d.Id == id);

            public void State(uint requestId, ForgePhase phase, int defNetId, byte[]? actions = null, ForgeReason reason = ForgeReason.None) =>
                Feed.RaiseForgeState(new ForgeState
                {
                    Version = requestId, RequestId = requestId, Reason = reason, Phase = phase, Kind = ForgeKind.Mold, DefNetId = (ushort)defNetId,
                    StationEntityId = Station, Actions = actions ?? new byte[0], MaxActions = 5, QualityMin = 50, QualityMax = 300,
                    RarityMin = 0, RarityMax = 1,
                });
        }

        [Test]
        public void A_forges_yes_lists_its_molds_and_recipes_and_the_picker_fills_the_inputs_from_the_bag()
        {
            var rig = new Rig();
            rig.Bag((0, "m3.ingotT1", 3), (1, "m3.oreT1", 4), (2, "m1.cyclopsHide", 1));
            rig.OpenAtForge();

            Assert.That((rig.Model.Open, rig.Model.Station), Is.EqualTo((true, NpcInteraction.Forge)));
            Assert.That(rig.Model.Definitions.All(d => d.Station == NpcInteraction.Forge), Is.True); // no Jeweler mold here
            Assert.That(rig.Row("m3.moldRing"), Is.EqualTo(-1));
            rig.Binder.Choose(rig.Row("m3.moldSword"));
            Assert.That(rig.Model.Chosen(0)!.Slot, Is.EqualTo(0)); // the blade takes the ingots
            Assert.That(rig.Model.Chosen(1)!.Slot, Is.EqualTo(2)); // the grip the hide
            Assert.That(rig.Model.CanStart, Is.True);

            rig.Binder.StartForge();
            rig.Binder.StartForge(); // one at a time
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { $"start {Station} Mold 1" }));
            Assert.That(rig.Requests.LastInputs.Select(i => (i.Container, i.Slot)), Is.EqualTo(new[] { ((byte)1, (byte)0), ((byte)1, (byte)2) }));
        }

        [Test]
        public void A_session_takes_actions_finishes_once_and_toasts_the_committed_item()
        {
            var rig = new Rig();
            rig.Bag((0, "m3.ingotT1", 2), (2, "m1.cyclopsHide", 1));
            rig.OpenAtForge();
            rig.Binder.Choose(rig.Row("m3.moldSword"));
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
        public void The_window_shows_the_rows_inputs_actions_and_the_quality_range()
        {
            var rig = new Rig();
            var presenter = new ForgeWindowPresenter(rig.Hud, new HudView(rig.Hud), rig.Config, rig.Model, rig.Binder);
            presenter.Bind();
            Assert.That(presenter.IsShown, Is.False);

            rig.Bag((0, "m3.ingotT1", 2));
            rig.OpenAtForge();
            Assert.That(presenter.IsShown, Is.True);
            int sword = rig.Row("m3.moldSword");
            Assert.That(presenter.Row(sword).text, Does.StartWith("Forged Sword"));
            rig.Binder.Choose(sword);
            Assert.That(presenter.InputText(0), Is.EqualTo("Blade: Copper Ingot x2"));
            Assert.That(presenter.InputText(1), Is.EqualTo("Grip: no material (needs 1)"));
            Assert.That(presenter.StartButton.enabledSelf, Is.False);

            rig.State(1, ForgePhase.Running, 1, new byte[] { 1 });
            Assert.That(presenter.SessionShown, Is.True);
            Assert.That(presenter.ActionsText, Is.EqualTo("Actions 1/5"));
            Assert.That(presenter.QualityText, Is.EqualTo("Quality 50 to 300 (Common to Uncommon)"));
            Assert.That(presenter.ActionButton(1).text, Is.EqualTo("Hit"));
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
