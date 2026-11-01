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
/// The loop's socket step and the fight step's item goal (WP-M3-9, spec 5.3 R-5.3-05, 11.5) against a step context over the
/// repository's tables, the city's jeweler's bench in the client's entity registry and the server's answers applied to the kept
/// <c>ClientGems</c>, as the world client does: the step picks a cut gem and the worn gear with a free slot, sends one request,
/// waits for the committed answer and counts a set or a lost gem; nothing to do is done in a loop and failed in a script; a
/// refusal fails with its reason.
/// </summary>
public class GemStepTests
{
    private const int OwnId = 1, BenchId = 3;
    private const ushort BenchNetId = 5; // NpcDef m1.bench
    private const byte Jeweler = 2, Equipped = 5;
    private const long Ring = 77;

    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    [Fact]
    public void Socket_sends_the_first_cut_gem_into_worn_gear_with_a_free_slot_and_waits_for_the_committed_answer()
    {
        var bot = new Bot();
        bot.Contents([new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.roughGemT1"), Count = 3 },
            new InventorySlotEntry { Slot = 4, ItemNetId = Item("m3.rubyT1"), Count = 1 }]);
        var step = new SocketStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(ReliableMessages.TryRead(bot.Raw(0), out GemRequest request));
        Assert.Equal((GemOp.Socket, (uint)BenchId, Equipped, (byte)6, Ring, (byte)4),
            (request.Op, request.StationEntityId, request.Container, request.Slot, request.InstanceId, request.GemSlot));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the answer comes after the commit
        Assert.Equal(1, bot.SentCount);

        bot.Answer(request.RequestId, GemReason.None, success: false);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step)); // a lost gem is a result too (decision 11)
        Assert.Equal(1, bot.Counters.Loop["socket.failed"]);
    }

    [Fact]
    public void The_argument_names_the_gem_and_a_set_gem_counts()
    {
        var bot = new Bot();
        bot.Contents([new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.rubyT1"), Count = 1 },
            new InventorySlotEntry { Slot = 1, ItemNetId = Item("m3.emeraldT1"), Count = 1 }]);
        var step = new SocketStep("m3.emeraldT1");
        step.Begin(bot.Context);
        bot.Tick(step);
        Assert.True(ReliableMessages.TryRead(bot.Raw(0), out GemRequest request));
        Assert.Equal(1, request.GemSlot);
        bot.Answer(request.RequestId, GemReason.None, success: true);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["socket.socketed"]);
    }

    [Fact]
    public void Nothing_to_do_is_done_in_a_loop_and_failed_in_a_script()
    {
        var loop = new Bot();
        loop.Contents([]);
        var step = new SocketStep();
        step.Begin(loop.Context);
        Assert.Equal(BotStepStatus.Done, loop.Tick(step));
        Assert.Equal(1, loop.Counters.Loop["socket.noGem"]);

        var script = new Bot("m3-exit-jeweler");
        script.Contents([new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.rubyT1"), Count = 1 }], ringGems: [Item("m3.rubyT1")]);
        step.Begin(script.Context);
        Assert.Equal(BotStepStatus.Failed, script.Tick(step));
        Assert.Equal(1, script.Counters.Loop["socket.noTarget"]);

        var noJob = new Bot(job: false);
        noJob.Contents([new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.rubyT1"), Count = 1 }]);
        step.Begin(noJob.Context);
        Assert.Equal(BotStepStatus.Done, noJob.Tick(step));
        Assert.Equal(1, noJob.Counters.Loop["socket.noJob"]);
        Assert.Equal(0, noJob.SentCount);
    }

    [Fact]
    public void A_refusal_fails_the_step_with_its_reason()
    {
        var bot = new Bot();
        bot.Contents([new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.rubyT1"), Count = 1 }]);
        var step = new SocketStep();
        step.Begin(bot.Context);
        bot.Tick(step);
        Assert.True(ReliableMessages.TryRead(bot.Raw(0), out GemRequest request));
        bot.Answer(request.RequestId, GemReason.InCombat, success: false);
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["socket.refused.InCombat"]);
    }

    [Fact]
    public void The_jeweler_scripts_rough_gem_fight_is_done_once_the_bag_holds_two()
    {
        // m3-exit-jeweler's fight:until=m3.roughGemT1x2@field1.spawn.imp.a1 (WP-M3-8's item goal form at a tier 1 camp).
        var bot = new Bot();
        var step = new FightStep("until=m3.roughGemT1x2@field1.spawn.imp.a1");
        step.Begin(bot.Context);
        bot.Contents([new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.roughGemT1"), Count = 1 }]);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        bot.Contents([new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.roughGemT1"), Count = 1 },
            new InventorySlotEntry { Slot = 1, ItemNetId = Item("m3.roughGemT1"), Count = 1 }]);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
    }

    private static ushort Item(string id) => (ushort)Data.Value.Rows<ItemDef>().Single(i => i.Id == id).NetId;

    /// <summary>A level-12 Jeweler warrior's step context at the city's jeweler's bench, wearing a ring, with nothing connected.</summary>
    private sealed class Bot
    {
        private readonly List<byte[]> _sent = [];
        private uint _version;

        public Bot(string behaviour = "m3-loop", bool job = true)
        {
            var data = Data.Value;
            var position = data.Markers["m1.npc.bench"];
            var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), data.Behaviour(behaviour), data.Grid, position, 200,
                new Dictionary<int, int>(), new Pcg32Rng(1));
            Context = new BotStepContext(0, data, brain,
                new BotPerception { Self = position, Confirmed = position, NowMs = 1_000_000, ActionIdle = true },
                new EntityRegistry(PredictionSettings.From(data.Tables)), new ClientInventory(data.Tables), new BotOwnState(),
                new BotCounters(), BotStepRegistry.Default(), message => { _sent.Add(message); return true; }, _ => { })
            {
                ClassId = "warrior",
                Gems = new ClientGems(data.Tables),
            };
            Station();
            Assert.True(Context.Jobs.Apply(ReliableMessages.Write(ReliableMessageId.JobState, new JobState
            {
                Version = ++_version,
                Jobs = job ? [new JobEntry { JobNetId = Jeweler, Slot = JobSlot.Main, Level = 12, XpToNext = 100 }] : [],
            }), out _));
        }

        public BotStepContext Context { get; }

        public BotCounters Counters => Context.Counters;

        public int SentCount => _sent.Count;

        public BotStepStatus Tick(IBotStep step)
        {
            var command = Context.Brain.Hold();
            return step.Tick(Context, ref command);
        }

        private void Station()
        {
            int x = (int)GridPath.Cm(Context.View.Self.X), y = (int)GridPath.Cm(Context.View.Self.Y);
            var buffer = new byte[512];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 100, OriginXCm = x, OriginZCm = y });
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = OwnId, Kind = EntityKind.Player, DefId = 1 }));
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = BenchId, Kind = EntityKind.Npc, DefId = BenchNetId }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = OwnId, XCm = x, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = BenchId, XCm = x + 50, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            int size = writer.Finish();
            Assert.True(Context.Entities.ApplySnapshot(buffer.AsSpan(0, size), out _, out _, out _));
        }

        /// <summary>The Normal bag and a worn forged ring (its gems as net ids) in the ring slot.</summary>
        public void Contents(InventorySlotEntry[] normal, ushort[]? ringGems = null) =>
            Assert.True(Context.Inventory.Apply(ReliableMessages.Write(ReliableMessageId.InventoryContents, new InventoryContents
            {
                Revision = ++_version, SlotCount = 30, Slots = normal,
                Containers = [new InventoryContainerEntry { Code = 2, SlotCount = 20, Slots = [] },
                    new InventoryContainerEntry
                    {
                        Code = Equipped, SlotCount = 8,
                        Slots = [new InventorySlotEntry { Slot = 6, ItemNetId = Item("m3.forgedRing"), Count = 1, InstanceId = Ring, Gems = ringGems ?? [] }],
                    }],
            }), out _));

        public void Answer(uint requestId, GemReason reason, bool success) =>
            Assert.True(Context.Gems!.Apply(ReliableMessages.Write(ReliableMessageId.GemResult, new GemResult
            {
                RequestId = requestId, Reason = reason, Op = GemOp.Socket, Success = success, InstanceId = Ring,
            }), out _));

        public byte[] Raw(int index) => _sent[index];
    }
}
