using Shared.Simulation.Actions;

namespace Shared.Tests.Simulation;

/// <summary>Spec 3.3 <c>ChargeMeter</c> (R-3.3-02, R-3.3-03): lazy recharge from ticks, integer only.</summary>
public class ChargeMeterTests
{
    private const int Recharge = 80; // 4 s at 20 Hz

    [Fact]
    public void A_new_meter_is_full_and_stays_full()
    {
        var meter = new ChargeMeter(3, Recharge);
        Assert.Equal(3, meter.ChargesAt(0));
        Assert.Equal(3, meter.ChargesAt(1_000_000));
        Assert.Equal(0, meter.TicksUntilNextCharge(500));
    }

    [Fact]
    public void Spending_from_full_starts_the_recharge_at_that_tick()
    {
        var meter = new ChargeMeter(3, Recharge);
        Assert.True(meter.TrySpend(10));
        Assert.Equal(2, meter.ChargesAt(10));
        Assert.Equal(2, meter.ChargesAt(89));
        Assert.Equal(3, meter.ChargesAt(90)); // exactly one recharge later
        Assert.Equal(Recharge, meter.TicksUntilNextCharge(10));
        Assert.Equal(1, meter.TicksUntilNextCharge(89));
        Assert.Equal(0, meter.TicksUntilNextCharge(90));
    }

    [Fact]
    public void An_empty_meter_refuses_and_refills_one_charge_per_recharge()
    {
        var meter = new ChargeMeter(3, Recharge);
        Assert.True(meter.TrySpend(0));
        Assert.True(meter.TrySpend(0));
        Assert.True(meter.TrySpend(0));
        var before = meter;
        Assert.False(meter.TrySpend(0));
        Assert.Equal(before, meter); // a refused spend changes nothing
        Assert.Equal(0, meter.ChargesAt(79));
        Assert.Equal(1, meter.ChargesAt(80));
        Assert.Equal(1, meter.ChargesAt(159));
        Assert.Equal(2, meter.ChargesAt(160));
        Assert.Equal(3, meter.ChargesAt(240));
        Assert.Equal(3, meter.ChargesAt(100_000)); // capped at max
    }

    [Fact]
    public void Spending_while_recharging_keeps_the_progress_towards_the_next_charge()
    {
        var meter = new ChargeMeter(3, Recharge);
        meter.TrySpend(0);
        Assert.True(meter.TrySpend(40)); // 1 left; the charge started at 0 is half done
        Assert.Equal(1, meter.ChargesAt(79));
        Assert.Equal(2, meter.ChargesAt(80));
        Assert.Equal(3, meter.ChargesAt(160));

        var partial = new ChargeMeter(3, Recharge);
        partial.TrySpend(0);
        partial.TrySpend(0);
        partial.TrySpend(0);
        Assert.True(partial.TrySpend(100)); // one charge back at 80, spent at 100; the next one is due at 160
        Assert.Equal(0, partial.ChargesAt(159));
        Assert.Equal(1, partial.ChargesAt(160));
        Assert.Equal(60, partial.TicksUntilNextCharge(100));
    }

    [Fact]
    public void A_refund_gives_one_charge_back_and_keeps_the_progress()
    {
        var meter = new ChargeMeter(3, Recharge);
        meter.TrySpend(0);
        meter.TrySpend(0);
        meter.Refund(40);
        Assert.Equal(2, meter.ChargesAt(40));
        Assert.Equal(3, meter.ChargesAt(80));
        meter.Refund(500);
        Assert.Equal(3, meter.ChargesAt(500)); // never above max
    }

    [Fact]
    public void Ticks_before_the_last_change_add_nothing_and_ticks_may_wrap()
    {
        var meter = new ChargeMeter(2, Recharge);
        meter.TrySpend(1_000);
        Assert.Equal(1, meter.ChargesAt(10)); // a clock behind the meter

        var wrapping = new ChargeMeter(2, Recharge);
        wrapping.TrySpend(uint.MaxValue - 9);
        Assert.Equal(1, wrapping.ChargesAt(uint.MaxValue));
        Assert.Equal(1, wrapping.ChargesAt(69));
        Assert.Equal(2, wrapping.ChargesAt(70)); // 80 ticks later, across the wrap
    }

    [Fact]
    public void Without_a_recharge_time_the_meter_never_runs_out_and_without_charges_it_never_spends()
    {
        var instant = new ChargeMeter(3, 0);
        for (uint tick = 0; tick < 10; tick++)
            Assert.True(instant.TrySpend(tick));
        Assert.Equal(3, instant.ChargesAt(10));

        var none = new ChargeMeter(0, Recharge);
        Assert.False(none.TrySpend(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChargeMeter(-1, Recharge));
    }

    [Fact]
    public void Many_ticks_match_a_meter_counted_tick_by_tick()
    {
        // Reference: count every tick (what "computed on read" must equal), with random spends over 20 000 ticks.
        var random = new Random(31);
        foreach (int recharge in new[] { 1, 7, 80, 100 })
        {
            var meter = new ChargeMeter(3, recharge);
            int charges = 3, progress = 0;
            for (uint tick = 1; tick <= 20_000; tick++)
            {
                if (charges < 3 && ++progress == recharge)
                {
                    charges++;
                    progress = 0;
                }
                Assert.Equal(charges, meter.ChargesAt(tick));
                if (random.Next(recharge + 3) == 0)
                {
                    bool spent = charges > 0;
                    if (spent)
                    {
                        if (charges == 3)
                            progress = 0;
                        charges--;
                    }
                    Assert.Equal(spent, meter.TrySpend(tick));
                    Assert.Equal(charges, meter.ChargesAt(tick));
                }
                if (charges < 3)
                    Assert.Equal(recharge - progress, meter.TicksUntilNextCharge(tick));
            }
        }
    }
}
