using BotClient;
using BotClient.Brain;
using BotClient.Loop;
using BotClient.Loop.Steps;
using BotClient.World;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Jobs;
using Shared.Util;

namespace GameServer.Tests.Bots;

/// <summary>
/// The loop's gather step (WP-M3-5, spec 5.2, 11.5; contract in Loop/Steps/README.md) against a step context over the
/// repository's tables, a ring 1 ore node in the client's entity registry (a snapshot as the server writes it), the jobs
/// and the Safe bag as the server sends them: it harvests the node of its job with a tool held, plays the harvest action,
/// waits for the committed answer and counts it; refusals send it on or fail it; with no job or no tool it is done at once.
/// </summary>
public class GatherStepTests
{
    private const int OwnId = 1, NodeId = 2;
    private const ushort OreT1 = 1; // NodeDef node.ore.t1
    private const byte BlacksmithNetId = 1;
    private const string Ring1 = "field1.node.ring1.a";

    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    [Fact]
    public void A_blacksmith_with_a_pickaxe_harvests_the_node_in_reach_and_counts_the_committed_yield()
    {
        var bot = new Bot();
        bot.Blacksmith();
        bot.Pickaxe();
        bot.Node(offsetCm: 100);
        var step = new GatherStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var ask = bot.Sent(0);
        Assert.Equal((uint)NodeId, ask.NodeEntityId);
        Assert.Equal(["harvest.ore.t1"], bot.Played); // the channel plays in the predictor (the cast bar)
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the answer comes after the commit
        Assert.Equal(1, bot.SentCount);

        bot.Answer(ask, HarvestReason.None, count: 2);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal((1L, 2L), (bot.Counters.Loop["gather.harvested"], bot.Counters.Loop["gather.units"]));
    }

    [Fact]
    public void The_argument_is_the_number_of_harvests()
    {
        var bot = new Bot();
        bot.Blacksmith();
        bot.Pickaxe();
        bot.Node(offsetCm: 100);
        var step = new GatherStep("2");
        step.Begin(bot.Context);

        bot.Tick(step);
        bot.Answer(bot.Sent(0), HarvestReason.None, count: 1);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // one more to go
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the next request
        Assert.Equal(2, bot.SentCount);
        bot.Answer(bot.Sent(1), HarvestReason.None, count: 3);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal((2L, 4L), (bot.Counters.Loop["gather.harvested"], bot.Counters.Loop["gather.units"]));
    }

    [Fact]
    public void A_node_out_of_reach_is_walked_to_first()
    {
        var bot = new Bot();
        bot.Blacksmith();
        bot.Pickaxe();
        bot.Node(offsetCm: 2_000);
        var step = new GatherStep();
        step.Begin(bot.Context);

        var command = bot.Context.Brain.Hold();
        Assert.Equal(BotStepStatus.Running, step.Tick(bot.Context, ref command));
        Assert.Equal(0, bot.SentCount);
        Assert.NotEqual(bot.Context.Brain.Hold().Keys, command.Keys); // walking toward it
    }

    [Fact]
    public void A_depleted_node_or_a_broken_channel_is_tried_again_and_another_refusal_fails_the_step()
    {
        var bot = new Bot();
        bot.Blacksmith();
        bot.Pickaxe();
        bot.Node(offsetCm: 100);
        var step = new GatherStep();
        step.Begin(bot.Context);

        bot.Tick(step);
        bot.Answer(bot.Sent(0), HarvestReason.HarvestInterrupted);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["gather.refused.HarvestInterrupted"]);

        bot.Tick(step); // tries again
        bot.Answer(bot.Sent(1), HarvestReason.BagFull);
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["gather.refused.BagFull"]);
        Assert.Contains("BagFull", bot.Context.LastReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_job_or_without_a_tool_the_step_is_done_at_once()
    {
        var noJob = new Bot();
        noJob.Pickaxe();
        noJob.Node(offsetCm: 100);
        var step = new GatherStep();
        step.Begin(noJob.Context);
        Assert.Equal(BotStepStatus.Done, noJob.Tick(step));
        Assert.Equal(1, noJob.Counters.Loop["gather.noJob"]);

        var noTool = new Bot();
        noTool.Blacksmith();
        noTool.Node(offsetCm: 100);
        step.Begin(noTool.Context);
        Assert.Equal(BotStepStatus.Done, noTool.Tick(step));
        Assert.Equal(1, noTool.Counters.Loop["gather.noTool"]);
        Assert.Equal(0, noTool.SentCount);
    }

    [Fact]
    public void A_node_type_argument_harvests_only_that_type()
    {
        var bot = new Bot();
        bot.Blacksmith();
        bot.Pickaxe(); // a T1 pickaxe: the gem vein (minimum tool tier 2) is out of its reach
        bot.Node(offsetCm: 100);
        var step = new GatherStep("node.gemVein");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["gather.noTool"]);
    }

    /// <summary>A step context at a ring 1 node marker with nothing connected; requests are kept, answers applied as the world client does.</summary>
    private sealed class Bot
    {
        private readonly List<byte[]> _sent = [];

        public Bot()
        {
            var data = Data.Value;
            var position = data.Markers[Ring1];
            var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), data.Behaviour("m3-loop"), data.Grid, position, 200,
                new Dictionary<int, int>(), new Pcg32Rng(1));
            Harvest = new ClientHarvest(data.Tables);
            Context = new BotStepContext(0, data, brain,
                new BotPerception { Self = position, Confirmed = position, NowMs = 1_000_000, ActionIdle = true },
                new EntityRegistry(PredictionSettings.From(data.Tables)), new ClientInventory(data.Tables), new BotOwnState(),
                new BotCounters(), BotStepRegistry.Default(), message => { _sent.Add(message); return true; }, _ => { })
            {
                ClassId = "warrior", Harvest = Harvest, Nodes = new ClientNodes(data.Tables), PlayAction = id => Played.Add(id),
            };
            Context.Jobs.Rules = JobRules.From(data.Tables); // as the world client sets it: JobDef ids for IOwnJobs.Has
            Assert.True(Context.Progression.Apply(ReliableMessages.Write(ReliableMessageId.ProgressionState,
                new ProgressionState { Level = 12, BandPercent = 100 }), out _));
        }

        public BotStepContext Context { get; }
        public ClientHarvest Harvest { get; }
        public BotCounters Counters => Context.Counters;
        public List<string> Played { get; } = [];
        public int SentCount => _sent.Count;

        public BotStepStatus Tick(IBotStep step)
        {
            var command = Context.Brain.Hold();
            return step.Tick(Context, ref command);
        }

        /// <summary>An available T1 ore node <paramref name="offsetCm"/> east of the bot, in the client's registry from a snapshot.</summary>
        public void Node(int offsetCm)
        {
            int x = (int)GridPath.Cm(Context.View.Self.X), y = (int)GridPath.Cm(Context.View.Self.Y);
            var buffer = new byte[512];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 100, OriginXCm = x, OriginZCm = y });
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = OwnId, Kind = EntityKind.Player, DefId = 1 }));
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = NodeId, Kind = EntityKind.ResourceNode, DefId = OreT1 }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = OwnId, XCm = x, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = NodeId, XCm = x + offsetCm, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            int size = writer.Finish();
            Assert.True(Context.Entities.ApplySnapshot(buffer.AsSpan(0, size), out _, out _, out _));
        }

        public void Blacksmith() =>
            Assert.True(Context.Jobs.Apply(ReliableMessages.Write(ReliableMessageId.JobState, new JobState
            {
                Version = 1, Jobs = [new JobEntry { JobNetId = BlacksmithNetId, Slot = JobSlot.Main, Level = 1, XpToNext = 100 }],
            }), out _));

        /// <summary>A T1 pickaxe in the Safe bag's equipment area (container code 2), as the committed contents carry it.</summary>
        public void Pickaxe()
        {
            var pickaxe = Data.Value.Rows<ItemDef>().Single(i => i.Id == "m3.pickaxeT1");
            Assert.True(Context.Inventory.Apply(ReliableMessages.Write(ReliableMessageId.InventoryContents, new InventoryContents
            {
                Revision = 1, SlotCount = 30, Slots = [],
                Containers = [new InventoryContainerEntry
                {
                    Code = 2, SlotCount = 20,
                    Slots = [new InventorySlotEntry { Slot = 0, ItemNetId = (ushort)pickaxe.NetId, Count = 1, InstanceId = 1 << 24 | 1 }],
                }],
            }), out _));
        }

        public void Answer(HarvestRequest ask, HarvestReason reason, ushort count = 0) =>
            Assert.True(Harvest.Apply(ReliableMessages.Write(ReliableMessageId.HarvestResult, new HarvestResult
            {
                RequestId = ask.RequestId, NodeEntityId = ask.NodeEntityId, ItemNetId = reason == HarvestReason.None ? (ushort)401 : (ushort)0,
                Count = count, Reason = reason,
            }), out _));

        public HarvestRequest Sent(int index)
        {
            Assert.True(ReliableMessages.TryRead(_sent[index], out HarvestRequest request));
            return request;
        }
    }
}
