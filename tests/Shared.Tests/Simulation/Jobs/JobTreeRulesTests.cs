using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Jobs;
using Shared.Tests.Data;

namespace Shared.Tests.Simulation.Jobs;

/// <summary>
/// The job tree rules and bonuses of WP-M3-7 (spec 5.1 R-5.1-04; <c>docs/design/job-trees.md</c>) as pure functions: the
/// order of the learn refusals, the row gates, a free respec with every point back, the bonus sum per activity, the cap and
/// the mold tiers; and decision 7, the secondary job's slower curve, its cap at the main's level and its bank. The numbers
/// are test placeholders; the tests pin the rules.
/// </summary>
public class JobTreeRulesTests
{
    private static readonly JobTreeRules Rules = JobTreeRules.From(JobTreeTablesTests.Set(new[]
    {
        JobTreeTablesTests.Node("a", max: 5),
        JobTreeTablesTests.Node("q", col: 1, kind: JobBonusKind.QualityBonus, max: 5),
        JobTreeTablesTests.Node("b", row: 1, requires: new[] { "a" }, levels: new[] { 2 }),
        JobTreeTablesTests.Node("late", row: 3, col: 1),
        JobTreeTablesTests.Node("m", row: 3, kind: JobBonusKind.UnlockMoldTiers, max: 1),
    }))!;

    private static Dictionary<string, int> Learned(params (string Id, int Level)[] nodes) => nodes.ToDictionary(n => n.Id, n => n.Level);

    private static JobTreeCheck Learn(string node, int jobLevel = 20, int points = 5, bool held = true, params (string, int)[] learned) =>
        Rules.CheckLearn("blacksmith", held, jobLevel, Learned(learned), node, points, out _);

    [Fact]
    public void A_learn_checks_the_node_the_job_the_row_the_links_the_max_and_the_points_in_that_order()
    {
        Assert.Equal(JobTreeCheck.UnknownNode, Learn("nope"));
        Assert.Equal(JobTreeCheck.UnknownNode, Rules.CheckLearn("jeweler", true, 20, Learned(), "a", 5, out _));
        Assert.Equal(JobTreeCheck.JobRequired, Learn("b", jobLevel: 1, points: 0, held: false));
        Assert.Equal(JobTreeCheck.RowLocked, Learn("b", jobLevel: 4, points: 0));
        Assert.Equal(JobTreeCheck.NodeLocked, Learn("b", jobLevel: 5, points: 0, learned: ("a", 1)));
        Assert.Equal(JobTreeCheck.NotEnoughPoints, Learn("b", jobLevel: 5, points: 0, learned: ("a", 2)));
        Assert.Equal(JobTreeCheck.Ok, Learn("b", jobLevel: 5, points: 1, learned: ("a", 2)));
        Assert.Equal(JobTreeCheck.Maxed, Learn("a", points: 0, learned: ("a", 5)));
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 4, false)]
    [InlineData(1, 5, true)]
    [InlineData(2, 9, false)]
    [InlineData(2, 10, true)]
    [InlineData(3, 14, false)]
    [InlineData(3, 15, true)]
    [InlineData(4, 20, false)] // a row the settings do not have never opens
    public void Rows_open_at_their_job_level(int row, int jobLevel, bool open) => Assert.Equal(open, Rules.RowOpen(row, jobLevel));

    [Fact]
    public void A_learn_costs_the_next_levels_cost()
    {
        Assert.Equal(JobTreeCheck.Ok, Rules.CheckLearn("blacksmith", true, 1, Learned(("a", 2)), "a", 1, out int cost));
        Assert.Equal(1, cost);
    }

    [Fact]
    public void A_respec_gives_every_learned_level_back_and_needs_something_learned()
    {
        Assert.Equal(JobTreeCheck.NothingToRespec, Rules.CheckRespec("blacksmith", true, Learned(), out _));
        Assert.Equal(JobTreeCheck.JobRequired, Rules.CheckRespec("blacksmith", false, Learned(("a", 1)), out _));
        Assert.Equal(JobTreeCheck.Ok, Rules.CheckRespec("blacksmith", true, Learned(("a", 3), ("b", 2), ("m", 1)), out int refund));
        Assert.Equal(6, refund);
    }

    [Fact]
    public void Every_check_has_a_reason_code()
    {
        foreach (JobTreeCheck check in Enum.GetValues(typeof(JobTreeCheck)))
            Assert.Equal(check == JobTreeCheck.Ok, JobTreeRules.ReasonOf(check) == JobReason.None);
    }

    [Fact]
    public void The_bonus_sums_the_learned_nodes_of_its_kind_and_activity_and_stops_at_the_cap()
    {
        // a and b give 4 per level (yield, harvest); the cap is 25.
        Assert.Equal(0, Rules.Bonus("blacksmith", Learned(), JobBonusKind.YieldPercent, JobActivity.Harvest));
        Assert.Equal(8, Rules.Bonus("blacksmith", Learned(("a", 2)), JobBonusKind.YieldPercent, JobActivity.Harvest));
        Assert.Equal(16, Rules.Bonus("blacksmith", Learned(("a", 2), ("b", 2)), JobBonusKind.YieldPercent, JobActivity.Harvest));
        Assert.Equal(25, Rules.Bonus("blacksmith", Learned(("a", 5), ("b", 2), ("late", 2)), JobBonusKind.YieldPercent, JobActivity.Harvest));
        Assert.Equal(0, Rules.Bonus("blacksmith", Learned(("a", 5)), JobBonusKind.YieldPercent, JobActivity.Craft)); // another activity
        Assert.Equal(0, Rules.Bonus("jeweler", Learned(("a", 5)), JobBonusKind.YieldPercent, JobActivity.Harvest)); // another job's tree
        Assert.Equal(12, Rules.Bonus("blacksmith", Learned(("q", 5)), JobBonusKind.QualityBonus, JobActivity.Harvest)); // 20 capped at 12
    }

    [Fact]
    public void The_Jewelers_two_quality_nodes_feed_their_own_activity()
    {
        var gemCutter = JobTreeTablesTests.Node("cut", kind: JobBonusKind.QualityBonus, max: 2, job: "jeweler");
        gemCutter.Activities = new[] { JobActivity.Refine };
        var fineSetting = JobTreeTablesTests.Node("set", col: 1, kind: JobBonusKind.QualityBonus, max: 2, job: "jeweler");
        fineSetting.Activities = new[] { JobActivity.Craft };
        var rules = JobTreeRules.From(JobTreeTablesTests.Set(new[] { gemCutter, fineSetting }))!;
        var learned = Learned(("cut", 1), ("set", 2));

        Assert.Equal(4, rules.Bonus("jeweler", learned, JobBonusKind.QualityBonus, JobActivity.Refine));
        Assert.Equal(8, rules.Bonus("jeweler", learned, JobBonusKind.QualityBonus, JobActivity.Craft));
    }

    [Fact]
    public void Master_Molds_unlocks_its_tiers_and_nothing_else()
    {
        Assert.Equal(0u, Rules.MoldTiers("blacksmith", Learned()));
        uint tiers = Rules.MoldTiers("blacksmith", Learned(("m", 1)));
        Assert.True(JobTreeMath.HasTier(tiers, 2));
        Assert.True(JobTreeMath.HasTier(tiers, 3));
        Assert.False(JobTreeMath.HasTier(tiers, 1));
        Assert.False(JobTreeMath.HasTier(tiers, 4));
        Assert.Equal(0, Rules.Bonus("blacksmith", Learned(("m", 1)), JobBonusKind.UnlockMoldTiers, JobActivity.Craft));
    }

    [Fact]
    public void The_tab_reads_the_value_now_and_at_the_next_level()
    {
        var node = Rules.Node("a")!;
        Assert.Equal((0, 4), (JobTreeMath.ValueAt(node, 0), JobTreeMath.NextValue(node, 0)));
        Assert.Equal((12, 16), (JobTreeMath.ValueAt(node, 3), JobTreeMath.NextValue(node, 3)));
        Assert.Null(JobTreeMath.NextValue(node, 5));
    }

    // Decision 7 (R-5.1-03 as changed on 2026-10-30): the curve 100, 150, 200, 250 at 150 % is 150, 225, 300, 375.
    private static readonly int[] Curve = JobMath.ScaleCurve(new[] { 100, 150, 200, 250 }, 150);

    [Fact]
    public void The_secondary_curve_is_the_curve_times_its_percent_rounded_up()
    {
        Assert.Equal(new[] { 150, 225, 300, 375 }, Curve);
        Assert.Equal(new[] { 2, 1 }, JobMath.ScaleCurve(new[] { 1, 1 }, 150).Take(1).Concat(JobMath.ScaleCurve(new[] { 1 }, 100)));
    }

    [Fact]
    public void The_secondary_never_passes_the_main_and_banks_one_level_while_capped()
    {
        int level = 1;
        long xp = 0;
        Assert.Equal(1, JobMath.AddSecondaryXp(ref level, ref xp, 1_000, Curve, maxLevel: 5, mainLevel: 2, bankLevels: 1));
        Assert.Equal((2, 225L), (level, xp)); // 150 to level 2, then capped: 225 banked (level 2's need), the rest dropped

        Assert.Equal(0, JobMath.AddSecondaryXp(ref level, ref xp, 50, Curve, 5, 2, 1));
        Assert.Equal((2, 225L), (level, xp)); // the bank is full: more XP is dropped
    }

    [Fact]
    public void A_main_level_up_applies_the_bank_at_once_and_a_far_ahead_main_lets_it_catch_up()
    {
        int level = 2;
        long xp = 225;
        Assert.Equal(1, JobMath.AddSecondaryXp(ref level, ref xp, 0, Curve, 5, mainLevel: 3, bankLevels: 1));
        Assert.Equal((3, 0L), (level, xp));

        level = 1;
        xp = 0;
        Assert.Equal(3, JobMath.AddSecondaryXp(ref level, ref xp, 150 + 225 + 300 + 10, Curve, 5, mainLevel: 5, bankLevels: 1));
        Assert.Equal((4, 10L), (level, xp)); // not capped below the main: it levels normally
    }

    [Fact]
    public void Without_a_bank_capped_xp_is_dropped_and_at_the_job_cap_it_stays_zero()
    {
        int level = 2;
        long xp = 0;
        JobMath.AddSecondaryXp(ref level, ref xp, 100, Curve, 5, mainLevel: 2, bankLevels: 0);
        Assert.Equal((2, 0L), (level, xp));

        level = 4;
        xp = 0;
        Assert.Equal(1, JobMath.AddSecondaryXp(ref level, ref xp, 1_000, Curve, maxLevel: 5, mainLevel: 5, bankLevels: 1));
        Assert.Equal((5, 0L), (level, xp));
    }

    [Fact]
    public void A_secondary_above_its_main_gains_nothing_until_the_main_passes_it()
    {
        int level = 4;
        long xp = 20;
        Assert.Equal(0, JobMath.AddSecondaryXp(ref level, ref xp, 500, Curve, 5, mainLevel: 2, bankLevels: 1));
        Assert.Equal((4, 20L), (level, xp));
    }

    [Fact]
    public void The_bank_counts_its_levels_from_the_current_one()
    {
        Assert.Equal(225, JobMath.BankLimit(2, Curve, 5, 1));
        Assert.Equal(525, JobMath.BankLimit(2, Curve, 5, 2));
        Assert.Equal(375, JobMath.BankLimit(4, Curve, 5, 3)); // only the levels below the cap
        Assert.Equal(0, JobMath.BankLimit(5, Curve, 5, 1));
    }
}
