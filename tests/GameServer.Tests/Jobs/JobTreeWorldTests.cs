using GameServer.Events;
using GameServer.Jobs;
using GameServer.Persistence;
using Shared.Protocol;
using Shared.Simulation.Jobs;

namespace GameServer.Tests.Jobs;

/// <summary>
/// The job skill trees in the world (spec 5.1 R-5.1-04, WP-M3-7; <c>docs/design/job-trees.md</c>), with the repository
/// tables: a learn or respec is checked, applied, saved at once and answered only once its save committed (rule 5); only
/// then does <see cref="IJobBonusQuery"/> read it. Learning works any time; a respec is refused in combat and during an
/// activity session. Decision 7: the secondary job levels on a slower curve and never above the main. Placeholders the tests
/// read: a pick's 1 point, Prospector +4 yield per level (cap 25), Diligent Smith +15 % job XP at level 3, rows at job
/// levels 1/5/10/15, the secondary curve at 150 % with a 1-level bank, and the job level curve 100 + 50 per level.
/// </summary>
public class JobTreeWorldTests
{
    private const byte Blacksmith = 1, Jeweler = 2;
    private const string Prospector = "blacksmith.prospector";

    private static JobWorldTests.Fixture Picked()
    {
        var f = new JobWorldTests.Fixture();
        f.Pick(1, Blacksmith, JobSlot.Main);
        f.Commit();
        return f;
    }

    /// <summary>A character whose saved jobs are <paramref name="rows"/> (committed, as loaded at login).</summary>
    private static JobWorldTests.Fixture Holding(params JobProgressRow[] rows) => new(jobs: new JobRecord(rows));

    private static JobProgressRow Smith(int level, int points, params (string Node, int Level)[] nodes) =>
        new("blacksmith", JobSlot.Main, level, 0, points, """{"v":1,"recent":[]}""")
        {
            Nodes = nodes.Select(n => new JobNodeRow(n.Node, n.Level)).ToList(),
        };

    private static void Tree(JobWorldTests.Fixture f, uint requestId, JobTreeOp op, byte job, string node = "")
    {
        f.Harness.SendJobTree(1, requestId, op, job, node);
        f.Harness.Ticks(1);
    }

    private static List<JobTreeResult> Answers(JobWorldTests.Fixture f) => f.Transport.JobTreeResultsFor(1);

    private static int Yield(JobWorldTests.Fixture f) =>
        f.Harness.World.JobBonusQuery.Get(f.CharacterId, "blacksmith", JobBonusKind.YieldPercent, JobActivity.Harvest);

    [Fact]
    public void A_pick_starts_with_one_job_point()
    {
        var f = Picked();

        Assert.Equal((ushort)1, f.LastState().Jobs.Single().Points);
    }

    [Fact]
    public void Prospector_learned_raises_the_yield_bonus_only_once_its_save_committed()
    {
        var f = Picked();
        Assert.Equal(0, Yield(f));

        Tree(f, 1, JobTreeOp.Learn, Blacksmith, Prospector);
        Assert.Empty(Answers(f)); // applied, waiting for its save
        Assert.Equal(0, Yield(f)); // the bonus query reads committed nodes only (rule 5)
        Assert.Equal(1, f.Jobs.WorkingNodes(f.CharacterId, "blacksmith")![Prospector]);

        f.Commit();
        var answer = Assert.Single(Answers(f));
        Assert.Equal((JobReason.None, (byte)1, (ushort)0), (answer.Reason, answer.Level, answer.Points));
        Assert.Equal(4, Yield(f)); // the next harvest's yield roll reads +4 % (WP-M3-5)
        var state = f.LastState();
        Assert.Equal(JobCause.TreeChanged, state.Cause);
        var job = state.Jobs.Single();
        Assert.Equal((ushort)0, job.Points);
        Assert.Equal((Prospector, (byte)1), (job.Nodes.Single().NodeId, job.Nodes.Single().Level));
        Assert.Contains(f.Harness.Characters.JobOps, o => o.Op == JobOp.Learn && o.NodeId == Prospector);
    }

    [Fact]
    public void A_respec_gives_the_point_back_and_clears_the_bonus_after_its_commit()
    {
        var f = Holding(Smith(3, 2, (Prospector, 2)));
        Assert.Equal(8, Yield(f));

        Tree(f, 1, JobTreeOp.Respec, Blacksmith);
        Assert.Equal(8, Yield(f));
        f.Commit();

        var answer = Assert.Single(Answers(f));
        Assert.Equal((JobReason.None, (ushort)4), (answer.Reason, answer.Points));
        Assert.Equal(0, Yield(f));
        Assert.Empty(f.LastState().Jobs.Single().Nodes);
        Assert.Equal((ushort)4, f.LastState().Jobs.Single().Points);
    }

    [Fact]
    public void A_double_submit_learns_once()
    {
        var f = Holding(Smith(2, 2));

        f.Harness.SendJobTree(1, 7, JobTreeOp.Learn, Blacksmith, Prospector);
        f.Harness.SendJobTree(1, 7, JobTreeOp.Learn, Blacksmith, Prospector);
        f.Harness.Ticks(1);
        f.Commit();
        Tree(f, 7, JobTreeOp.Learn, Blacksmith, Prospector); // the same request again after the commit

        Assert.All(Answers(f), a => Assert.Equal(JobReason.None, a.Reason));
        Assert.Equal(1, f.Jobs.WorkingNodes(f.CharacterId, "blacksmith")![Prospector]);
        Assert.Equal(1, f.Jobs.Working(f.CharacterId, "blacksmith")!.Value.Points);
    }

    [Theory]
    [InlineData(1, 1, "jeweler.gemCutter", JobReason.UnknownJobNode)] // a Jeweler node through the Blacksmith
    [InlineData(1, 1, "blacksmith.nope", JobReason.UnknownJobNode)]
    [InlineData(1, 1, "blacksmith.keenEye", JobReason.JobRowLocked)] // row 1 opens at job level 5
    [InlineData(5, 1, "blacksmith.keenEye", JobReason.JobNodeLocked)] // needs Prospector 1
    [InlineData(5, 0, Prospector, JobReason.NotEnoughJobPoints)]
    public void A_learn_is_refused_with_its_reason(int level, int points, string node, JobReason reason)
    {
        var f = Holding(Smith(level, points));

        Tree(f, 1, JobTreeOp.Learn, Blacksmith, node);

        Assert.Equal(reason, Assert.Single(Answers(f)).Reason);
        Assert.Equal(points, f.Jobs.Working(f.CharacterId, "blacksmith")!.Value.Points);
    }

    [Fact]
    public void A_maxed_node_and_a_job_not_held_are_refused()
    {
        var f = Holding(Smith(10, 3, (Prospector, 5)));

        Tree(f, 1, JobTreeOp.Learn, Blacksmith, Prospector);
        Tree(f, 2, JobTreeOp.Learn, Jeweler, "jeweler.gemCutter");
        Tree(f, 3, JobTreeOp.Respec, Jeweler);

        Assert.Equal([JobReason.JobNodeMaxed, JobReason.JobRequired, JobReason.JobRequired], Answers(f).Select(a => a.Reason));
    }

    [Fact]
    public void Learning_works_in_combat_but_a_respec_does_not()
    {
        var f = Holding(Smith(2, 2, (Prospector, 1)));
        f.Player.LastCombatAtMs = f.Harness.Clock.UtcNowMs;

        Tree(f, 1, JobTreeOp.Respec, Blacksmith);
        Assert.Equal(JobReason.InCombat, Assert.Single(Answers(f)).Reason);
        Tree(f, 2, JobTreeOp.Learn, Blacksmith, Prospector); // decision 10: learning works any time
        f.Commit();

        Assert.Equal(JobReason.None, Answers(f)[^1].Reason);
        Assert.Equal(8, Yield(f));
    }

    [Fact]
    public void A_respec_is_refused_during_an_activity_session_and_with_nothing_learned()
    {
        var f = Holding(Smith(2, 2, (Prospector, 1)));
        var gate = new FakeGate { Running = true };
        f.Harness.World.JobActivityGates.Add(gate);

        Tree(f, 1, JobTreeOp.Respec, Blacksmith);
        gate.Running = false;
        Tree(f, 2, JobTreeOp.Respec, Blacksmith);
        f.Commit();
        Tree(f, 3, JobTreeOp.Respec, Blacksmith);

        Assert.Equal([JobReason.JobActivityRunning, JobReason.None, JobReason.JobNothingToRespec], Answers(f).Select(a => a.Reason));
    }

    [Fact]
    public void Diligent_Smith_raises_the_job_xp_of_its_activities()
    {
        var f = Holding(Smith(2, 0, ("blacksmith.diligentSmith", 3)));

        f.Publish(f.Harvest("h1")); // 10 XP x 115 %, rounded down

        Assert.Equal(11, f.Log.Events.OfType<JobXpGained>().Single().Amount);
        Assert.Equal(11, f.Jobs.Working(f.CharacterId, "blacksmith")!.Value.Xp);
    }

    [Fact]
    public void A_secondary_Jeweler_levels_at_150_percent_never_above_the_main_and_catches_up_with_its_bank()
    {
        var f = Holding(Smith(2, 0), new JobProgressRow("jeweler", JobSlot.Secondary, 1, 0, 1, """{"v":1,"recent":[]}"""));

        // 20 refines of different recipes at tier 3 (10 base x 200 % = 20 each): 400 XP. The secondary curve is 150, 225, ...
        for (int i = 0; i < 20; i++)
            f.Publish(new Refined(f.CharacterId, f.Player.Id, "jeweler", 3, 200 + i, 500, 1, $"r{i}"));

        // Level 2 (150 XP) is the main's level: the rest is banked up to level 2's 225 and the 25 over it dropped.
        Assert.Equal((2, 225L, 2), Secondary(f));
        f.Harness.Ticks(1); // the state goes out in phase 10 of the tick after the event
        Assert.Equal((uint)225, f.LastState().Jobs.Single(j => j.Slot == JobSlot.Secondary).XpToNext);

        // The main levels to 3 (four tier-3 crafts, 160 XP of its 150): the bank applies at once.
        for (int i = 0; i < 4; i++)
            f.Publish(new Crafted(f.CharacterId, f.Player.Id, "blacksmith", 3, 100 + i, 500, 0, 10, f.CharacterId, $"c{i}"));

        Assert.Equal(3, f.Jobs.Working(f.CharacterId, "blacksmith")!.Value.Level);
        Assert.Equal((3, 0L, 3), Secondary(f));
        Assert.Contains(f.Log.Events.OfType<JobLevelUp>(), u => u.JobId == "jeweler" && (u.FromLevel, u.ToLevel) == (2, 3));
    }

    private static (int Level, long Xp, int Points) Secondary(JobWorldTests.Fixture f)
    {
        var job = f.Jobs.Working(f.CharacterId, "jeweler")!.Value;
        return (job.Level, job.Xp, job.Points);
    }

    private sealed class FakeGate : IJobActivityGate
    {
        public bool Running { get; set; }

        public bool InSession(long characterId) => Running;
    }
}
