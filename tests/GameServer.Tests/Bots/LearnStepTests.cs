using BotClient;
using BotClient.Brain;
using BotClient.Loop;
using BotClient.Loop.Steps;
using BotClient.World;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Util;

namespace GameServer.Tests.Bots;

/// <summary>
/// The M2 loop's learn step (WP-M2-5; spec 3.5 R-3.5-04, 11.5) against a step context over the repository's tables: it
/// spends the unspent points one request at a time, kit skills first, then upgrades, from the client's kept skill tree and
/// progression, with the server's answers applied as the world client does.
/// </summary>
public class LearnStepTests
{
    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    private sealed class Bot
    {
        private readonly List<byte[]> _sent = [];

        public Bot(string classId, int level, int points, params (string Node, byte Level)[] learned)
        {
            var data = Data.Value;
            var position = data.Markers["m1.npc.outpost"];
            var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), data.Behaviour("m2-loop"), data.Grid, position, 200,
                new Dictionary<int, int>(), new Pcg32Rng(1));
            Progression = new ClientProgression();
            Tree = new ClientSkillTree(data.Tables);
            Context = new BotStepContext(0, data, brain, new BotPerception { Self = position, Confirmed = position, NowMs = 1_000, ActionIdle = true },
                new EntityRegistry(PredictionSettings.From(data.Tables)), new ClientInventory(data.Tables), new BotOwnState(),
                new BotCounters(), BotStepRegistry.Default(), message => { _sent.Add(message); return true; }, _ => { })
            {
                ClassId = classId, Progression = Progression, SkillTree = Tree,
            };
            SetPoints(level, points);
            State(classId, learned);
        }

        public BotStepContext Context { get; }
        public ClientProgression Progression { get; }
        public ClientSkillTree Tree { get; }

        public BotStepStatus Tick(IBotStep step)
        {
            var command = Context.Brain.Hold();
            return step.Tick(Context, ref command);
        }

        public SkillTreeRequest Sent(int index)
        {
            Assert.True(ReliableMessages.TryRead(_sent[index], out SkillTreeRequest request));
            return request;
        }

        public int SentCount => _sent.Count;

        public void SetPoints(int level, int points) => Assert.True(Progression.Apply(ReliableMessages.Write(ReliableMessageId.ProgressionState,
            new ProgressionState { Level = level, SkillPoints = points, BandPercent = 100 }), out _));

        private uint _version;

        public void State(string classId, params (string Node, byte Level)[] learned) => Assert.True(Tree.Apply(
            ReliableMessages.Write(ReliableMessageId.SkillTreeState, new SkillTreeState
            {
                Version = ++_version, ClassId = classId,
                Nodes = learned.Select(l => new SkillNodeEntry { NodeId = l.Node, Level = l.Level }).ToArray(),
            }), out _));

        public void Answer(uint requestId, SkillTreeOp op, SkillTreeReason reason = SkillTreeReason.None) => Assert.True(Tree.Apply(
            ReliableMessages.Write(ReliableMessageId.SkillTreeResult, new SkillTreeResult { RequestId = requestId, Op = op, Reason = reason }), out _));
    }

    [Fact]
    public void A_new_character_learns_a_kit_skill_with_its_point_and_is_done_when_none_is_left()
    {
        var bot = new Bot("warrior", level: 1, points: 1);
        var step = new LearnStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var learn = bot.Sent(0);
        Assert.Equal((SkillTreeOp.Learn, "warrior.piercingAttack"), (learn.Op, learn.Target)); // a kit skill before Increase Def
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // waits for the answer
        Assert.Equal(1, bot.SentCount);

        bot.Answer(learn.RequestId, SkillTreeOp.Learn);
        bot.SetPoints(1, 0);
        bot.State("warrior", ("warrior.piercingAttack", 1));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Context.Counters.Loop["learn.skills"]);
        Assert.Equal(1, bot.Context.Counters.Loop["learn.nothing"]);
    }

    [Fact]
    public void With_nothing_left_to_learn_it_upgrades_the_lowest_learned_hotbar_skill()
    {
        // Level 1: every row-0 warrior node is learned, Piercing Attack at 2.
        var bot = new Bot("warrior", level: 1, points: 1, ("warrior.piercingAttack", 2), ("warrior.staminaBuff", 1), ("warrior.increaseDef", 1));
        var step = new LearnStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal((SkillTreeOp.Upgrade, "warrior.staminaBuff"), (bot.Sent(0).Op, bot.Sent(0).Target)); // not the passive
        bot.Answer(bot.Sent(0).RequestId, SkillTreeOp.Upgrade, SkillTreeReason.NotEnoughPoints);
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Equal(1, bot.Context.Counters.Loop["learn.refused.NotEnoughPoints"]);
    }

    [Fact]
    public void Without_points_it_is_done_at_once_and_without_a_tree_it_is_not_available()
    {
        var bot = new Bot("warrior", level: 5, points: 0);
        var step = new LearnStep();
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(0, bot.SentCount);

        var bare = new Bot("warrior", 1, 1);
        bare.Tree.Reset();
        Assert.Equal(BotStepStatus.NotAvailable, bare.Tick(step));
    }
}
