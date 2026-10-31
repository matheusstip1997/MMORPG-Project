using BotClient;
using BotClient.Brain;
using BotClient.Loop;
using BotClient.Loop.Steps;
using BotClient.World;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation.Jobs;
using Shared.Util;

namespace GameServer.Tests.Bots;

/// <summary>
/// The loop's learnJob step (WP-M3-7; spec 5.1 R-5.1-04, 11.5) against a step context over the repository's tables: it
/// spends a held job's points on its tree one request at a time, the first node in table order that can take a level, from
/// the client's kept job state, with the server's answers applied as the world client does. Placeholders it reads: the
/// Blacksmith's first node is Diligent Smith (max 3, row 0), Keen Eye needs job level 5.
/// </summary>
public class LearnJobStepTests
{
    private const byte Blacksmith = 1, Jeweler = 2;
    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    private sealed class Bot
    {
        private readonly List<byte[]> _sent = [];
        private uint _version;

        public Bot()
        {
            var data = Data.Value;
            var position = data.Markers["m1.npc.outpost"];
            var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), data.Behaviour("m2-loop"), data.Grid, position, 200,
                new Dictionary<int, int>(), new Pcg32Rng(1));
            Context = new BotStepContext(0, data, brain, new BotPerception { Self = position, Confirmed = position, NowMs = 1_000, ActionIdle = true },
                new EntityRegistry(PredictionSettings.From(data.Tables)), new ClientInventory(data.Tables), new BotOwnState(),
                new BotCounters(), BotStepRegistry.Default(), message => { _sent.Add(message); return true; }, _ => { })
            {
                ClassId = "warrior",
            };
        }

        public BotStepContext Context { get; }

        public BotCounters Counters => Context.Counters;

        public int SentCount => _sent.Count;

        public BotStepStatus Tick(IBotStep step)
        {
            var command = Context.Brain.Hold();
            return step.Tick(Context, ref command);
        }

        public JobTreeRequest Sent(int index)
        {
            Assert.True(ReliableMessages.TryRead(_sent[index], out JobTreeRequest request));
            return request;
        }

        public void Jobs(params JobEntry[] jobs) => Assert.True(Context.Jobs.Apply(ReliableMessages.Write(ReliableMessageId.JobState,
            new JobState { Version = ++_version, Jobs = jobs }), out _));

        public void Answer(JobTreeRequest ask, JobReason reason = JobReason.None) => Assert.True(Context.Jobs.Apply(
            ReliableMessages.Write(ReliableMessageId.JobTreeResult, new JobTreeResult
            {
                RequestId = ask.RequestId, Op = ask.Op, JobNetId = ask.JobNetId, NodeId = ask.NodeId, Reason = reason,
            }), out _));
    }

    private static JobEntry Job(byte netId, JobSlot slot, int level, int points, params (string Node, byte Level)[] nodes) => new()
    {
        JobNetId = netId, Slot = slot, Level = (ushort)level, Points = (ushort)points,
        Nodes = nodes.Select(n => new JobNodeEntry { NodeId = n.Node, Level = n.Level }).ToArray(),
    };

    [Fact]
    public void Without_a_job_state_it_waits_and_with_no_points_it_has_nothing_to_do()
    {
        var bot = new Bot();
        var step = new LearnJobStep();
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["learnJob.waiting"]);

        bot.Jobs(Job(Blacksmith, JobSlot.Main, 1, 0));
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["learnJob.nothing"]);
        Assert.Equal(0, bot.SentCount);
    }

    [Fact]
    public void It_learns_the_first_node_that_can_take_a_level_and_keeps_going_while_points_are_left()
    {
        var bot = new Bot();
        bot.Jobs(Job(Blacksmith, JobSlot.Main, 1, 2));
        var step = new LearnJobStep();
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var first = bot.Sent(0);
        Assert.Equal((JobTreeOp.Learn, Blacksmith, "blacksmith.diligentSmith"), (first.Op, first.JobNetId, first.NodeId));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // waits for the answer
        bot.Answer(first);
        bot.Jobs(Job(Blacksmith, JobSlot.Main, 1, 1, ("blacksmith.diligentSmith", 1)));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal("blacksmith.diligentSmith", bot.Sent(1).NodeId); // its second level
        bot.Answer(bot.Sent(1));
        bot.Jobs(Job(Blacksmith, JobSlot.Main, 1, 0, ("blacksmith.diligentSmith", 2)));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(2, bot.Counters.Loop["learnJob.levels"]);
        Assert.Equal(1, bot.Counters.Loop["learnJob.nothing"]);
    }

    [Fact]
    public void It_skips_maxed_nodes_and_locked_rows()
    {
        var bot = new Bot();
        // Diligent Smith maxed, Prospector maxed: Firm Hammer is next in row 0; Keen Eye (row 1) stays locked at job level 1.
        bot.Jobs(Job(Blacksmith, JobSlot.Main, 1, 3, ("blacksmith.diligentSmith", 3), ("blacksmith.prospector", 5)));
        var step = new LearnJobStep();

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal("blacksmith.firmHammer", bot.Sent(0).NodeId);
    }

    [Fact]
    public void The_argument_names_the_job_and_a_job_not_held_is_nothing_to_do()
    {
        var bot = new Bot();
        bot.Jobs(Job(Blacksmith, JobSlot.Main, 3, 2), Job(Jeweler, JobSlot.Secondary, 2, 1));

        var jeweler = new LearnJobStep("jeweler");
        Assert.Equal(BotStepStatus.Running, bot.Tick(jeweler));
        Assert.Equal((Jeweler, "jeweler.diligentJeweler"), (bot.Sent(0).JobNetId, bot.Sent(0).NodeId));

        var other = new Bot();
        other.Jobs(Job(Blacksmith, JobSlot.Main, 3, 2));
        Assert.Equal(BotStepStatus.Done, other.Tick(new LearnJobStep("jeweler")));
        Assert.Equal(1, other.Counters.Loop["learnJob.nothing"]);
        Assert.Equal(BotStepStatus.NotAvailable, other.Tick(new LearnJobStep("tailor")));
    }

    [Fact]
    public void A_refusal_fails_the_step_with_its_reason()
    {
        var bot = new Bot();
        bot.Jobs(Job(Blacksmith, JobSlot.Main, 1, 1));
        var step = new LearnJobStep();
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));

        bot.Answer(bot.Sent(0), JobReason.NotEnoughJobPoints);

        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["learnJob.refused.NotEnoughJobPoints"]);
    }
}
