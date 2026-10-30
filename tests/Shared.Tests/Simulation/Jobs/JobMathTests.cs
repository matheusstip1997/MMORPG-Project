using Shared.Data.Tables;
using Shared.Simulation.Jobs;

namespace Shared.Tests.Simulation.Jobs;

/// <summary>
/// Job XP of R-5.1-04 (WP-M3-1) as pure integer math: the tier factor, the diminishing return for repeats inside the
/// window, level-ups and the job level cap; the recent window ring; the pick rules (spec 5.1 M3 decisions). The numbers are
/// placeholders; the tests pin the rules, not the tuning.
/// </summary>
public class JobMathTests
{
    private static readonly int[] Tiers = { 100, 150, 200 };

    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 150)]
    [InlineData(3, 200)]
    [InlineData(7, 200)] // past the end: the last entry
    [InlineData(0, 100)] // below 1: the first entry
    public void The_tier_factor_reads_its_entry_and_clamps(int tier, int expected) =>
        Assert.Equal(expected, JobMath.TierPercent(Tiers, tier));

    [Fact]
    public void A_tier_list_without_entries_pays_nothing() => Assert.Equal(0, JobMath.TierPercent(System.Array.Empty<int>(), 1));

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 90)]
    [InlineData(5, 50)]
    [InlineData(8, 20)]
    [InlineData(9, 20)] // the floor
    [InlineData(50, 20)]
    public void Repeats_diminish_the_xp_down_to_the_floor(int repeats, int expected) =>
        Assert.Equal(expected, JobMath.RepeatPercent(repeats, 10, 20));

    [Fact]
    public void A_floor_of_zero_lets_repeats_reach_nothing_and_never_below() =>
        Assert.Equal(0, JobMath.RepeatPercent(30, 10, 0));

    [Theory]
    [InlineData(10, 100, 100, 10)]
    [InlineData(10, 150, 100, 15)]
    [InlineData(10, 200, 90, 18)]
    [InlineData(10, 150, 20, 3)]
    [InlineData(7, 150, 90, 9)] // 9.45 rounded down
    [InlineData(10, 100, 0, 0)]
    [InlineData(0, 200, 100, 0)]
    public void Xp_is_base_times_tier_times_repeat_rounded_down(int baseXp, int tier, int repeat, int expected) =>
        Assert.Equal(expected, JobMath.XpFor(baseXp, tier, repeat));

    [Fact]
    public void Xp_takes_every_level_it_pays_for_and_stops_at_the_cap()
    {
        int[] curve = { 100, 150, 200 }; // levels 1-3, cap 4
        int level = 1;
        long xp = 50;
        Assert.Equal(2, JobMath.AddXp(ref level, ref xp, 220, curve, 4)); // 270: 100 to level 2, 150 to level 3, 20 left
        Assert.Equal((3, 20L), (level, xp));
        Assert.Equal(1, JobMath.AddXp(ref level, ref xp, 1_000, curve, 4));
        Assert.Equal((4, 0L), (level, xp)); // at the cap the rest is dropped
        Assert.Equal(0, JobMath.AddXp(ref level, ref xp, 1_000, curve, 4));
        Assert.Equal((4, 0L), (level, xp));
    }

    [Fact]
    public void The_window_counts_repeats_of_the_same_activity_and_subject_among_the_newest_entries()
    {
        var window = new JobRecentWindow(4);
        window.Add(JobActivity.Harvest, 7);
        window.Add(JobActivity.Harvest, 8);
        window.Add(JobActivity.Craft, 7);
        window.Add(JobActivity.Harvest, 7);
        Assert.Equal(2, window.Repeats(JobActivity.Harvest, 7, 4));
        Assert.Equal(1, window.Repeats(JobActivity.Harvest, 7, 2)); // only the two newest
        Assert.Equal(1, window.Repeats(JobActivity.Craft, 7, 4)); // another activity on the same subject is apart
        Assert.Equal(0, window.Repeats(JobActivity.Harvest, 9, 4));
    }

    [Fact]
    public void A_full_window_drops_its_oldest_entry_and_lists_oldest_first()
    {
        var window = new JobRecentWindow(3);
        for (int subject = 1; subject <= 5; subject++)
            window.Add(JobActivity.Harvest, subject);
        Assert.Equal(3, window.Count);
        Assert.Equal(new[] { 3, 4, 5 }, Enumerable.Range(0, window.Count).Select(i => window.At(i).Subject));
        Assert.Equal(0, window.Repeats(JobActivity.Harvest, 1, 3));
        var copy = window.Clone();
        copy.Add(JobActivity.Harvest, 6);
        Assert.Equal(5, window.At(2).Subject); // the copy is apart
        Assert.Equal(6, copy.At(2).Subject);
    }

    private static readonly JobDef Smith = new() { Id = "blacksmith", NetId = 1, AllowedAsSecondary = true, MinLevel = 12 };
    private static readonly JobDef MainOnly = new() { Id = "merchant", NetId = 3, AllowedAsSecondary = false, MinLevel = 12 };

    [Theory]
    [InlineData(JobSlot.Main, 12, null, null, JobPickCheck.Ok)]
    [InlineData(JobSlot.Main, 11, null, null, JobPickCheck.LevelTooLow)]
    [InlineData(JobSlot.Main, 30, "jeweler", null, JobPickCheck.AlreadyChosen)] // permanent (user decision 2026-10-30)
    [InlineData(JobSlot.Secondary, 30, null, null, JobPickCheck.MainFirst)] // the main first (user decision 2026-10-30)
    [InlineData(JobSlot.Secondary, 30, "blacksmith", null, JobPickCheck.SameAsMain)]
    [InlineData(JobSlot.Secondary, 30, "jeweler", null, JobPickCheck.Ok)]
    [InlineData(JobSlot.Secondary, 30, "jeweler", "tailor", JobPickCheck.AlreadyChosen)]
    public void A_pick_checks_the_slot_the_order_the_job_and_the_level(JobSlot slot, int level, string? main, string? secondary,
        JobPickCheck expected) =>
        Assert.Equal(expected, JobRules.CheckPick(Smith, slot, level, main, secondary));

    [Fact]
    public void A_job_not_allowed_as_secondary_is_refused_there_only()
    {
        Assert.Equal(JobPickCheck.NotSecondary, JobRules.CheckPick(MainOnly, JobSlot.Secondary, 30, "blacksmith", null));
        Assert.Equal(JobPickCheck.Ok, JobRules.CheckPick(MainOnly, JobSlot.Main, 30, null, null));
    }
}
