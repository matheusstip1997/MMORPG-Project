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
    /// The gems panel (spec 5.3 R-5.3-05, 9.13; WP-M3-9): it opens with the forge window at a jeweler's bench and lists the gear
    /// with gem slots and the cut gems of the bag; a socket and a removal go out one at a time with the Jeweler's level checked
    /// like the server; refusals and outcomes toast; the presenter's rows, socketed gems, odds and buttons; the committed texts.
    /// </summary>
    public sealed class GemPanelTests
    {
        private const uint Bench = 78;
        private const long Sword = 123;

        private static ConfigSet Tables()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return set;
        }

        private static ushort Item(ConfigSet set, string id) =>
            set.TryGet(typeof(ItemDef), out var table) ? (ushort)table.Rows.Cast<ItemDef>().Single(i => i.Id == id).NetId : (ushort)0;

        private sealed class FakeGemRequests : IGemRequests
        {
            public List<string> Sent { get; } = new List<string>();

            public uint SocketGem(uint stationEntityId, in GemTarget target, byte gemSlot)
            {
                Sent.Add($"socket {stationEntityId} {target.Container}:{target.Entry.Slot} {target.Entry.InstanceId} gem {gemSlot}");
                return (uint)Sent.Count;
            }

            public uint RemoveGem(uint stationEntityId, in GemTarget target, byte gemIndex)
            {
                Sent.Add($"remove {stationEntityId} {target.Entry.InstanceId} {gemIndex}");
                return (uint)Sent.Count;
            }
        }

        private sealed class Rig
        {
            public Rig()
            {
                Set = Tables();
                Hud = HudConfig.From(Set);
                Config = GemConfig.From(Set);
                Forge = new ForgeWindowViewModel(ForgeConfig.From(Set).Rules);
                Model = new GemPanelViewModel(new ClientGems(Set));
                var settings = PredictionSettings.From(Set);
                Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
                var catalog = ActionCatalog.From(Set);
                Feed = new WorldClientFeed(Set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                    null, null, settings.TickMs) { GemRequests = Requests };
                Binder = new GemBinder(Config, Hud.Text, Model, Forge, Toast, 2_000, () => 0);
                Binder.Bind(Feed);
            }

            public ConfigSet Set { get; }
            public HudConfig Hud { get; }
            public GemConfig Config { get; }
            public ForgeWindowViewModel Forge { get; }
            public WorldClientFeed Feed { get; }
            public FakeGemRequests Requests { get; } = new FakeGemRequests();
            public GemPanelViewModel Model { get; }
            public ToastViewModel Toast { get; } = new ToastViewModel();
            public GemBinder Binder { get; }

            public void OpenAtBench()
            {
                var result = new OutpostInteractResult { RequestId = 1, NpcEntityId = Bench, Kind = NpcInteraction.JewelerBench, Reason = OutpostReason.None };
                Forge.OpenAt(Bench, NpcInteraction.JewelerBench);
                Feed.RaiseOutpostInteracted(result);
            }

            public void Jeweler(int level)
            {
                var jeweler = Set.TryGet(typeof(JobDef), out var jobs) ? jobs.Rows.Cast<JobDef>().Single(j => j.Id == "jeweler") : null;
                Feed.RaiseJobs(new JobState { Jobs = new[] { new JobEntry { JobNetId = (byte)jeweler!.NetId, Slot = Shared.Simulation.Jobs.JobSlot.Main, Level = (ushort)level } } });
            }

            /// <summary>A worn forged sword (its gems as item ids) and a Normal bag.</summary>
            public void Contents(string[] swordGems, params (byte Slot, string Item, ushort Count)[] bag) =>
                Feed.RaiseInventory(new InventoryContents
                {
                    Revision = 1, SlotCount = 30,
                    Slots = bag.Select(s => new InventorySlotEntry { Slot = s.Slot, ItemNetId = Item(Set, s.Item), Count = s.Count }).ToArray(),
                    Containers = new[]
                    {
                        new InventoryContainerEntry
                        {
                            Code = 5, SlotCount = 8,
                            Slots = new[]
                            {
                                new InventorySlotEntry
                                {
                                    Slot = 0, ItemNetId = Item(Set, "m3.forgedSword"), Count = 1, InstanceId = Sword,
                                    Gems = swordGems.Select(g => Item(Set, g)).ToArray(),
                                },
                            },
                        },
                    },
                });
        }

        [Test]
        public void The_panel_opens_at_a_bench_lists_worn_gear_and_cut_gems_and_sockets_one_at_a_time()
        {
            var rig = new Rig();
            rig.Jeweler(1);
            rig.Contents(new string[0], (0, "m3.roughGemT1", 2), (1, "m3.rubyT1", 1), (2, "m3.sapphireT1", 1));
            Assert.That(rig.Model.Open, Is.False);
            rig.OpenAtBench();
            Assert.That(rig.Model.Open, Is.True);
            Assert.That(rig.Model.Targets.Count, Is.EqualTo(1));
            Assert.That(rig.Model.GemCount, Is.EqualTo(2)); // the rough gem is not a cut gem
            Assert.That(rig.Model.CanSocket, Is.True);

            rig.Binder.Socket();
            rig.Binder.Socket(); // one at a time
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { $"socket {Bench} 5:0 {Sword} gem 1" }));

            rig.Feed.RaiseGemAnswered(new GemResult { RequestId = 1, Op = GemOp.Socket, Success = false, GemItemNetId = Item(rig.Set, "m3.rubyT1"), InstanceId = Sword });
            Assert.That(rig.Toast.TextId, Is.EqualTo(GemTextIds.ToastSocketFailed));
            Assert.That(rig.Model.Pending, Is.EqualTo(0u));

            rig.Binder.NextGem();
            rig.Binder.Socket();
            rig.Feed.RaiseGemAnswered(new GemResult { RequestId = 2, Op = GemOp.Socket, Reason = GemReason.NoGemSlot });
            Assert.That(rig.Toast.ReasonCode, Is.EqualTo("NoGemSlot"));
        }

        [Test]
        public void A_socket_needs_the_jeweler_and_its_tier_level_and_a_removal_its_cost()
        {
            var rig = new Rig();
            rig.Contents(new[] { "m3.rubyT1" }, (0, "m3.roughGemT1", 1), (1, "m3.rubyT2", 1));
            rig.OpenAtBench();
            Assert.That(rig.Model.CanSocket, Is.False); // the sword's one slot is taken
            Assert.That(rig.Model.CanRemove(0), Is.False); // no Jeweler
            rig.Jeweler(12);
            Assert.That(rig.Model.CanRemove(0), Is.False); // 2 rough gems needed, 1 held
            rig.Contents(new[] { "m3.rubyT1" }, (0, "m3.roughGemT1", 2));
            Assert.That(rig.Model.CanRemove(0), Is.True);
            rig.Binder.Remove(0);
            Assert.That(rig.Requests.Sent, Is.EqualTo(new[] { $"remove {Bench} {Sword} 0" }));
            rig.Feed.RaiseGemAnswered(new GemResult { RequestId = 1, Op = GemOp.Remove, Success = true, GemItemNetId = Item(rig.Set, "m3.rubyT1"), InstanceId = Sword, CostCount = 2 });
            Assert.That(rig.Toast.TextId, Is.EqualTo(GemTextIds.ToastRemoved));

            rig.Contents(new string[0], (0, "m3.rubyT2", 1));
            rig.Jeweler(4);
            Assert.That(rig.Model.CanSocket, Is.False); // a tier-2 gem needs Jeweler level 5
            rig.Jeweler(5);
            Assert.That(rig.Model.CanSocket, Is.True);
        }

        [Test]
        public void The_panel_shows_the_gear_its_gems_the_odds_and_closes_with_the_forge_window()
        {
            var rig = new Rig();
            rig.Jeweler(12);
            var presenter = new GemPanelPresenter(rig.Hud, new HudView(rig.Hud), rig.Config, rig.Model, rig.Binder);
            presenter.Bind();
            rig.Contents(new[] { "m3.rubyT1" }, (0, "m3.emeraldT1", 3), (1, "m3.roughGemT1", 2));
            Assert.That(presenter.IsShown, Is.False);
            rig.OpenAtBench();
            Assert.That(presenter.IsShown, Is.True);
            Assert.That(presenter.Row(0).text, Is.EqualTo("Forged Sword (gems 1/1)"));
            Assert.That(presenter.SocketedText(0), Does.StartWith("Cut Ruby: "));
            Assert.That(presenter.SocketedText(0), Does.Contain("+4"));
            Assert.That(presenter.RemoveButton(0).enabledSelf, Is.True);
            Assert.That(presenter.GemText, Is.EqualTo("Gem: Cut Emerald x3"));

            rig.Contents(new string[0], (0, "m3.emeraldT1", 3));
            Assert.That(presenter.OddsText, Does.StartWith("Success 80 %"));
            Assert.That(presenter.SocketButton.enabledSelf, Is.True);

            rig.Forge.Close();
            rig.Binder.Frame(0);
            Assert.That(presenter.IsShown, Is.False);
        }

        [Test]
        public void Committed_ui_strings_cover_every_gem_text_id_and_reason()
        {
            var set = Tables();
            var ui = new HashSet<string>(set.TryGet(typeof(UiStrings), out var table) ? table.Rows.Cast<UiStrings>().Select(r => r.Id) : new string[0]);
            var reasons = new HashSet<string>(set.TryGet(typeof(ReasonCodeStrings), out var codes) ? codes.Rows.Cast<ReasonCodeStrings>().Select(r => r.Code) : new string[0]);
            Assert.That(GemTextIds.All.Where(id => !ui.Contains(id)), Is.Empty);
            Assert.That(System.Enum.GetNames(typeof(GemReason)).Where(n => n != "None" && !reasons.Contains(n)), Is.Empty);
        }
    }
}
