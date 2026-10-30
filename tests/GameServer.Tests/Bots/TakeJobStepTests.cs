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
/// The loop's takeJob step (WP-M3-1, spec 5.1 M3 decisions) against a step context over the repository's tables, the job
/// master in the client's entity registry (a snapshot as the server writes it) and the server's answers applied to the kept
/// <c>ClientJobs</c>, as the world client does: below the level it waits, at the master it picks the main job and waits for
/// the committed answer, then the secondary, and a refusal fails the step with its reason.
/// </summary>
public class TakeJobStepTests
{
    private const int OwnId = 1, NpcId = 2;
    private const ushort JobMasterNetId = 3; // NpcDef m1.jobMaster
    private const byte Blacksmith = 1, Jeweler = 2;

    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    [Fact]
    public void Below_the_jobs_level_the_step_waits_and_from_it_picks_the_main_job_at_the_job_master()
    {
        var bot = new Bot();
        bot.JobMaster();
        var step = new TakeJobStep("blacksmith");
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step)); // no job state yet
        Assert.Equal(1, bot.Counters.Loop["takeJob.waiting"]);

        bot.Jobs(1);
        bot.Level(11);
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["takeJob.tooLow"]);
        Assert.Equal(0, bot.SentCount);

        bot.Level(12);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        var ask = bot.Sent(0);
        Assert.Equal(((uint)NpcId, Blacksmith, JobSlot.Main), (ask.NpcEntityId, ask.JobNetId, ask.Slot));
        Assert.Equal(BotStepStatus.Running, bot.Tick(step)); // the answer comes after the commit

        bot.Answer(ask, JobReason.None);
        bot.Jobs(2, new JobEntry { JobNetId = Blacksmith, Slot = JobSlot.Main, Level = 1, XpToNext = 100 });
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["takeJob.picked"]);

        step.Begin(bot.Context); // the next round: held
        Assert.Equal(BotStepStatus.Done, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["takeJob.held"]);
        Assert.Equal(1, bot.SentCount);
    }

    [Fact]
    public void With_a_different_main_held_the_entrys_job_goes_to_the_secondary_slot()
    {
        var bot = new Bot();
        bot.JobMaster();
        bot.Level(12);
        bot.Jobs(1, new JobEntry { JobNetId = Blacksmith, Slot = JobSlot.Main, Level = 1, XpToNext = 100 });
        var step = new TakeJobStep("jeweler");
        step.Begin(bot.Context);

        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        Assert.Equal((Jeweler, JobSlot.Secondary), (bot.Sent(0).JobNetId, bot.Sent(0).Slot));
    }

    [Fact]
    public void A_refusal_fails_the_step_with_its_reason_and_an_unknown_job_is_not_available()
    {
        var bot = new Bot();
        bot.JobMaster();
        bot.Level(12);
        bot.Jobs(1);
        var step = new TakeJobStep("blacksmith");
        step.Begin(bot.Context);
        bot.Tick(step);
        bot.Answer(bot.Sent(0), JobReason.InCombat);
        Assert.Equal(BotStepStatus.Failed, bot.Tick(step));
        Assert.Equal(1, bot.Counters.Loop["takeJob.refused.InCombat"]);

        var unknown = new TakeJobStep("tailor");
        unknown.Begin(bot.Context);
        Assert.Equal(BotStepStatus.NotAvailable, bot.Tick(unknown));
    }

    [Fact]
    public void Without_an_argument_bots_take_the_jobs_round_robin_in_id_order()
    {
        Assert.Equal(Blacksmith, FirstAsk(botIndex: 0).JobNetId);
        Assert.Equal(Jeweler, FirstAsk(botIndex: 1).JobNetId);
        Assert.Equal(Blacksmith, FirstAsk(botIndex: 2).JobNetId);
    }

    private static JobRequest FirstAsk(int botIndex)
    {
        var bot = new Bot(botIndex);
        bot.JobMaster();
        bot.Level(12);
        bot.Jobs(1);
        var step = new TakeJobStep();
        step.Begin(bot.Context);
        Assert.Equal(BotStepStatus.Running, bot.Tick(step));
        return bot.Sent(0);
    }

    /// <summary>A warrior's step context at the job master's marker with nothing connected; requests are kept, answers applied as the world client does.</summary>
    private sealed class Bot
    {
        private readonly List<byte[]> _sent = [];

        public Bot(int botIndex = 0)
        {
            var data = Data.Value;
            var position = data.Markers["m1.npc.jobMaster"];
            var brain = new BotBrain(BotKit.For(data.Tables, "warrior"), data.Behaviour("m2-loop"), data.Grid, position, 200,
                new Dictionary<int, int>(), new Pcg32Rng(1));
            Context = new BotStepContext(botIndex, data, brain,
                new BotPerception { Self = position, Confirmed = position, NowMs = 1_000_000, ActionIdle = true },
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

        /// <summary>The job master 50 cm from the bot, in the client's registry from a snapshot.</summary>
        public void JobMaster()
        {
            int x = (int)GridPath.Cm(Context.View.Self.X), y = (int)GridPath.Cm(Context.View.Self.Y);
            var buffer = new byte[512];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 100, OriginXCm = x, OriginZCm = y });
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = OwnId, Kind = EntityKind.Player, DefId = 1 }));
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = NpcId, Kind = EntityKind.Npc, DefId = JobMasterNetId }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = OwnId, XCm = x, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = NpcId, XCm = x + 50, ZCm = y, HpBucket = Snapshot.TopHpBucket }));
            int size = writer.Finish();
            Assert.True(Context.Entities.ApplySnapshot(buffer.AsSpan(0, size), out _, out _, out _));
        }

        public void Level(int level) =>
            Assert.True(Context.Progression.Apply(ReliableMessages.Write(ReliableMessageId.ProgressionState,
                new ProgressionState { Level = level, BandPercent = 100 }), out _));

        public void Jobs(uint version, params JobEntry[] jobs) =>
            Assert.True(Context.Jobs.Apply(ReliableMessages.Write(ReliableMessageId.JobState,
                new JobState { Version = version, Jobs = jobs }), out _));

        public void Answer(JobRequest ask, JobReason reason) =>
            Assert.True(Context.Jobs.Apply(ReliableMessages.Write(ReliableMessageId.JobResult,
                new JobResult { RequestId = ask.RequestId, Slot = ask.Slot, JobNetId = ask.JobNetId, Reason = reason }), out _));

        public JobRequest Sent(int index)
        {
            Assert.True(ReliableMessages.TryRead(_sent[index], out JobRequest request));
            return request;
        }
    }
}
