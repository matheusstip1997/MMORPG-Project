using Shared.Simulation.Economy;

namespace Shared.Tests.Simulation.Economy;

/// <summary>
/// The demand loop's pure math (R-4.2-03, WP-M2-4): curve interpolation, ratio, decay, clamp, smoothing, and the loop's
/// stability over simulated hours (it settles, does not oscillate, never grows without bound, and recovers).
/// </summary>
public class DemandMathTests
{
    // The placeholder shapes of data/tables/DemandCurve.json.
    private static readonly DemandCurveShape Spawn = new([0, 500, 1000, 2000, 4000], [80, 90, 100, 130, 200]);
    private static readonly DemandCurveShape Price = new([0, 1000, 2000, 4000, 8000], [100, 100, 80, 55, 30]);

    [Theory]
    [InlineData(-5, 80)] // flat before the first point
    [InlineData(0, 80)]
    [InlineData(250, 85)] // halfway between 80 and 90
    [InlineData(1000, 100)]
    [InlineData(1500, 115)]
    [InlineData(3000, 165)]
    [InlineData(4000, 200)]
    [InlineData(1_000_000, 200)] // flat after the last point
    public void The_curve_interpolates_between_points_and_is_flat_beyond_them(long ratio, int expected)
    {
        Assert.Equal(expected, Spawn.Evaluate(ratio));
    }

    [Fact]
    public void A_falling_curve_rounds_toward_the_lower_point()
    {
        Assert.Equal(87, Price.Evaluate(1650)); // 100 - 20 x 0.65 = 87
    }

    [Fact]
    public void The_ratio_is_decayed_over_expected_in_thousandths_and_capped()
    {
        Assert.Equal(0, DemandMath.RatioPermille(0, 3_000));
        Assert.Equal(1000, DemandMath.RatioPermille(3_000, 3_000));
        Assert.Equal(2500, DemandMath.RatioPermille(7_500, 3_000));
        Assert.Equal(DemandMath.MaxRatioPermille, DemandMath.RatioPermille(long.MaxValue / 2, 3_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => DemandMath.RatioPermille(1, 0));
    }

    [Fact]
    public void Decay_keeps_the_retained_thousandths_rounded_down()
    {
        Assert.Equal(9_500, DemandMath.Decay(10_000, 950));
        Assert.Equal(0, DemandMath.Decay(1, 950));
        Assert.Equal(10_000, DemandMath.Decay(10_000, 1000));
    }

    [Theory]
    [InlineData(100, 200, 10, 110)]
    [InlineData(100, 50, 10, 90)]
    [InlineData(100, 105, 10, 105)]
    public void Smoothing_moves_at_most_the_step(int current, int target, int step, int expected)
    {
        Assert.Equal(expected, DemandMath.Smooth(current, target, step));
    }

    [Fact]
    public void A_step_clamps_the_curve_then_smooths_toward_it()
    {
        // The curve says 200 at a ratio of 4.0, the clamp allows 150, the smoothing 10 per step.
        Assert.Equal(110, DemandMath.Step(100, Spawn, 4000, 50, 150, 10));
        int value = 100;
        for (int i = 0; i < 20; i++)
            value = DemandMath.Step(value, Spawn, 4000, 50, 150, 10);
        Assert.Equal(150, value);
    }

    [Fact]
    public void A_constant_kill_rate_settles_on_one_value_without_oscillating()
    {
        // 24 simulated hours of one-minute steps: 3 kills a minute of one type, 5 players online (expected 15 kills).
        const int retention = 950;
        long expected = DemandMath.ExpectedMilli(5, 3, 3_000);
        long decayed = 0;
        int spawn = 100;
        var values = new List<int>();
        for (int minute = 0; minute < 24 * 60; minute++)
        {
            decayed += 3 * DemandMath.Unit;
            decayed = DemandMath.Decay(decayed, retention);
            spawn = DemandMath.Step(spawn, Spawn, DemandMath.RatioPermille(decayed, expected), 50, 300, 10);
            values.Add(spawn);
        }

        // Steady state: decayed = 3000 x 0.95 / 0.05 = 57 kills, a ratio of 3.8: the curve's 193%.
        Assert.InRange(decayed, 56_000, 57_000);
        var lastHour = values.Skip(values.Count - 60).ToList();
        Assert.Single(lastHour.Distinct());
        Assert.InRange(lastHour[0], 190, 196);
        // No overshoot: never above the value it settles on (the first minutes dip below 100 while the counter fills).
        Assert.All(values, v => Assert.True(v <= lastHour[0]));
        int lowest = values.IndexOf(values.Min());
        for (int i = lowest + 1; i < values.Count; i++)
            Assert.True(values[i] >= values[i - 1], $"minute {i} moved back");
    }

    [Fact]
    public void After_the_hunting_stops_the_multiplier_recovers_to_the_rarely_hunted_value()
    {
        const int retention = 950;
        long expected = DemandMath.ExpectedMilli(5, 3, 3_000);
        long decayed = 200 * DemandMath.Unit; // a heavily hunted type
        int spawn = 300;
        for (int minute = 0; minute < 6 * 60; minute++)
        {
            decayed = DemandMath.Decay(decayed, retention);
            spawn = DemandMath.Step(spawn, Spawn, DemandMath.RatioPermille(decayed, expected), 50, 300, 10);
        }

        Assert.Equal(0, decayed);
        Assert.Equal(80, spawn); // nobody hunts it: a bit faster than the base
    }

    [Fact]
    public void Sold_out_prices_recover_to_full_over_time()
    {
        const int retention = 900;
        long expected = DemandMath.ExpectedMilli(1, 3, 5_000);
        long decayed = 120 * DemandMath.Unit; // 120 horns sold in a burst by a lone seller
        int Percent() => DemandMath.Clamp(Price.Evaluate(DemandMath.RatioPermille(decayed, expected)), 30, 100);

        Assert.Equal(30, Percent());
        int minutes = 0;
        while (Percent() < 100)
        {
            decayed = DemandMath.Decay(decayed, retention);
            minutes++;
            Assert.True(minutes < 120, "the price never recovered");
        }
        Assert.InRange(minutes, 15, 30); // about 20 minutes with the placeholder retention
    }

    [Fact]
    public void The_decayed_counter_is_bounded_by_the_input_rate()
    {
        // Rule 12.5: no loop with unbounded growth. Even 1000 kills a minute settle at rate / (1 - retention).
        long decayed = 0;
        for (int minute = 0; minute < 10_000; minute++)
            decayed = DemandMath.Decay(decayed + 1000 * DemandMath.Unit, 950);
        Assert.InRange(decayed, 18_000_000, 19_000_000);
    }
}
