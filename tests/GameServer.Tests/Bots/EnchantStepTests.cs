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
using Shared.Simulation.Crafting;
using Shared.Simulation.Jobs;
using Shared.Util;

namespace GameServer.Tests.Bots;

/// <summary>
/// The loop's enchant step and the fight step's <c>until=</c> argument (WP-M3-8, spec 5.3 R-5.3-04, 11.5) against a step
/// context over the repository's tables, the city forge in the client's entity registry and the server's answers applied to
/// the kept <c>ClientEnchant</c>, as the world client does: the step picks the equipped sword, checks its stones, sends one
/// attempt and waits for the answer after the commit; without the stones or an item it is done in a loop and failed in a
/// script; a refusal fails with its reason.
/// </summary>
public class EnchantStepTests
{
    private const int OwnId = 1, ForgeId = 2;
    private const ushort ForgeNetId = 4; // NpcDef m1.forge
    private const byte Blacksmith = 1, Normal = 1, Safe = 2, Equipped = 5;
    private const long SwordId = 77;

    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    [Fact]
    public void Enchant_sends_one_attempt_on_the_equipped_sword_and_waits_for_the_committed_answer()
    {
        var bot = new Bot();
        bot.Contents(stones: 2, swordLevel: 2);
        var step = new EnchantStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var request = bot.Sent(0);
        Assert.Equal(((uint)ForgeId, Equipped, (byte)0, SwordId), (request.StationEntityId, request.Container, request.Slot, request.InstanceId));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the answer comes after the commit
        Assert.Equal(1, bot.SentCount);

        bot.Result(request.RequestId, EnchantReason.None, from: 2, to: 3);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal((1, 1), (bot.Counters.Loop["enchant.attempt"], bot.Counters.Loop["enchant.up"]));
        Assert.Equal((byte)3, step.Made!.ToLevel);
    }

    [Fact]
    public void An_argument_names_the_item_and_a_maxed_item_is_skipped()
    {
        var bot = new Bot();
        bot.Contents(stones: 1, swordLevel: 9);
        var step = new EnchantStep("m3.forgedSword");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step)); // +9 is not offered: nothing to enchant, done in a loop
        Assert.Equal(1, bot.Counters.Loop["enchant.noItem"]);
        Assert.Equal(0, bot.SentCount);
    }

    [Fact]
    public void Without_stones_it_is_done_in_a_loop_and_failed_in_a_script()
    {
        var loop = new Bot();
        loop.Contents(stones: 0);
        var step = new EnchantStep();
        step.Begin(loop.Context);
        Assert.Equal(BotStepStatus.Done, loop.Tick(step));
        Assert.Equal(1, loop.Counters.Loop["enchant.noStones"]);

        var script = new Bot("m3-exit-blacksmith");
        script.Contents(stones: 0);
        step = new EnchantStep();
        step.Begin(script.Context);
        Assert.Equal(BotStepStatus.Failed, script.Tick(step));
        Assert.Equal(0, script.SentCount);
    }

    [Fact]
    public void A_refusal_fails_with_its_reason()
    {
        var bot = new Bot("m3-exit-blacksmith");
        bot.Contents(stones: 1);
        var step = new EnchantStep();
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        bot.Result(bot.Sent(0).RequestId, EnchantReason.ItemReserved);
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["enchant.refused.ItemReserved"]);
    }

    [Fact]
    public void Fight_until_an_item_is_done_once_the_bag_holds_it()
    {
        var bot = new Bot();
        var step = new FightStep("until=m3.enchantStone x2@field1.spawn.imp.a1".Replace(" ", ""));
        step.Begin(bot.Context);
        bot.Contents(stones: 1);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        bot.Contents(stones: 2);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Throws<ArgumentException>(() => new FightStep("until=@field1.spawn.imp.a1"));
        Assert.Throws<ArgumentException>(() => new FightStep("until=m3.enchantStonex0"));
    }

    private static ushort Item(string id) => (ushort)Data.Value.Rows<ItemDef>().Single(i => i.Id == id).NetId;

    /// <summary>A level-12 Blacksmith warrior's step context at the city forge with nothing connected.</summary>
    private sealed class Bot
    {
        private readonly List<byte[]> _sent = [];
        private uint _version;

        public Bot(string behaviour = "m3-loop")
        {
            var data = Data.Value;
            var position = data.Markers["m1.npc.forge"];
            var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), data.Behaviour(behaviour), data.Grid, position, 200,
                new Dictionary<int, int>(), new Pcg32Rng(1));
            Context = new BotStepContext(0, data, brain,
                new BotPerception { Self = position, Confirmed = position, NowMs = 1_000_000, ActionIdle = true },
                new EntityRegistry(PredictionSettings.From(data.Tables)), new ClientInventory(data.Tables), new BotOwnState(),
                new BotCounters(), BotStepRegistry.Default(), message => { _sent.Add(message); return true; }, _ => { })
            {
                ClassId = "warrior",
                Enchant = new ClientEnchant { Rules = EnchantRules.From(data.Tables) },
            };
            Station();
            Assert.True(Context.Jobs.Apply(ReliableMessages.Write(ReliableMessageId.JobState, new JobState
            {
                Version = 1, Jobs = [new JobEntry { JobNetId = Blacksmith, Slot = JobSlot.Main, Level = 12, XpToNext = 100 }],
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
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = ForgeId, Kind = EntityKind.Npc, DefId = ForgeNetId }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = OwnId, XCm = x, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = ForgeId, XCm = x + 50, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            int size = writer.Finish();
            Assert.True(Context.Entities.ApplySnapshot(buffer.AsSpan(0, size), out _, out _, out _));
        }

        /// <summary>The stones in the Normal bag and the forged sword equipped at its level.</summary>
        public void Contents(int stones, int swordLevel = 0) =>
            Assert.True(Context.Inventory.Apply(ReliableMessages.Write(ReliableMessageId.InventoryContents, new InventoryContents
            {
                Revision = ++_version, SlotCount = 30,
                Slots = stones > 0 ? [new InventorySlotEntry { Slot = 4, ItemNetId = Item("m3.enchantStone"), Count = (ushort)stones }] : [],
                Containers = [new InventoryContainerEntry { Code = Safe, SlotCount = 20, Slots = [] },
                    new InventoryContainerEntry { Code = Equipped, SlotCount = 8, Slots =
                        [new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.forgedSword"), Count = 1, InstanceId = SwordId,
                            CrafterId = 1, Enchant = (byte)swordLevel }] }],
            }), out _));

        public void Result(uint requestId, EnchantReason reason, int from = 0, int to = 0) =>
            Assert.True(Context.Enchant.Apply(ReliableMessages.Write(ReliableMessageId.EnchantResult, new EnchantResult
            {
                RequestId = requestId, Reason = reason, InstanceId = SwordId, FromLevel = (byte)from, ToLevel = (byte)to,
                StonesUsed = (byte)(reason == EnchantReason.None ? 1 : 0),
            }), out _));

        public EnchantRequest Sent(int index) =>
            ReliableMessages.TryRead(_sent[index], out EnchantRequest request) ? request : throw new InvalidOperationException("not an EnchantRequest");
    }
}
