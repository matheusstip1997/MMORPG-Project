using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Jobs;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Tests.Economy;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;

namespace GameServer.Tests.Jobs;

/// <summary>
/// Jobs in the world (spec 5.1 M3 decisions, WP-M3-1), with the repository tables (the Blacksmith's starter items are
/// WP-M3-2's two molds): the job master opens the window, a pick is checked, applied and
/// answered only once its save committed (rule 5), each slot is written once, the main comes first, the starter items ride
/// the same save, and the activity events raise job XP with diminishing returns, idempotent per operation key.
/// </summary>
public class JobWorldTests
{
    private const string Master = "test.npc.jobMaster";
    private const string FarMaster = "test.npc.jobMaster.far";
    private const string Outpost = "test.npc.outpost";
    private static readonly string[] BlacksmithMolds = ["m3.moldSword", "m3.moldChest"]; // JobDef.starterItemIds (WP-M3-2 rows)
    private const byte Blacksmith = 1, Jeweler = 2;
    private const byte Safe = 2;

    internal sealed class Fixture
    {
        public Fixture(int level = 12, int fillSafeSlots = 0, JobRecord? jobs = null)
        {
            var tables = TestCombat.Tables();
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            Bus.Subscribe<JobChosen>(Log);
            Bus.Subscribe<JobLevelUp>(Log);
            Bus.Subscribe<JobXpGained>(Log);
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(2_000, 0), EconomyFixture.Npc(Master, 200, 0, "m1.jobMaster"),
                EconomyFixture.Npc(FarMaster, 5_000, 5_000, "m1.jobMaster"), EconomyFixture.Npc(Outpost, 0, 200, "m1.outpost"),
            ]);
            Rules = JobRules.From(tables)!;
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus,
                combat: TestCombat.Rules(tables, TestCombat.Point()), items: ItemRules.From(tables)!,
                economy: EconomyRules.From(tables)!, progression: ProgressionRules.From(tables)!, jobs: Rules);
            var character = Harness.Characters.Add(1, 0, new CharacterState(level, 0, "", 0, 0, 0, 0, 100, 50, 100));
            Harness.Characters.Items[character.CharacterId] = [];
            Harness.Characters.Instances[character.CharacterId] = Enumerable.Range(0, fillSafeSlots)
                .Select(slot => new ItemInstanceRow(ItemInstanceIds.Make(1, slot + 1), Safe, slot, "m2.copperRing", 0, false, false,
                    ItemInstanceIds.NewProperties(1)))
                .ToArray();
            if (jobs != null)
                Harness.Characters.JobRecords[character.CharacterId] = jobs;
            Player = Harness.Join(1);
            Harness.Ticks(1);
        }

        public WorldHarness Harness { get; }
        public EventBus Bus { get; }
        public JobRules Rules { get; }
        public EventLog Log { get; } = new();
        public Player Player { get; }
        public JobService Jobs => Harness.World.Jobs!;
        public FakeTransport Transport => Harness.Transport;
        public long CharacterId => Player.CharacterId;

        public uint NpcId(string markerId) => Harness.World.Entities.OfType<Npc>().Single(n => n.MarkerId == markerId).Id;

        public void Pick(uint requestId, byte job, JobSlot slot, string master = Master)
        {
            Harness.SendJob(1, requestId, NpcId(master), job, slot);
            Harness.Ticks(1);
        }

        /// <summary>The pick's save (issued the next tick) and its completion.</summary>
        public void Commit() => Harness.Ticks(2);

        public JobResult LastResult() => Transport.JobResultsFor(1)[^1];

        public JobState LastState() => Transport.JobStatesFor(1)[^1];

        public void Publish<T>(in T payload) where T : struct, IGameEvent
        {
            Bus.Publish(payload, CharacterId);
            Harness.Ticks(1);
        }

        public Harvested Harvest(string opKey, int node = 1, int tier = 1, string job = "blacksmith") =>
            new(CharacterId, Player.Id, job, tier, node, 401, 1, opKey);
    }

    internal sealed class EventLog : IEventConsumer<JobChosen>, IEventConsumer<JobLevelUp>, IEventConsumer<JobXpGained>
    {
        public List<object> Events { get; } = [];
        public void Handle(in EventEnvelope envelope, in JobChosen payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in JobLevelUp payload) => Events.Add(payload);
        public void Handle(in EventEnvelope envelope, in JobXpGained payload) => Events.Add(payload);
    }

    [Fact]
    public void The_job_master_opens_the_window_with_the_state_after_its_yes()
    {
        var f = new Fixture();
        int before = f.Transport.JobStatesFor(1).Count;

        f.Harness.SendInteract(1, 1, f.NpcId(Master), NpcInteraction.Jobs);
        f.Harness.Ticks(1);

        var yes = f.Transport.InteractResultsFor(1)[^1];
        Assert.Equal((NpcInteraction.Jobs, OutpostReason.None), (yes.Kind, yes.Reason));
        Assert.Equal(before + 1, f.Transport.JobStatesFor(1).Count);
        Assert.Empty(f.LastState().Jobs);
        Assert.Equal(OutpostReason.InteractionUnavailable, Interact(f, Outpost)); // the outpost does not offer jobs
    }

    private static OutpostReason Interact(Fixture f, string marker)
    {
        f.Harness.SendInteract(1, 99, f.NpcId(marker), NpcInteraction.Jobs);
        f.Harness.Ticks(1);
        return f.Transport.InteractResultsFor(1)[^1].Reason;
    }

    [Fact]
    public void A_pick_is_answered_only_after_its_save_commits_with_its_starter_item_in_the_same_save()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;

        f.Pick(1, Blacksmith, JobSlot.Main);
        f.Harness.Ticks(2); // the pick's save is issued, not completed

        Assert.Empty(f.Transport.JobResultsFor(1));
        Assert.False(f.Jobs.HasJob(f.CharacterId, "blacksmith")); // not before the commit
        Assert.Empty(f.Log.Events.OfType<JobChosen>());
        var save = f.Harness.Saves.Jobs.Single(j => j.Jobs is { Ops.Count: > 0 });
        Assert.Equal(SaveReason.JobPick, save.Reason);
        var op = Assert.Single(save.Jobs!.Ops);
        Assert.Equal(("blacksmith", JobSlot.Main, 12), (op.JobId, op.Slot, op.LevelAtPick));
        Assert.Equal(BlacksmithMolds, op.StarterItems);
        Assert.Contains(save.Inventory!.Ops, o => o.OpKey == JobService.StarterOpKey(f.CharacterId, "blacksmith", 0)); // one transaction

        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var answer = f.LastResult();
        Assert.Equal((1u, JobSlot.Main, Blacksmith, JobReason.None), (answer.RequestId, answer.Slot, answer.JobNetId, answer.Reason));
        Assert.True(f.Jobs.HasJob(f.CharacterId, "blacksmith"));
        Assert.Equal(1, f.Jobs.JobLevel(f.CharacterId, "blacksmith"));
        Assert.Equal("blacksmith", f.Jobs.JobIn(f.CharacterId, JobSlot.Main));
        var chosen = Assert.Single(f.Log.Events.OfType<JobChosen>());
        Assert.Equal(("blacksmith", JobSlot.Main), (chosen.JobId, chosen.Slot));
        var state = f.LastState();
        Assert.Equal(JobCause.Picked, state.Cause);
        var entry = Assert.Single(state.Jobs);
        Assert.Equal((Blacksmith, JobSlot.Main, (ushort)1, 0u, (uint)f.Rules.XpToNext(1)), (entry.JobNetId, entry.Slot, entry.Level, entry.Xp, entry.XpToNext));
    }

    [Fact]
    public void A_double_submit_applies_once_and_a_repeat_after_the_commit_gets_the_first_answer()
    {
        var f = new Fixture();
        f.Harness.Saves.AutoComplete = false;

        f.Pick(1, Blacksmith, JobSlot.Main);
        f.Pick(1, Blacksmith, JobSlot.Main); // the same request while its save waits: nothing more, no answer yet
        Assert.Equal(1, f.Jobs.WaitingCount(f.CharacterId));
        Assert.Empty(f.Transport.JobResultsFor(1));

        f.Harness.Ticks(1);
        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);
        f.Pick(1, Blacksmith, JobSlot.Main); // after the commit: the first answer again

        Assert.Equal([JobReason.None, JobReason.None], f.Transport.JobResultsFor(1).Select(r => r.Reason));
        Assert.Single(f.Harness.Characters.JobOps);
        Assert.Single(f.Log.Events.OfType<JobChosen>());
    }

    [Fact]
    public void A_second_pick_into_a_written_slot_is_refused_with_JobAlreadyChosen()
    {
        var f = new Fixture();
        f.Pick(1, Blacksmith, JobSlot.Main);
        f.Commit();

        f.Pick(2, Jeweler, JobSlot.Main);

        Assert.Equal(JobReason.JobAlreadyChosen, f.LastResult().Reason);
        Assert.Equal("blacksmith", f.Jobs.JobIn(f.CharacterId, JobSlot.Main));
        Assert.Single(f.Harness.Characters.JobOps);
    }

    [Fact]
    public void Two_picks_for_one_slot_in_one_tick_have_one_winner()
    {
        var f = new Fixture();
        f.Harness.SendJob(1, 1, f.NpcId(Master), Blacksmith, JobSlot.Main);
        f.Harness.SendJob(1, 2, f.NpcId(Master), Jeweler, JobSlot.Main);
        f.Harness.Ticks(1);
        f.Commit();

        var results = f.Transport.JobResultsFor(1);
        Assert.Equal(JobReason.JobAlreadyChosen, results.Single(r => r.RequestId == 2).Reason); // refused at once
        Assert.Equal(JobReason.None, results.Single(r => r.RequestId == 1).Reason);
        Assert.Single(f.Harness.Characters.JobOps);
    }

    [Fact]
    public void The_main_job_comes_first_then_a_different_secondary()
    {
        var f = new Fixture();
        f.Pick(1, Jeweler, JobSlot.Secondary);
        Assert.Equal(JobReason.JobMainFirst, f.LastResult().Reason);

        f.Pick(2, Blacksmith, JobSlot.Main);
        f.Commit();
        f.Pick(3, Blacksmith, JobSlot.Secondary);
        Assert.Equal(JobReason.JobSameAsMain, f.LastResult().Reason);

        f.Pick(4, Jeweler, JobSlot.Secondary);
        f.Commit();
        Assert.Equal(JobReason.None, f.LastResult().Reason);
        Assert.Equal("jeweler", f.Jobs.JobIn(f.CharacterId, JobSlot.Secondary));
        Assert.Equal(2, f.LastState().Jobs.Length);
    }

    [Theory]
    [InlineData(11, JobReason.JobLevelTooLow)]
    [InlineData(12, JobReason.None)]
    public void The_pick_opens_at_the_jobs_minimum_character_level(int level, JobReason expected)
    {
        var f = new Fixture(level);
        f.Pick(1, Blacksmith, JobSlot.Main);
        f.Commit();
        Assert.Equal(expected, f.LastResult().Reason);
    }

    [Fact]
    public void A_pick_away_from_the_job_master_or_at_another_npc_is_refused()
    {
        var f = new Fixture();
        f.Pick(1, Blacksmith, JobSlot.Main, FarMaster);
        Assert.Equal(JobReason.JobMasterOutOfReach, f.LastResult().Reason);
        f.Pick(2, Blacksmith, JobSlot.Main, Outpost);
        Assert.Equal(JobReason.JobMasterOutOfReach, f.LastResult().Reason);
        Assert.False(f.Jobs.HasJob(f.CharacterId, "blacksmith"));
    }

    [Fact]
    public void A_pick_in_combat_or_for_an_unknown_job_is_refused()
    {
        var f = new Fixture();
        f.Player.LastCombatAtMs = f.Harness.Clock.UtcNowMs;
        f.Pick(1, Blacksmith, JobSlot.Main);
        Assert.Equal(JobReason.InCombat, f.LastResult().Reason);

        f.Player.LastCombatAtMs = 0;
        f.Pick(2, 77, JobSlot.Main);
        Assert.Equal(JobReason.UnknownJob, f.LastResult().Reason);
        Assert.Empty(f.Harness.Characters.JobOps);
    }

    [Fact]
    public void No_room_for_the_starter_items_refuses_the_pick_and_writes_nothing()
    {
        var f = new Fixture(fillSafeSlots: 19); // one free slot of the Safe bag equipment area (20) for the two molds
        f.Pick(1, Blacksmith, JobSlot.Main);
        f.Commit();

        Assert.Equal(JobReason.BagFull, f.LastResult().Reason);
        Assert.False(f.Jobs.HasJob(f.CharacterId, "blacksmith"));
        Assert.Empty(f.Harness.Characters.JobOps);
        Assert.False(f.Harness.World.Inventory!.WasApplied(f.CharacterId, JobService.StarterOpKey(f.CharacterId, "blacksmith", 0)));
    }

    [Fact]
    public void A_harvest_raises_the_jobs_xp_once_per_operation_key_with_diminishing_repeats()
    {
        var f = new Fixture();
        f.Pick(1, Blacksmith, JobSlot.Main);
        f.Commit();

        f.Publish(f.Harvest("h1"));
        Assert.Equal(10, f.Jobs.Working(f.CharacterId, "blacksmith")!.Value.Xp); // base 10 at tier 1
        f.Publish(f.Harvest("h1")); // a repeated Harvested adds nothing
        Assert.Equal(10, f.Jobs.Working(f.CharacterId, "blacksmith")!.Value.Xp);
        f.Publish(f.Harvest("h2")); // the same node again: one repeat, 90%
        Assert.Equal(19, f.Jobs.Working(f.CharacterId, "blacksmith")!.Value.Xp);
        f.Publish(f.Harvest("h3", node: 2, tier: 2)); // another node at tier 2: 150%, no repeat
        Assert.Equal(34, f.Jobs.Working(f.CharacterId, "blacksmith")!.Value.Xp);

        Assert.Equal([10, 9, 15], f.Log.Events.OfType<JobXpGained>().Select(e => e.Amount));
        f.Harness.Ticks(1); // the state goes out in phase 10 of the tick after the event (phase 11)
        Assert.Equal((uint)34, f.LastState().Jobs.Single().Xp);
        Assert.Equal(JobCause.Progress, f.LastState().Cause);
    }

    [Fact]
    public void Enough_xp_levels_the_job_up_with_its_points_and_a_JobLevelUp()
    {
        var f = new Fixture();
        f.Pick(1, Blacksmith, JobSlot.Main);
        f.Commit();

        // Ten crafts of different recipes at tier 3 (20 base x 200% = 40 each): 400 XP, over levels 1 (100) and 2 (150).
        for (int i = 0; i < 10; i++)
            f.Publish(new Crafted(f.CharacterId, f.Player.Id, "blacksmith", 3, 100 + i, 500, 0, 10, f.CharacterId, $"c{i}"));

        var job = f.Jobs.Working(f.CharacterId, "blacksmith")!.Value;
        Assert.Equal((3, 150L, 2), (job.Level, job.Xp, job.Points));
        var ups = f.Log.Events.OfType<JobLevelUp>().ToList();
        Assert.Equal([(1, 2), (2, 3)], ups.Select(u => (u.FromLevel, u.ToLevel)));
        Assert.All(ups, u => Assert.Equal(1, u.PointsGained));
    }

    [Fact]
    public void An_event_for_a_job_the_character_does_not_hold_or_without_xp_pays_nothing()
    {
        var f = new Fixture();
        f.Pick(1, Blacksmith, JobSlot.Main);
        f.Commit();

        f.Publish(f.Harvest("h1", job: "jeweler"));
        f.Publish(new ItemUsed(f.CharacterId, f.Player.Id, "", 0, 301, 1, "u1"));

        Assert.Equal(0, f.Jobs.Working(f.CharacterId, "blacksmith")!.Value.Xp);
        Assert.Null(f.Jobs.Working(f.CharacterId, "jeweler"));
        Assert.Empty(f.Log.Events.OfType<JobXpGained>());
        Assert.Equal(1, f.Jobs.IgnoredEvents);
    }

    [Fact]
    public void Job_xp_and_the_window_ride_the_class_2_batch_and_load_again()
    {
        var f = new Fixture();
        f.Pick(1, Blacksmith, JobSlot.Main);
        f.Commit();
        f.Publish(f.Harvest("h1"));
        f.Publish(f.Harvest("h2"));
        f.Harness.Clock.Advance(1_000);
        f.Harness.Ticks(2);

        var saved = f.Harness.Characters.JobRecords[f.CharacterId].Jobs.Single();
        Assert.Equal(("blacksmith", JobSlot.Main, 1, 19L), (saved.JobId, saved.Slot, saved.Level, saved.Xp));
        Assert.Contains("\"recent\":[[1,1],[1,1]]", saved.RecentJson);

        // The next login reads it: the window survives, so the next harvest of that node is diminished twice.
        var again = new Fixture(jobs: new JobRecord([saved]));
        again.Publish(again.Harvest("h3"));
        Assert.Equal(19 + 8, again.Jobs.Working(again.CharacterId, "blacksmith")!.Value.Xp);
    }

    [Fact]
    public void Harvests_and_crafts_count_their_achievements_once_per_operation_key()
    {
        var f = new Fixture();
        f.Pick(1, Blacksmith, JobSlot.Main);
        f.Commit();
        var store = f.Harness.World.Progression!;
        var progression = ProgressionRules.From(TestCombat.Tables())!;

        f.Publish(f.Harvest("h1"));
        f.Publish(f.Harvest("h1"));
        f.Publish(new Crafted(f.CharacterId, f.Player.Id, "blacksmith", 1, 100, 500, 0, 10, f.CharacterId, "c1"));

        Assert.Equal(1, store.CounterValue(f.CharacterId, progression.Counter(AchievementCategory.ResourcesHarvested)!));
        Assert.Equal(1, store.CounterValue(f.CharacterId, progression.Counter(AchievementCategory.ItemsCrafted)!));
    }
}
