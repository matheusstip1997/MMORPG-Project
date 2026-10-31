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
/// The loop's refine, craft and convert steps and <c>equip:crafted</c> (WP-M3-6, spec 5.3, 11.5) against a step context over
/// the repository's tables, the city forge in the client's entity registry and the server's answers applied to the kept
/// <c>ClientForge</c>, as the world client does: the step picks its inputs from the bag, starts, uses each allowed action,
/// finishes, waits for the committed result; nothing to make is done in a loop and failed in a script; a refusal fails with
/// its reason.
/// </summary>
public class ForgeStepTests
{
    private const int OwnId = 1, ForgeId = 2;
    private const ushort ForgeNetId = 4; // NpcDef m1.forge
    private const byte Blacksmith = 1, Jeweler = 2, Normal = 1, Safe = 2;

    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    [Fact]
    public void Refine_starts_with_its_ore_finishes_and_waits_for_the_committed_result()
    {
        var bot = new Bot();
        bot.Bag(new InventorySlotEntry { Slot = 3, ItemNetId = Item("m3.oreT1"), Count = 2 });
        var step = new RefineStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var start = bot.Sent<ForgeStart>(0);
        Assert.Equal((ForgeKind.Recipe, (ushort)101, (uint)ForgeId), (start.Kind, start.DefNetId, start.StationEntityId));
        Assert.Equal((Normal, (byte)3), (start.Inputs.Single().Container, start.Inputs.Single().Slot));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // waits for the state

        bot.State(start.RequestId, ForgeReason.None);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var finish = bot.Sent<ForgeFinish>(1); // a recipe takes no actions
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the result comes after the commit

        bot.Result(finish.RequestId, ForgeReason.None);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["refine.made"]);
    }

    [Fact]
    public void Craft_uses_each_allowed_action_once_before_it_finishes()
    {
        var bot = new Bot();
        bot.Bag(new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.ingotT1"), Count = 2 },
            new InventorySlotEntry { Slot = 1, ItemNetId = Item("m1.cyclopsHide"), Count = 1 });
        var step = new CraftStep("m3.moldSword");
        step.Begin(bot.Context);

        bot.Tick(step);
        var start = bot.Sent<ForgeStart>(0);
        Assert.Equal((ForgeKind.Mold, (ushort)1), (start.Kind, start.DefNetId));
        Assert.Equal(new byte[] { 0, 1 }, start.Inputs.Select(i => i.Slot));
        uint last = start.RequestId;
        var actions = new List<byte>();
        for (int i = 0; i < 3; i++)
        {
            bot.State(last, ForgeReason.None);
            bot.Tick(step);
            var action = bot.Sent<ForgeAction>(1 + i);
            actions.Add(action.ActionNetId);
            last = action.RequestId;
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, actions); // smelt, hit, cool
        bot.State(last, ForgeReason.None);
        bot.Tick(step);
        bot.Result(bot.Sent<ForgeFinish>(4).RequestId, ForgeReason.None);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["craft.made"]);
    }

    [Fact]
    public void Nothing_to_make_is_done_in_a_loop_and_failed_in_a_script()
    {
        var loop = new Bot();
        loop.Bag();
        var refine = new RefineStep();
        refine.Begin(loop.Context);
        Assert.Equal(BotStepStatus.Done, loop.Tick(refine));
        Assert.Equal(1, loop.Counters.Loop["refine.noMaterials"]);
        Assert.Equal(0, loop.SentCount);

        var script = new Bot("m3-exit-blacksmith");
        script.Bag(new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.ingotT1"), Count = 2 }); // no grip
        var craft = new CraftStep();
        craft.Begin(script.Context);
        Assert.Equal(BotStepStatus.Failed, script.Tick(craft));
    }

    [Fact]
    public void A_jewelers_refine_and_craft_are_not_yet_until_its_recipes_land()
    {
        // Wave 1 QA F3, the WP-M3-12 step contract (Loop/Steps/README.md): no refine recipe and only output-less molds of the
        // Jeweler exist before WP-M3-9, so the script reports "not yet" naming the WP, not "failed: nothing to refine".
        var script = new Bot("m3-exit-jeweler");
        script.Jobs(level: 12, job: Jeweler);
        script.Contents(safe: [new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.moldRing"), Count = 1, InstanceId = 5 }]);

        foreach (IBotStep step in new IBotStep[] { new RefineStep(), new CraftStep() })
        {
            step.Begin(script.Context);
            Assert.Equal(BotStepStatus.NotAvailable, script.Tick(step));
            Assert.Contains(ForgeRun.ContentWp, script.Context.LastReason);
        }
        Assert.Equal(0, script.SentCount);
        Assert.False(script.Counters.Loop.ContainsKey("refine.noMaterials"));
    }

    [Fact]
    public void A_refusal_fails_the_step_with_its_reason()
    {
        var bot = new Bot();
        bot.Bag(new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.oreT1"), Count = 5 });
        var step = new ConvertStep();
        step.Begin(bot.Context);
        bot.Jobs(level: 3);
        bot.Tick(step);
        bot.State(bot.Sent<ForgeStart>(0).RequestId, ForgeReason.ConversionCapReached);
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["convert.refused.ConversionCapReached"]);
    }

    [Fact]
    public void Equip_crafted_moves_the_newest_crafted_piece_and_fails_without_one()
    {
        var bot = new Bot("m3-exit-blacksmith");
        bot.Bag();
        var step = new EquipStep(EquipStep.Crafted);
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["equip.noCrafted"]);

        bot.Contents(safe:
        [
            new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.moldSword"), Count = 1, InstanceId = 5 },
            new InventorySlotEntry { Slot = 1, ItemNetId = Item("m2.trainingSword"), Count = 1, InstanceId = 6, Rarity = 4 },
            new InventorySlotEntry { Slot = 2, ItemNetId = Item("m3.forgedSword"), Count = 1, InstanceId = 7, CrafterId = 9 },
            new InventorySlotEntry { Slot = 3, ItemNetId = Item("m3.forgedSword"), Count = 1, InstanceId = 8, CrafterId = 9 },
        ]);
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.True(ReliableMessages.TryRead(bot.Raw(0), out InventoryMoveRequest move));
        Assert.Equal((Safe, (byte)3), (move.FromContainer, move.FromSlot)); // the newest crafted instance, not the rarer drop
    }

    private static ushort Item(string id) => (ushort)Data.Value.Rows<ItemDef>().Single(i => i.Id == id).NetId;

    [Fact]
    public void Craft_plays_the_timing_bar_on_its_hit_and_counts_the_judgement()
    {
        // WP-M3-10: the hit's state opens a round; the bot presses near the centre on its own clock, then carries on.
        var bot = new Bot();
        long now = 0;
        bot.Context.Forge.NowMs = () => now;
        bot.Bag(new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.ingotT1"), Count = 2 },
            new InventorySlotEntry { Slot = 1, ItemNetId = Item("m1.cyclopsHide"), Count = 1 });
        var step = new CraftStep("m3.moldSword");
        step.Begin(bot.Context);
        bot.Tick(step);
        bot.State(bot.Sent<ForgeStart>(0).RequestId, ForgeReason.None);
        bot.Tick(step);
        bot.State(bot.Sent<ForgeAction>(1).RequestId, ForgeReason.None); // smelt: no round
        bot.Tick(step);
        var hit = bot.Sent<ForgeAction>(2);
        Assert.Equal(2, hit.ActionNetId);

        var bar = new TimingBarParams(900, 500, 120, 4_000, 30);
        bot.BarState(hit.RequestId, bar); // the round reaches the bot at now = 0
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(3, bot.SentCount); // waiting for the marker
        long best = TimingBar.BestOffsetMs(in bar, bot.Context.Behaviour.ReactionMinMs);
        long slack = (long)bot.Context.Behaviour.ForgeBarErrorPermille * 1_000 / bar.SpeedPermillePerSec + 1;
        now = best + slack;
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var press = bot.Sent<ForgeChallengeInput>(3);
        Assert.Equal((byte)1, press.Round);
        Assert.InRange((long)press.OffsetMs, best - slack, best + slack);
        Assert.True(TimingBar.Score(in bar, press.OffsetMs) > 0); // within the placeholder error: inside the window

        bot.Judged(press.RequestId, ForgeHitOutcome.Scored, 25);
        bot.Tick(step);
        Assert.Equal(3, bot.Sent<ForgeAction>(4).ActionNetId); // cool
        Assert.Equal((1L, 25L, 1L), (bot.Counters.Loop["craft.barHits"], bot.Counters.Loop["craft.barScore"],
            bot.Counters.Loop["craft.barOutcome.Scored"]));
    }

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
                Forge = new ClientForge { Rules = ForgeRules.From(data.Tables) },
            };
            Station();
            Jobs(level: 12);
            Assert.True(Context.Progression.Apply(ReliableMessages.Write(ReliableMessageId.ProgressionState,
                new ProgressionState { Level = 12, BandPercent = 100 }), out _));
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

        public void Jobs(int level, byte job = Blacksmith) =>
            Assert.True(Context.Jobs.Apply(ReliableMessages.Write(ReliableMessageId.JobState, new JobState
            {
                Version = ++_version, Jobs = [new JobEntry { JobNetId = job, Slot = JobSlot.Main, Level = (ushort)level, XpToNext = 100 }],
            }), out _));

        /// <summary>The Normal bag and the Safe bag equipment area (the sword mold by default).</summary>
        public void Bag(params InventorySlotEntry[] normal) =>
            Contents(normal, [new InventorySlotEntry { Slot = 0, ItemNetId = Item("m3.moldSword"), Count = 1, InstanceId = 5 }]);

        public void Contents(InventorySlotEntry[]? normal = null, InventorySlotEntry[]? safe = null) =>
            Assert.True(Context.Inventory.Apply(ReliableMessages.Write(ReliableMessageId.InventoryContents, new InventoryContents
            {
                Revision = ++_version, SlotCount = 30, Slots = normal ?? [],
                Containers = [new InventoryContainerEntry { Code = Safe, SlotCount = 20, Slots = safe ?? [] },
                    new InventoryContainerEntry { Code = 5, SlotCount = 8, Slots = [] }],
            }), out _));

        public void State(uint requestId, ForgeReason reason) =>
            Assert.True(Context.Forge.Apply(ReliableMessages.Write(ReliableMessageId.ForgeState, new ForgeState
            {
                Version = ++_version, RequestId = requestId, Reason = reason, Phase = reason == ForgeReason.None ? ForgePhase.Running : ForgePhase.None,
            }), out _));

        /// <summary>A running state that opens round <paramref name="round"/> of the timing bar (WP-M3-10).</summary>
        public void BarState(uint requestId, TimingBarParams bar, byte round = 1) =>
            Assert.True(Context.Forge.Apply(ReliableMessages.Write(ReliableMessageId.ForgeState, new ForgeState
            {
                Version = ++_version, RequestId = requestId, Phase = ForgePhase.Running, ChallengeKind = ForgeChallengeKind.TimingBar,
                BarRound = round, BarSpeed = (ushort)bar.SpeedPermillePerSec, BarTarget = (ushort)bar.TargetPermille,
                BarHalfWidth = (ushort)bar.HalfWidthPermille, BarDurationMs = (ushort)bar.DurationMs, BarHitMax = (ushort)bar.HitMaxScore,
            }), out _));

        /// <summary>The answer to a press: the round closed with its outcome and the scores so far.</summary>
        public void Judged(uint requestId, ForgeHitOutcome outcome, params ushort[] scores) =>
            Assert.True(Context.Forge.Apply(ReliableMessages.Write(ReliableMessageId.ForgeState, new ForgeState
            {
                Version = ++_version, RequestId = requestId, Phase = ForgePhase.Running, ChallengeKind = ForgeChallengeKind.TimingBar,
                HitScores = scores, LastOutcome = outcome,
            }), out _));

        public void Result(uint requestId, ForgeReason reason) =>
            Assert.True(Context.Forge.Apply(ReliableMessages.Write(ReliableMessageId.ForgeResult, new ForgeResult
            {
                RequestId = requestId, Reason = reason, Count = 1, Success = true,
            }), out _));

        public byte[] Raw(int index) => _sent[index];

        public T Sent<T>(int index) where T : class
        {
            object? value = typeof(T).Name switch
            {
                nameof(ForgeStart) => ReliableMessages.TryRead(_sent[index], out ForgeStart s) ? s : null,
                nameof(ForgeAction) => ReliableMessages.TryRead(_sent[index], out ForgeAction a) ? a : null,
                nameof(ForgeFinish) => ReliableMessages.TryRead(_sent[index], out ForgeFinish f) ? f : null,
                nameof(ForgeChallengeInput) => ReliableMessages.TryRead(_sent[index], out ForgeChallengeInput p) ? p : null,
                _ => null,
            };
            return Assert.IsType<T>(value);
        }
    }
}
