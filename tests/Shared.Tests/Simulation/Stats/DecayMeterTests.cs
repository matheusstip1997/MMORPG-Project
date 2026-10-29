using Shared.Simulation.Stats;

namespace Shared.Tests.Simulation.Stats;

/// <summary>Intoxication and satiety (R-3.4-03, WP-M2-2): lazy decay from a UTC anchor, offline time included.</summary>
public class DecayMeterTests
{
    private const long T0 = 1_800_000_000_000; // a UTC ms, as IClock gives it
    private const int Minute = 60_000;

    private static DecayMeter At(int points, long atMs)
    {
        var meter = new DecayMeter();
        meter.Set((long)points * DecayMeter.MilliPerPoint, atMs);
        return meter;
    }

    [Fact]
    public void It_decays_its_rate_per_minute_and_reads_whole_points_rounded_up()
    {
        var meter = At(100, T0);
        Assert.Equal(100, meter.Current(T0, 10));
        Assert.Equal(90, meter.Current(T0 + Minute, 10));
        Assert.Equal(89_834, meter.MilliAt(T0 + Minute + 1_000, 10)); // 10 per minute: 10166.67 thousandths in 61 s, rounded down
        Assert.Equal(90, meter.Current(T0 + Minute + 1_000, 10));     // 89.83 shows as 90
        Assert.Equal(50, meter.Current(T0 + 5 * Minute, 10));
    }

    [Fact]
    public void It_never_goes_below_zero_even_after_a_long_absence()
    {
        var meter = At(100, T0);
        Assert.Equal(0, meter.Current(T0 + 10 * Minute, 10));
        Assert.Equal(0, meter.MilliAt(T0 + 365L * 24 * 60 * Minute, 10)); // a year offline does not overflow
        Assert.Equal(1, meter.Current(T0 + 10 * Minute - 1, 10));       // empty only at the exact end
    }

    [Fact]
    public void A_saved_meter_decays_across_the_offline_gap_from_its_saved_time()
    {
        var online = At(80, T0);
        online.Settle(T0 + 2 * Minute, 5);            // logout save: 70 at T0 + 2 min
        long saved = online.MilliAt(T0 + 2 * Minute, 5);
        Assert.Equal(70_000, saved);

        var loaded = new DecayMeter();
        loaded.Set(saved, T0 + 2 * Minute);           // login 6 minutes later
        Assert.Equal(40, loaded.Current(T0 + 8 * Minute, 5));
    }

    [Fact]
    public void A_clock_that_goes_backwards_decays_nothing()
    {
        var meter = At(50, T0);
        Assert.Equal(50, meter.Current(T0 - Minute, 10));
        meter.Settle(T0 - Minute, 10);
        Assert.Equal(40, meter.Current(T0 + Minute, 10)); // the anchor stayed at T0
    }

    [Fact]
    public void Add_is_capped_at_the_max_and_returns_the_points_gained()
    {
        var meter = At(90, T0);
        Assert.Equal(10, meter.Add(25, T0, max: 100, decayPerMinute: 10));
        Assert.Equal(100, meter.Current(T0, 10));
        Assert.Equal(0, meter.Add(5, T0, 100, 10));        // you cannot overeat (R-3.4-03)
        Assert.Equal(0, meter.Add(0, T0, 100, 10));
        Assert.Equal(90, meter.Current(T0 + Minute, 10));
        Assert.Equal(10, meter.Add(10, T0 + Minute, 100, 10));
    }

    [Fact]
    public void No_decay_rate_keeps_the_value()
    {
        var meter = At(30, T0);
        Assert.Equal(30, meter.Current(T0 + 1_000 * Minute, 0));
    }
}
