using Shared.Data.Tables;
using Shared.Simulation.Stats;

namespace Shared.Tests.Simulation.Stats;

/// <summary>
/// The pure build-up formulas and meter of spec 3.8 (backlog D6): gain with resistance, caps and weakness (R-3.8-03,
/// R-3.8-10), lazy decay after a delay (R-3.8-06), the trigger lock until the effect ends (R-3.8-02), the repeat penalty
/// (R-3.8-05) and reductions (R-3.8-04). 20 Hz, so 1 s = 20 ticks.
/// </summary>
public class BuildUpMathTests
{
    private const int TickRate = 20;

    private static BuildUpDef Burn() => new()
    {
        Id = "burn", NetId = 1, Element = Element.Fire, StatusId = "burn", DecayDelayMs = 3000, DecayPerSec = 10,
        ResistScalePercent = 100, ResistMinPercent = -50, ResistMaxPercent = 75,
        RepeatPenaltyWindowMs = 10_000, RepeatPenaltyGainPercents = [50, 25],
    };

    private static BuildUpRule Rule(BuildUpDef? def = null) =>
        new(def ?? Burn(), 0, elementIndex: 0, resistStat: 0, decayDelayTicks: 60, penaltyWindowTicks: 200, TickRate);

    [Theory]
    [InlineData(0, 2500)]     // no resistance: 25 points
    [InlineData(15, 2125)]    // 25 x 85% stays exact in units
    [InlineData(75, 625)]     // the cap
    [InlineData(200, 625)]    // above the cap: capped at 75%
    [InlineData(-20, 3000)]   // a weakness raises the gain
    [InlineData(-90, 3750)]   // weakness capped at -50%
    public void A_gain_is_scaled_by_the_capped_resistance(int resist, int units) =>
        Assert.Equal(units, BuildUpMath.GainUnits(Burn(), 25, resist, penaltyPercent: 100));

    [Fact]
    public void The_build_up_formula_scales_the_resistance_stat_on_its_own()
    {
        var def = Burn();
        def.ResistScalePercent = 50; // R-3.8-10: half the damage resistance counts for build-ups
        Assert.Equal(20, BuildUpMath.ResistPercent(def, 40));
        Assert.Equal(2000, BuildUpMath.GainUnits(def, 25, 40, 100));
    }

    [Fact]
    public void Penalty_and_parry_keep_their_percent_of_the_gain()
    {
        Assert.Equal(1250, BuildUpMath.GainUnits(Burn(), 25, 0, penaltyPercent: 50));
        Assert.Equal(625, BuildUpMath.GainUnits(Burn(), 25, 0, penaltyPercent: 50, keepPercent: 50)); // R-3.8-07
        Assert.Equal(0, BuildUpMath.GainUnits(Burn(), 0, 0, 100));
        Assert.Equal(BuildUpMath.MaxUnits, BuildUpMath.GainUnits(Burn(), 100, -50, 100)); // never above a full meter
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 50)]
    [InlineData(2, 25)]
    [InlineData(5, 25)] // beyond the last level: the last one
    public void Penalty_levels_read_their_gain_percent(int level, int percent) =>
        Assert.Equal(percent, BuildUpMath.PenaltyPercent(Burn(), level));

    [Fact]
    public void A_meter_decays_only_after_the_delay_at_a_steady_rate()
    {
        var rule = Rule();
        var meter = new BuildUpMeter();
        Assert.False(meter.Gain(rule, 50, 0, tick: 100));
        Assert.Equal(5000, meter.UnitsAt(rule, 100));
        Assert.Equal(5000, meter.UnitsAt(rule, 160)); // 3 s delay: nothing yet
        Assert.Equal(4950, meter.UnitsAt(rule, 161)); // 10 points per second = 50 units per tick
        Assert.Equal(4000, meter.UnitsAt(rule, 180)); // one second later: 10 points less
        Assert.Equal(0, meter.UnitsAt(rule, 260));
        Assert.Equal(0, meter.UnitsAt(rule, 10_000));
    }

    [Fact]
    public void A_gain_restarts_the_delay_and_a_reduction_does_not()
    {
        var rule = Rule();
        var meter = new BuildUpMeter();
        meter.Gain(rule, 50, 0, 100);
        meter.Gain(rule, 10, 0, 170); // decay settled first: 5000 - 10 ticks x 50 = 4500, then + 1000
        Assert.Equal(5500, meter.UnitsAt(rule, 170));
        Assert.Equal(5500, meter.UnitsAt(rule, 230));
        Assert.True(meter.Reduce(rule, 40, 240)); // 5500 - 10 ticks of decay (500) = 5000, minus 40 points
        Assert.Equal(1000, meter.UnitsAt(rule, 240));
        Assert.Equal(950, meter.UnitsAt(rule, 241)); // still decaying: the reduction did not restart the delay
        Assert.True(meter.Reduce(rule, 40, 241));
        Assert.Equal(0, meter.UnitsAt(rule, 241));
        Assert.False(meter.Reduce(rule, 40, 242)); // nothing left to lower
    }

    [Fact]
    public void A_full_meter_locks_ignores_changes_and_resets_when_its_effect_ends()
    {
        var rule = Rule();
        var meter = new BuildUpMeter();
        Assert.False(meter.Gain(rule, 75, 0, 10));
        Assert.True(meter.Gain(rule, 25, 0, 11)); // R-3.8-02: 100 triggers
        Assert.True(meter.Locked);
        Assert.Equal(1, meter.PenaltyLevel);
        Assert.Equal(BuildUpMath.MaxUnits, meter.UnitsAt(rule, 10_000)); // full while its effect runs, no decay
        Assert.Equal(100, meter.PointsAt(rule, 500));
        Assert.False(meter.Gain(rule, 25, 0, 12));
        Assert.False(meter.Reduce(rule, 40, 12));
        Assert.Equal(BuildUpMath.MaxUnits, meter.UnitsAt(rule, 12));

        Assert.True(meter.Unlock(rule, 91)); // its status ended
        Assert.False(meter.Locked);
        Assert.Equal(0, meter.UnitsAt(rule, 91));
        Assert.False(meter.Unlock(rule, 92));
    }

    [Fact]
    public void Triggers_inside_the_window_deepen_the_penalty_and_one_after_it_starts_again()
    {
        var rule = Rule(); // window 200 ticks, levels 50% then 25%
        var meter = new BuildUpMeter();
        Assert.True(meter.Gain(rule, 100, 0, 0));
        meter.Unlock(rule, 80);                      // window until 280, level 1: 50%
        Assert.True(meter.InPenaltyWindow(279));
        Assert.False(meter.InPenaltyWindow(280));

        meter.Gain(rule, 40, 0, 100);
        Assert.Equal(2000, meter.UnitsAt(rule, 100)); // 40 points at 50%
        Assert.False(meter.Gain(rule, 100, 0, 101)); // 2000 + 100 x 50% = 7000: not full yet
        Assert.Equal(7000, meter.UnitsAt(rule, 101));
        Assert.True(meter.Gain(rule, 60, 0, 102));   // 7000 + 3000: full inside the window
        Assert.Equal(2, meter.PenaltyLevel);
        meter.Unlock(rule, 182);                     // level 2 (25%) until 382
        meter.Gain(rule, 40, 0, 200);
        Assert.Equal(1000, meter.UnitsAt(rule, 200));

        meter.Gain(rule, 40, 0, 400);                // after the window (the 1000 decayed meanwhile): full gains again
        Assert.Equal(4000, meter.UnitsAt(rule, 400));
        Assert.True(meter.Gain(rule, 60, 0, 401));   // a trigger after the window starts at level 1
        Assert.Equal(1, meter.PenaltyLevel);
    }

    [Fact]
    public void Points_round_up_so_a_meter_above_zero_never_shows_zero()
    {
        var rule = Rule();
        var meter = new BuildUpMeter { Units = 1, SettledTick = 5, DecayStartTick = 5 };
        Assert.Equal(1, meter.PointsAt(rule, 5));
        Assert.Equal(0, new BuildUpMeter().PointsAt(rule, 5));
    }
}
