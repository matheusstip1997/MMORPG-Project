using GameServer.Host;
using GameServer.Tests.Support;

namespace GameServer.Tests.Host;

/// <summary>Spec 1.6 and architecture rule 12: a fixed 20 Hz tick that does not drift.</summary>
public class TickLoopTests
{
    private const long Period = 50_000; // 20 Hz in microseconds

    private static (TickLoop Loop, List<long> Starts) Create(FakeTickClock clock, Func<int, long> workMicros, int rateHz = 20)
    {
        var starts = new List<long>();
        var loop = new TickLoop(clock, rateHz, () =>
        {
            starts.Add(clock.NowMicros);
            clock.Advance(workMicros(starts.Count - 1));
        });
        return (loop, starts);
    }

    [Fact]
    public void Ticks_start_on_an_absolute_schedule_whatever_each_tick_costs()
    {
        var clock = new FakeTickClock();
        long t0 = clock.NowMicros;
        // Work between 0 and 49 ms: always within the 50 ms budget.
        var (loop, starts) = Create(clock, n => n * 7_919 % Period);

        for (int i = 0; i < 1_000; i++)
            loop.RunOne();

        for (int n = 0; n < starts.Count; n++)
            Assert.Equal(t0 + n * Period, starts[n]);
        Assert.Equal(0, loop.Overruns);
        Assert.Equal(1_000, loop.TicksRun);
    }

    [Fact]
    public void A_late_tick_does_not_shift_the_ticks_after_it()
    {
        var clock = new FakeTickClock();
        long t0 = clock.NowMicros;
        // Tick 3 takes 70 ms: tick 4 starts 20 ms late, tick 5 is back on time.
        var (loop, starts) = Create(clock, n => n == 3 ? 70_000 : 1_000);

        for (int i = 0; i < 6; i++)
            loop.RunOne();

        Assert.Equal(t0 + 4 * Period + 20_000, starts[4]);
        Assert.Equal(t0 + 5 * Period, starts[5]);
        Assert.Equal(0, loop.Overruns);
        Assert.Equal(70_000, loop.MaxTickMicros);
    }

    [Fact]
    public void A_tick_a_full_period_late_restarts_the_schedule_instead_of_bursting()
    {
        var clock = new FakeTickClock();
        long t0 = clock.NowMicros;
        // Tick 1 takes 130 ms: tick 2 (due at 100 ms) would start 80 ms late.
        var (loop, starts) = Create(clock, n => n == 1 ? 130_000 : 1_000);

        for (int i = 0; i < 5; i++)
            loop.RunOne();

        long restart = t0 + Period + 130_000;
        Assert.Equal(restart, starts[2]);
        Assert.Equal(restart + Period, starts[3]);
        Assert.Equal(restart + 2 * Period, starts[4]);
        Assert.Equal(1, loop.Overruns);
    }

    [Fact]
    public void Rates_that_do_not_divide_a_second_still_land_exactly_on_each_second()
    {
        var clock = new FakeTickClock();
        long t0 = clock.NowMicros;
        var (loop, starts) = Create(clock, _ => 0, rateHz: 30);

        for (int i = 0; i < 61; i++)
            loop.RunOne();

        Assert.Equal(t0 + 1_000_000, starts[30]);
        Assert.Equal(t0 + 2_000_000, starts[60]);
    }

    [Fact]
    public void Run_stops_when_cancelled()
    {
        var clock = new FakeTickClock();
        using var stop = new CancellationTokenSource();
        var loop = new TickLoop(clock, 20, () =>
        {
            clock.Advance(1_000);
            if (clock.NowMicros > 2_000_000)
                stop.Cancel();
        });

        loop.Run(stop.Token);

        Assert.Equal(21, loop.TicksRun);
        Assert.Equal(1_000, loop.LastTickMicros);
    }

    [Fact]
    public void Real_clock_keeps_the_rate_over_half_a_second()
    {
        var clock = new StopwatchTickClock();
        var loop = new TickLoop(clock, 20, () => { });
        long start = clock.NowMicros;

        for (int i = 0; i < 11; i++)
            loop.RunOne();

        // Tick 10 starts at 500 ms. Windows sleep granularity can make it a little late, never early.
        long elapsed = clock.NowMicros - start;
        Assert.InRange(elapsed, 10 * Period, 10 * Period + Period);
    }
}
