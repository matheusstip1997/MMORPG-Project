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
using Shared.Simulation.Stats;
using Shared.Util;

namespace GameServer.Tests.Bots;

/// <summary>
/// The M2 loop's allocate step (WP-M2-13; spec 3.4 R-3.4-08, 11.5) against a step context over the repository's tables:
/// it places the unspent free attribute points one request at a time on the class's listed attributes, the fewest placed
/// first, with the server's answers applied as the world client does; out of combat only.
/// </summary>
public class AllocateStepTests
{
    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    private sealed class Bot
    {
        private readonly List<byte[]> _sent = [];

        public Bot(string classId, int unspent, int str = 0, int dex = 0, int sta = 0, int intelligence = 0)
        {
            var data = Data.Value;
            var position = data.Markers["m1.npc.outpost"];
            var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), data.Behaviour("m2-loop"), data.Grid, position, 200,
                new Dictionary<int, int>(), new Pcg32Rng(1));
            View = new BotPerception { Self = position, Confirmed = position, NowMs = 1_000_000, ActionIdle = true };
            Context = new BotStepContext(0, data, brain, View, new EntityRegistry(PredictionSettings.From(data.Tables)),
                new ClientInventory(data.Tables), new BotOwnState(), new BotCounters(), BotStepRegistry.Default(),
                message => { _sent.Add(message); return true; }, _ => { })
            {
                ClassId = classId, Progression = Progression,
            };
            SetPoints(unspent, str, dex, sta, intelligence);
        }

        public BotStepContext Context { get; }
        public BotPerception View { get; }
        public ClientProgression Progression { get; } = new();

        public BotStepStatus Tick(IBotStep step)
        {
            var command = Context.Brain.Hold();
            return step.Tick(Context, ref command);
        }

        public AttributeSpendRequest Sent(int index)
        {
            Assert.True(ReliableMessages.TryRead(_sent[index], out AttributeSpendRequest request));
            return request;
        }

        public int SentCount => _sent.Count;

        public void SetPoints(int unspent, int str = 0, int dex = 0, int sta = 0, int intelligence = 0) => Assert.True(Progression.Apply(
            ReliableMessages.Write(ReliableMessageId.ProgressionState, new ProgressionState
            {
                Level = 1 + unspent + str + dex + sta + intelligence, BandPercent = 100, AttributePoints = unspent, StrPlaced = str,
                DexPlaced = dex, StaPlaced = sta, IntPlaced = intelligence,
            }), out _));

        public void Answer(uint requestId, int attribute, AttributeReason reason = AttributeReason.None) => Assert.True(Progression.Apply(
            ReliableMessages.Write(ReliableMessageId.AttributeSpendResult,
                new AttributeSpendResult { RequestId = requestId, Attribute = (byte)attribute, Reason = reason }), out _));
    }

    [Fact]
    public void A_warrior_spreads_its_points_over_str_and_sta_one_request_at_a_time()
    {
        var bot = new Bot("warrior", unspent: 2);
        var step = new AllocateStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal((1u, (byte)Attributes.Str), (bot.Sent(0).RequestId, bot.Sent(0).Attribute)); // ties in list order
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // waits for its answer
        Assert.Equal(1, bot.SentCount);

        bot.Answer(1, Attributes.Str);
        bot.SetPoints(1, str: 1); // the committed points follow the answer
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal((byte)Attributes.Sta, bot.Sent(1).Attribute); // the fewest placed of the list

        bot.Answer(2, Attributes.Sta);
        bot.SetPoints(0, str: 1, sta: 1);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(2, bot.Context.Counters.Loop["allocate.points"]);
        Assert.Equal(1, bot.Context.Counters.Loop["allocate.nothing"]);
    }

    [Fact]
    public void A_tier_1_class_uses_its_root_s_entry_and_a_class_without_one_its_fastest_growth()
    {
        var behaviour = Data.Value.Behaviour("m2-loop");
        var knight = new ProgressionState { Level = 31, AttributePoints = 3, StrPlaced = 5, StaPlaced = 4 };
        Assert.Equal(Attributes.Sta, AllocateStep.Choose(Data.Value.Tables, behaviour, "knight", knight)); // warrior's str,sta

        var none = new BotBehaviour { Id = "none" };
        Assert.Equal(Attributes.Int, AllocateStep.Choose(Data.Value.Tables, none, "mage", knight)); // intGrowth 325
        Assert.Equal(Attributes.Dex, AllocateStep.Choose(Data.Value.Tables, none, "archer", knight)); // dexGrowth 300
    }

    [Fact]
    public void No_point_no_state_or_combat_ends_the_step_at_once_and_a_refusal_fails_it()
    {
        var bot = new Bot("mage", unspent: 0);
        Assert.Equal(BotStepStatus.Done, bot.Tick(new AllocateStep()));
        Assert.Equal(1, bot.Context.Counters.Loop["allocate.nothing"]);

        bot.SetPoints(1);
        bot.View.LastCombatAtMs = bot.View.NowMs; // the server refuses a placement in combat (user decision 2026-10-29)
        Assert.Equal(BotStepStatus.Done, bot.Tick(new AllocateStep()));
        Assert.Equal(1, bot.Context.Counters.Loop["allocate.inCombat"]);
        Assert.Equal(0, bot.SentCount);

        bot.View.LastCombatAtMs = 0;
        var step = new AllocateStep();
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal((byte)Attributes.Int, bot.Sent(0).Attribute);
        bot.Answer(1, Attributes.Int, AttributeReason.AttributeBlockedCombat);
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Equal(1, bot.Context.Counters.Loop["allocate.refused.AttributeBlockedCombat"]);

        var fresh = new Bot("mage", unspent: 0);
        var noState = new BotStepContext(0, Data.Value, fresh.Context.Brain, fresh.View, fresh.Context.Entities, fresh.Context.Inventory,
            new BotOwnState(), new BotCounters(), BotStepRegistry.Default(), _ => true, _ => { }) { ClassId = "mage" };
        var command = noState.Brain.Hold();
        Assert.Equal(BotStepStatus.Done, new AllocateStep().Tick(noState, ref command));
        Assert.Equal(1, noState.Counters.Loop["allocate.waiting"]);
    }

    [Fact]
    public void The_attribute_order_entries_are_checked()
    {
        Assert.True(BotBehaviour.TryParseAttributeOrder("warrior:str,sta", out string classId, out var attributes));
        Assert.Equal("warrior", classId);
        Assert.Equal(new[] { Attributes.Str, Attributes.Sta }, attributes);
        foreach (var bad in new[] { "warrior", ":str", "warrior:", "warrior:luck", "warrior:str,str" })
            Assert.False(BotBehaviour.TryParseAttributeOrder(bad, out _, out _), bad);

        var errors = new Shared.Data.RowErrors();
        new BotBehaviour { Id = "x", HomeMarkerIds = ["m"], LoopAttributeOrder = ["warrior:str", "warrior:dex", "mage:luck"] }.Validate(errors);
        Assert.Contains(errors.Items, e => e.Message.Contains("listed twice"));
        Assert.Contains(errors.Items, e => e.Message.Contains("'mage:luck'"));
    }
}
