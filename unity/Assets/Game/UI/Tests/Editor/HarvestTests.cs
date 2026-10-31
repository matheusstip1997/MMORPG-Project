#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Outpost;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.UI.Tests
{
    /// <summary>
    /// Harvesting on the client (spec 5.2, 9.3, 9.13; WP-M3-5): <see cref="ClientHarvest"/>'s check (job, tool type and tier in
    /// the Safe bag's equipment area, a harvest waiting), request and answer; the HUD's <see cref="HarvestBinder"/>: the
    /// interaction key harvests the node in reach only when no NPC is, the prompt names the tool's verb and hides during an
    /// action, refusals and committed harvests toast; every node type's prompt has a <c>UiStrings</c> row.
    /// </summary>
    public sealed class HarvestTests
    {
        private const ushort Pickaxe = 501, Ore = 401; // m3.pickaxeT1, m3.oreT1
        private const byte Safe = 2;

        private sealed class OnlyBlacksmith : IOwnJobs
        {
            public bool Held { get; set; } = true;
            public bool Has(string jobId) => Held && jobId == "blacksmith";
            public int Version => 1;
        }

        private sealed class FakeHarvests : IHarvestRequests
        {
            public List<uint> Sent { get; } = new List<uint>();
            public HarvestReason Answer { get; set; }

            public HarvestReason RequestHarvest(uint nodeEntityId, out uint requestId)
            {
                requestId = 0;
                if (Answer != HarvestReason.None)
                    return Answer;
                Sent.Add(nodeEntityId);
                requestId = (uint)Sent.Count;
                return HarvestReason.None;
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
                null, null, settings.TickMs);
        }

        private static InventoryContents WithTool(ushort tool) => new InventoryContents
        {
            Revision = 1, SlotCount = 30, Slots = new InventorySlotEntry[0],
            Containers = new[]
            {
                new InventoryContainerEntry
                {
                    Code = Safe, SlotCount = 20,
                    Slots = tool == 0 ? new InventorySlotEntry[0] : new[] { new InventorySlotEntry { Slot = 0, ItemNetId = tool, Count = 1 } },
                },
            },
        };

        private static IEnumerable<T> Rows<T>(ConfigSet set) =>
            set.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>() : Enumerable.Empty<T>();

        private static NodeDef Node(ConfigSet set, string id) =>
            Rows<NodeDef>(set).Single(n => n.Id == id);

        [Test]
        public void The_check_needs_the_job_a_tool_of_the_type_and_its_tier_and_no_harvest_waiting()
        {
            var set = Tables();
            var harvest = new ClientHarvest(set);
            var jobs = new OnlyBlacksmith();
            var t1 = Node(set, "node.ore.t1");
            var t3 = Node(set, "node.ore.t3");

            Assert.That(harvest.BestToolTier(WithTool(Pickaxe), ToolType.Pickaxe), Is.EqualTo(1));
            Assert.That(harvest.Check(t1, jobs, WithTool(Pickaxe), 0), Is.EqualTo(HarvestReason.None));
            Assert.That(harvest.Check(t3, jobs, WithTool(Pickaxe), 0), Is.EqualTo(HarvestReason.ToolTierTooLow));
            Assert.That(harvest.Check(t1, jobs, WithTool(0), 0), Is.EqualTo(HarvestReason.ToolRequired));
            Assert.That(harvest.Check(t1, jobs, WithTool(Ore), 0), Is.EqualTo(HarvestReason.ToolRequired)); // not a tool
            jobs.Held = false;
            Assert.That(harvest.Check(t1, jobs, WithTool(Pickaxe), 0), Is.EqualTo(HarvestReason.JobRequired));
            jobs.Held = true;

            Assert.That(ReliableMessages.TryRead(harvest.Request(7, 1_000, out uint id), out HarvestRequest request), Is.True);
            Assert.That((request.RequestId, request.NodeEntityId, id), Is.EqualTo((1u, 7u, 1u)));
            Assert.That(harvest.Check(t1, jobs, WithTool(Pickaxe), 1_000), Is.EqualTo(HarvestReason.ActionBusy)); // waiting
            Assert.That(harvest.IsWaiting(1_000 + ClientHarvest.PendingTimeoutMs), Is.False);

            Assert.That(harvest.Apply(ReliableMessages.Write(ReliableMessageId.HarvestResult,
                new HarvestResult { RequestId = 1, NodeEntityId = 7, ItemNetId = Ore, Count = 2 }), out _), Is.True);
            Assert.That(harvest.IsWaiting(1_000), Is.False);
            Assert.That(harvest.LastResult!.Count, Is.EqualTo(2));
            harvest.Reset();
            Assert.That((harvest.RequestsSent, harvest.LastResult), Is.EqualTo((0u, (HarvestResult?)null)));
        }

        [Test]
        public void The_interact_key_harvests_the_node_in_reach_only_when_no_npc_is()
        {
            var set = Tables();
            var feed = Feed(set);
            var harvests = new FakeHarvests();
            feed.HarvestRequests = harvests;
            var prompt = new NpcPromptViewModel();
            var toast = new ToastViewModel();
            var binder = new HarvestBinder(set, InventoryConfig.From(set), HudConfig.From(set).Text, prompt, toast, 1500, () => 0,
                readInteractKey: false);
            binder.Bind(feed);
            var t1 = Node(set, "node.ore.t1");

            binder.Harvest(); // no node in reach: nothing
            feed.SetNodeTarget(42, (ushort)t1.NetId);
            binder.Frame(0);
            Assert.That((prompt.Visible, prompt.NpcEntityId, prompt.TextId), Is.EqualTo((true, 42u, "hud.node.prompt.pickaxe")));
            binder.Harvest();
            Assert.That(harvests.Sent, Is.EqualTo(new[] { 42u }));

            feed.SetNpcTarget(9, 1); // an NPC in reach keeps the key (the outpost binder talks to it)
            binder.Frame(0);
            binder.Harvest();
            Assert.That(harvests.Sent.Count, Is.EqualTo(1));
            Assert.That(prompt.Visible, Is.False);
            feed.SetNpcTarget(0, 0);

            harvests.Answer = HarvestReason.ToolRequired; // the client's check
            binder.Harvest();
            Assert.That((toast.Visible, toast.ReasonCode), Is.EqualTo((true, nameof(HarvestReason.ToolRequired))));
            binder.Dispose();
        }

        [Test]
        public void Refusals_and_committed_harvests_toast()
        {
            var set = Tables();
            var feed = Feed(set);
            var text = HudConfig.From(set).Text;
            var toast = new ToastViewModel();
            var binder = new HarvestBinder(set, InventoryConfig.From(set), text, new NpcPromptViewModel(), toast, 1500, () => 0,
                readInteractKey: false);
            binder.Bind(feed);

            feed.RaiseHarvestAnswered(new HarvestResult { RequestId = 1, Reason = HarvestReason.HarvestInterrupted });
            Assert.That(toast.ReasonCode, Is.EqualTo(nameof(HarvestReason.HarvestInterrupted)));

            feed.RaiseHarvestAnswered(new HarvestResult { RequestId = 2, ItemNetId = Ore, Count = 3 });
            Assert.That(toast.TextId, Is.EqualTo(HudTextIds.ToastHarvested));
            string ore = text.Get(Rows<ItemDef>(set).Single(i => i.NetId == Ore).NameId);
            Assert.That(text.Get(toast.TextId, new TextArg(toast.ArgName, toast.ArgValue), new TextArg(toast.Arg2Name, toast.Arg2Value)),
                Is.EqualTo("+3 " + ore));
            binder.Dispose();
        }

        [Test]
        public void Every_node_types_prompt_and_every_harvest_reason_has_a_text()
        {
            var set = Tables();
            var text = HudConfig.From(set).Text;
            foreach (var node in Rows<NodeDef>(set))
            {
                string id = HarvestBinder.PromptOf(node.ToolType);
                Assert.That(text.Has(id), Is.True, id);
                Assert.That(text.Has(UiStringCoverage.ActionId(node.ActionId)), Is.True, node.ActionId); // the cast bar's name
            }
        }
    }
}
