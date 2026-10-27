using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Progression;
using Shared.Simulation.Stats;

namespace Shared.Tests.Simulation.Progression;

/// <summary>
/// The exp formula of R-3.6-03 (spec 3.6 "M2 placeholder curves", WP-M2-3) as pure integer math: rank modifier, band
/// modifier, gain and level-ups. The numbers are the placeholder tables' shape; the tests pin the rules, not the tuning.
/// </summary>
public class ExpMathTests
{
    private static readonly int[] Percents = { 50, 75, 100, 125, 150 }; // diffs -2..+2

    [Theory]
    [InlineData(-7, 50)]
    [InlineData(-3, 50)] // clamped below the table
    [InlineData(-2, 50)]
    [InlineData(-1, 75)]
    [InlineData(0, 100)]
    [InlineData(1, 125)]
    [InlineData(2, 150)]
    [InlineData(3, 150)] // clamped above the table
    [InlineData(7, 150)]
    public void Rank_modifier_reads_the_row_of_the_difference_and_clamps_beyond(int diff, int expected) =>
        Assert.Equal(expected, ExpMath.RankPercent(diff, -2, Percents));

    [Theory]
    // Band 15-50 (rank D): inside x1.0 at both ends.
    [InlineData(15, 1_000_000)]
    [InlineData(30, 1_000_000)]
    [InlineData(50, 1_000_000)]
    // Below: +2% per level under the minimum, at most +20%.
    [InlineData(14, 1_020_000)]
    [InlineData(10, 1_100_000)]
    [InlineData(5, 1_200_000)]
    [InlineData(1, 1_200_000)]
    // Above: x0.6 per level over the maximum, multiplied.
    [InlineData(51, 600_000)]
    [InlineData(52, 360_000)]
    [InlineData(53, 216_000)]
    [InlineData(55, 77_760)]
    public void Band_modifier_is_one_inside_a_capped_bonus_below_and_a_multiplied_penalty_above(int level, long expectedPpm) =>
        Assert.Equal(expectedPpm, ExpMath.BandPpm(level, 15, 50, 2, 20, 60));

    [Fact]
    public void Far_above_the_band_the_penalty_reaches_zero_there_is_no_floor()
    {
        Assert.Equal(0, ExpMath.BandPpm(50 + 40, 15, 50, 2, 20, 60));
        Assert.Equal(0, ExpMath.Gain(1_000, 150, ExpMath.BandPpm(50 + 40, 15, 50, 2, 20, 60)));
    }

    [Theory]
    [InlineData(20, 100, 1_000_000, 20)]
    [InlineData(20, 125, 1_000_000, 25)]
    [InlineData(20, 50, 1_200_000, 12)]
    [InlineData(30, 75, 600_000, 13)] // 13.5 rounds down
    [InlineData(31, 100, 360_000, 11)] // 11.16 rounds down
    [InlineData(0, 150, 1_200_000, 0)]
    [InlineData(20, 0, 1_000_000, 0)]
    public void Gain_is_base_times_the_modifiers_rounded_down(long baseExp, int rankPercent, long bandPpm, long expected) =>
        Assert.Equal(expected, ExpMath.Gain(baseExp, rankPercent, bandPpm));

    [Fact]
    public void Gain_does_not_overflow_with_the_largest_table_values() =>
        Assert.Equal(1_800_000_000L, ExpMath.Gain(1_000_000_000, 150, 1_200_000));

    [Fact]
    public void One_gain_can_pay_several_level_ups()
    {
        var curve = new[] { 100, 200, 300, 400 };
        int level = 1;
        long exp = 50;

        int gained = ExpMath.Apply(ref level, ref exp, 50 + 200 + 120, curve, maxLevel: 5);

        Assert.Equal(2, gained);
        Assert.Equal(3, level);
        Assert.Equal(120, exp);
    }

    [Fact]
    public void Exactly_the_exp_a_level_needs_takes_the_level_with_nothing_left()
    {
        int level = 2;
        long exp = 150;

        Assert.Equal(1, ExpMath.Apply(ref level, ref exp, 50, new[] { 100, 200, 300 }, maxLevel: 4));
        Assert.Equal((3, 0L), (level, exp));
    }

    [Fact]
    public void At_the_cap_the_exp_stays_zero_and_the_rest_is_dropped()
    {
        var curve = new[] { 100, 200 };
        int level = 2;
        long exp = 190;

        Assert.Equal(1, ExpMath.Apply(ref level, ref exp, 5_000, curve, maxLevel: 3));
        Assert.Equal((3, 0L), (level, exp));

        Assert.Equal(0, ExpMath.Apply(ref level, ref exp, 5_000, curve, maxLevel: 3));
        Assert.Equal((3, 0L), (level, exp));
    }

    [Fact]
    public void A_zero_gain_changes_nothing()
    {
        int level = 1;
        long exp = 10;

        Assert.Equal(0, ExpMath.Apply(ref level, ref exp, 0, new[] { 100 }, maxLevel: 2));
        Assert.Equal((1, 10L), (level, exp));
    }

    [Fact]
    public void Curves_from_the_committed_tables_apply_rank_and_band_of_the_player()
    {
        var curves = ProgressionCurves.TryBuild(CommittedTables.Load(), out var error);
        Assert.Null(error);
        var demon = new MonsterDef { Level = 1, Rank = Rank.F };
        var boss = new MonsterDef { Level = 3, Rank = Rank.F, BaseExp = 200 };

        Assert.Equal(20, curves!.BaseExpOf(demon));
        Assert.Equal(200, curves.BaseExpOf(boss));
        Assert.Equal(20, curves.Gain(demon, level: 1, Rank.F)); // F band 0-25: x1.0
        Assert.Equal(15, curves.Gain(demon, level: 5, Rank.E)); // one rank lower: x0.75, E band 5-40
        Assert.Equal(12, curves.Gain(demon, level: 1, Rank.D)); // two ranks lower x0.5, 14 below D's band +20% (capped)
        Assert.Equal(12, curves.Gain(demon, level: 26, Rank.F)); // one level above F's band: x0.6
        Assert.Equal(20, curves.Gain(demon, level: 20, Rank.F)); // same rank, inside the band
        Assert.Equal(25, curves.Gain(new MonsterDef { Level = 1, Rank = Rank.E }, level: 20, Rank.F)); // a rank higher: x1.25
    }
}
