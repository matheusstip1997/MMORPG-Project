using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Gathering;
using Shared.Util;

namespace Shared.Tests.Simulation.Gathering;

/// <summary>
/// The harvest yield roll and gate as pure functions (spec 5.2 "yield is a server roll using node tier, job level,
/// skill-tree bonus, and randomness"; anti-bot "diminishing returns on repetitive harvesting"; WP-M3-5).
/// </summary>
public class HarvestMathTests
{
    private static readonly HarvestSettings Settings = new()
    {
        YieldRepeatWindow = 10, YieldRepeatStepPercent = 10, YieldRepeatFloorPercent = 50, YieldPercentPerJobLevel = 5,
        MaxBonusPercent = 200,
    };

    private static readonly HarvestChoice[] Ore =
    {
        new(401, 0, 93, 1, 3, 1),
        new(405, 0, 5, 1, 1, 1, isGem: true),
        new(406, 0, 2, 1, 1, 5),
    };

    /// <summary>Returns the queued values in order (NextInt: the value modulo the bound, or within the range).</summary>
    private sealed class ScriptedRng : IRng
    {
        private readonly Queue<int> _values;
        public ScriptedRng(params int[] values) => _values = new Queue<int>(values);
        public int Draws { get; private set; }
        public uint NextUInt() { Draws++; return (uint)_values.Dequeue(); }
        public int NextInt(int maxExclusive) { Draws++; return _values.Dequeue() % maxExclusive; }
        public int NextInt(int minInclusive, int maxExclusive) { Draws++; return minInclusive + _values.Dequeue() % (maxExclusive - minInclusive); }
        public bool RollPercent(int percent) { Draws++; return _values.Dequeue() < percent; }
    }

    [Fact]
    public void The_pick_is_by_weight_the_non_gem_entries_first_then_the_gems()
    {
        // Job level 1 (the stone needs level 5): 98 in all, drawn in hundredths: 0..9299 ore, 9300..9799 the gem.
        Assert.Equal(401, HarvestMath.Roll(Ore, 1, 0, 100, new ScriptedRng(0, 0, 0)).ItemNetId);
        Assert.Equal(401, HarvestMath.Roll(Ore, 1, 0, 100, new ScriptedRng(9_299, 0, 0)).ItemNetId);
        Assert.Equal(405, HarvestMath.Roll(Ore, 1, 0, 100, new ScriptedRng(9_300, 0, 0)).ItemNetId);
        Assert.Equal(405, HarvestMath.Roll(Ore, 1, 0, 100, new ScriptedRng(9_799, 0, 0)).ItemNetId);
    }

    [Fact]
    public void An_entry_above_the_job_level_is_out_of_the_roll_and_in_once_the_level_reaches_it()
    {
        Assert.Equal(401, HarvestMath.Roll(Ore, 4, 0, 100, new ScriptedRng(9_299, 0, 0)).ItemNetId); // the last ore hundredth
        Assert.Equal(406, HarvestMath.Roll(Ore, 5, 0, 100, new ScriptedRng(9_400, 0, 0)).ItemNetId); // 9300..9499: the stone
    }

    [Fact]
    public void The_gem_find_bonus_adds_percentage_points_to_the_gem_entries()
    {
        // 5 of 98 is about 5.1 %; +10 points makes the gem span 500 + 10 x 98 = 1480 of 9800 hundredths (about 15.1 %).
        Assert.Equal(401, HarvestMath.Roll(Ore, 1, 0, 100, new ScriptedRng(8_320, 0, 0)).ItemNetId);
        Assert.Equal(405, HarvestMath.Roll(Ore, 1, 0, 100, new ScriptedRng(8_320, 0, 0), gemFindPoints: 10).ItemNetId);
        Assert.Equal(401, HarvestMath.Roll(Ore, 1, 0, 100, new ScriptedRng(8_319, 0, 0), gemFindPoints: 10).ItemNetId);
        // Never past every unit of the roll, and a table without gem entries ignores it.
        Assert.Equal(405, HarvestMath.Roll(Ore, 1, 0, 100, new ScriptedRng(0, 0, 0), gemFindPoints: 500).ItemNetId);
        var noGems = new[] { new HarvestChoice(401, 0, 10, 1, 1, 1) };
        Assert.Equal(401, HarvestMath.Roll(noGems, 1, 0, 100, new ScriptedRng(999, 0, 0), gemFindPoints: 50).ItemNetId);
    }

    [Fact]
    public void Over_many_seeds_the_gem_find_bonus_raises_the_gem_share_by_about_its_points()
    {
        int Gems(int points)
        {
            int gems = 0;
            for (ulong seed = 1; seed <= 20_000; seed++)
                gems += HarvestMath.Roll(Ore, 1, 0, 100, new Pcg32Rng(seed, 3), points).ItemNetId == 405 ? 1 : 0;
            return gems;
        }
        Assert.InRange(Gems(0), 860, 1_180); // 5.1 % of 20 000
        Assert.InRange(Gems(3), 1_450, 1_800); // 8.1 %
    }

    [Fact]
    public void No_entry_in_reach_is_an_empty_yield_with_no_draw()
    {
        var rng = new ScriptedRng();
        var yield = HarvestMath.Roll(new[] { new HarvestChoice(406, 0, 2, 1, 1, 5) }, 1, 0, 100, rng);
        Assert.True(yield.IsEmpty);
        Assert.Equal(0, rng.Draws);
    }

    [Fact]
    public void The_base_count_is_in_its_range_and_a_picked_entry_draws_exactly_three_times()
    {
        var low = new ScriptedRng(0, 0, 9_999);
        Assert.Equal(1, HarvestMath.Roll(Ore, 1, 0, 100, low).Count);
        Assert.Equal(3, low.Draws);
        Assert.Equal(3, HarvestMath.Roll(Ore, 1, 0, 100, new ScriptedRng(0, 2, 9_999)).Count);
    }

    [Fact]
    public void The_bonus_scales_the_count_and_the_remainder_rounds_by_chance()
    {
        // base 3 x 150 % = 4.5: the half rounds up below a draw of 5000 and down from it.
        Assert.Equal(5, HarvestMath.ScaledCount(3, 50, 100, new ScriptedRng(4_999)));
        Assert.Equal(4, HarvestMath.ScaledCount(3, 50, 100, new ScriptedRng(5_000)));
        // A whole result never rounds up: the draw is still made, so the stream does not depend on the numbers.
        var whole = new ScriptedRng(0);
        Assert.Equal(6, HarvestMath.ScaledCount(3, 100, 100, whole));
        Assert.Equal(1, whole.Draws);
    }

    [Fact]
    public void The_count_is_never_below_one_unit()
    {
        Assert.Equal(1, HarvestMath.ScaledCount(1, 0, 0, new ScriptedRng(9_999)));
        Assert.Equal(1, HarvestMath.ScaledCount(1, 0, 50, new ScriptedRng(9_999)));
    }

    [Fact]
    public void The_job_level_tool_and_tree_bonuses_add_up_and_are_capped()
    {
        Assert.Equal(0, HarvestMath.BonusPercent(1, 0, 0, Settings));
        Assert.Equal(45 + 10 + 5, HarvestMath.BonusPercent(10, 10, 5, Settings));
        Assert.Equal(200, HarvestMath.BonusPercent(20, 150, 100, Settings));
        Assert.Equal(0, HarvestMath.BonusPercent(0, -5, -5, Settings)); // nothing negative counts
    }

    [Fact]
    public void Repeats_diminish_the_yield_down_to_the_floor()
    {
        Assert.Equal(100, HarvestMath.RepeatPercent(0, Settings));
        Assert.Equal(70, HarvestMath.RepeatPercent(3, Settings));
        Assert.Equal(50, HarvestMath.RepeatPercent(5, Settings));
        Assert.Equal(50, HarvestMath.RepeatPercent(9, Settings));
        // base 3 at the floor: 1.5 rounds to 1 or 2, never 3.
        Assert.Equal(1, HarvestMath.ScaledCount(3, 0, 50, new ScriptedRng(5_000)));
        Assert.Equal(2, HarvestMath.ScaledCount(3, 0, 50, new ScriptedRng(4_999)));
    }

    [Fact]
    public void The_same_seed_gives_the_same_yield()
    {
        for (ulong seed = 1; seed < 50; seed++)
        {
            var a = HarvestMath.Roll(Ore, 7, 30, 80, new Pcg32Rng(seed, 9));
            var b = HarvestMath.Roll(Ore, 7, 30, 80, new Pcg32Rng(seed, 9));
            Assert.Equal((a.EntryIndex, a.ItemNetId, a.Count), (b.EntryIndex, b.ItemNetId, b.Count));
            Assert.InRange(a.Count, 1, HarvestMath.MaxCount(Ore[a.EntryIndex], 30, 80));
        }
    }

    [Fact]
    public void Max_count_bounds_every_roll_of_an_entry()
    {
        Assert.Equal(3, HarvestMath.MaxCount(Ore[0], 0, 100));
        Assert.Equal(9, HarvestMath.MaxCount(Ore[0], 200, 100));
        Assert.Equal(2, HarvestMath.MaxCount(Ore[0], 0, 50));
        Assert.Equal(1, HarvestMath.MaxCount(Ore[1], 0, 10));
    }
}

/// <summary>The harvest gate in its order (spec 5.2 R-5.2-01, R-5.2-02; WP-M3-5): the first failing check is the reason.</summary>
public class HarvestRulesTests
{
    private static HarvestCheck Ok() => new()
    {
        Alive = true, IsNode = true, Available = true, DistanceSqCm = 200L * 200, ReachCm = 250, HasJob = true,
        BestToolTier = 1, MinToolTier = 1, HasRoom = true, Busy = false,
    };

    [Fact]
    public void Every_check_passing_is_none()
    {
        Assert.Equal(HarvestReason.None, HarvestRules.Check(Ok()));
    }

    [Fact]
    public void Each_failing_check_gives_its_reason_in_order()
    {
        var c = Ok();
        c.Busy = true;
        Assert.Equal(HarvestReason.ActionBusy, HarvestRules.Check(c));
        c.HasRoom = false;
        Assert.Equal(HarvestReason.BagFull, HarvestRules.Check(c));
        c.MinToolTier = 2;
        Assert.Equal(HarvestReason.ToolTierTooLow, HarvestRules.Check(c));
        c.BestToolTier = 0;
        Assert.Equal(HarvestReason.ToolRequired, HarvestRules.Check(c));
        c.HasJob = false;
        Assert.Equal(HarvestReason.JobRequired, HarvestRules.Check(c));
        c.DistanceSqCm = 251L * 251;
        Assert.Equal(HarvestReason.OutOfRange, HarvestRules.Check(c));
        c.Available = false;
        Assert.Equal(HarvestReason.NodeDepleted, HarvestRules.Check(c));
        c.IsNode = false;
        Assert.Equal(HarvestReason.NodeNotFound, HarvestRules.Check(c));
        c.Alive = false;
        Assert.Equal(HarvestReason.HarvestWhileDead, HarvestRules.Check(c));
    }

    [Fact]
    public void The_reach_is_inclusive_and_the_distance_never_overflows()
    {
        Assert.True(HarvestRules.InReach(250L * 250, 250));
        Assert.False(HarvestRules.InReach(250L * 250 + 1, 250));
        Assert.Equal(2L * int.MaxValue * (long)int.MaxValue, HarvestRules.DistanceSq(int.MaxValue, int.MaxValue, 0, 0));
    }
}
