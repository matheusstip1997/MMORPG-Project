using Shared.Simulation.Stats;
using static Shared.Tests.Simulation.Stats.StatsTestConfig;

namespace Shared.Tests.Simulation.Stats;

/// <summary>
/// Resource meters (R-3.4-03): lazy regeneration from timestamps, the regen delay (stamina 20/s after 1 s, spec 12.7),
/// all-or-nothing spends with refusal reasons, damage and heals, max changes, and timing edges.
/// </summary>
public class ResourceMetersTests
{
    private const long T0 = 1_760_000_000_000; // a UTC ms timestamp, as IClock gives

    private readonly CombatConfig _config = Build();

    private (StatBlock Stats, ResourceMeters Meters) Entity(int maxHp = 200, int hpRegen = 7, int maxMp = 50, int mpRegen = 5,
        int maxStamina = 100, int staminaRegen = 20)
    {
        var ids = _config.Ids;
        var stats = new StatBlock(_config.Stats);
        stats.SetBase(ids.MaxHp, maxHp);
        stats.SetBase(ids.HpRegen, hpRegen);
        stats.SetBase(ids.MaxMp, maxMp);
        stats.SetBase(ids.MpRegen, mpRegen);
        stats.SetBase(ids.MaxStamina, maxStamina);
        stats.SetBase(ids.StaminaRegen, staminaRegen);
        return (stats, new ResourceMeters(stats, _config, T0));
    }

    [Fact]
    public void Meters_start_full_at_their_max_stats()
    {
        var (_, meters) = Entity();
        Assert.Equal(200, meters.Current(ResourceKind.Hp, T0));
        Assert.Equal(50, meters.Current(ResourceKind.Mp, T0));
        Assert.Equal(100, meters.Current(ResourceKind.Stamina, T0));
        Assert.Equal(100, meters.Max(ResourceKind.Stamina, T0));
    }

    [Theory]
    [InlineData(0, 70)]
    [InlineData(999, 70)]     // still inside the 1 s delay
    [InlineData(1_000, 70)]   // delay just ended, nothing regenerated yet
    [InlineData(1_049, 70)]   // 0.98 points
    [InlineData(1_050, 71)]   // 20/s x 50 ms = 1 point
    [InlineData(2_000, 90)]
    [InlineData(2_500, 100)]  // full exactly
    [InlineData(60_000, 100)] // never above max
    public void Stamina_regenerates_20_per_second_after_a_1_second_delay(long afterMs, int expected)
    {
        var (_, meters) = Entity();
        Assert.Equal(SpendResult.Ok, meters.TrySpend(ResourceKind.Stamina, 30, T0));

        Assert.Equal(expected, meters.Current(ResourceKind.Stamina, T0 + afterMs));
    }

    [Fact]
    public void Fractional_regeneration_is_exact_however_often_the_meter_is_read()
    {
        var (_, polled) = Entity(hpRegen: 7);
        var (_, once) = Entity(hpRegen: 7);
        polled.Drain(ResourceKind.Hp, 150, T0);
        once.Drain(ResourceKind.Hp, 150, T0);
        long regenStart = T0 + _config.HpRegenDelayMs;

        for (long t = regenStart; t <= regenStart + 3_000; t += 13)
            polled.Current(ResourceKind.Hp, t);

        // 7/s: 1 point needs 142.86 ms
        Assert.Equal(50, polled.Current(ResourceKind.Hp, regenStart + 142));
        Assert.Equal(51, once.Current(ResourceKind.Hp, regenStart + 143));
        Assert.Equal(71, polled.Current(ResourceKind.Hp, regenStart + 3_000));
        Assert.Equal(71, once.Current(ResourceKind.Hp, regenStart + 3_000));
    }

    [Fact]
    public void A_spend_inside_the_delay_restarts_it()
    {
        var (_, meters) = Entity();
        meters.TrySpend(ResourceKind.Stamina, 20, T0);
        meters.TrySpend(ResourceKind.Stamina, 20, T0 + 900);

        Assert.Equal(60, meters.Current(ResourceKind.Stamina, T0 + 1_500));
        Assert.Equal(60, meters.Current(ResourceKind.Stamina, T0 + 1_900));
        Assert.Equal(62, meters.Current(ResourceKind.Stamina, T0 + 2_000));
    }

    [Fact]
    public void A_spend_of_exactly_the_current_value_succeeds()
    {
        var (_, meters) = Entity();
        Assert.Equal(SpendResult.Ok, meters.TrySpend(ResourceKind.Mp, 50, T0));
        Assert.Equal(0, meters.Current(ResourceKind.Mp, T0));
    }

    [Fact]
    public void A_refused_spend_changes_nothing_and_does_not_restart_the_delay()
    {
        var (_, meters) = Entity();
        meters.TrySpend(ResourceKind.Stamina, 95, T0); // 5 left

        Assert.Equal(SpendResult.NotEnough, meters.TrySpend(ResourceKind.Stamina, 10, T0 + 900));
        Assert.Equal(5, meters.Current(ResourceKind.Stamina, T0 + 900));
        Assert.Equal(7, meters.Current(ResourceKind.Stamina, T0 + 1_100)); // regen began at T0 + 1000 as planned
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void Spending_zero_or_less_is_refused(int amount)
    {
        var (_, meters) = Entity();
        Assert.Equal(SpendResult.InvalidAmount, meters.TrySpend(ResourceKind.Stamina, amount, T0));
        Assert.Equal(100, meters.Current(ResourceKind.Stamina, T0));
    }

    [Fact]
    public void A_huge_spend_is_refused_without_overflow()
    {
        var (_, meters) = Entity();
        Assert.Equal(SpendResult.NotEnough, meters.TrySpend(ResourceKind.Stamina, int.MaxValue, T0));
    }

    [Fact]
    public void Drain_removes_up_to_the_current_value_and_hp_does_not_regenerate_at_zero()
    {
        var (_, meters) = Entity();
        Assert.Equal(150, meters.Drain(ResourceKind.Hp, 150, T0));
        Assert.Equal(50, meters.Drain(ResourceKind.Hp, int.MaxValue, T0 + 10));
        Assert.Equal(0, meters.Drain(ResourceKind.Hp, 10, T0 + 20));
        Assert.Equal(0, meters.Drain(ResourceKind.Hp, -10, T0 + 20));

        Assert.Equal(0, meters.Current(ResourceKind.Hp, T0 + 3_600_000)); // dead entities stay at 0

        Assert.Equal(40, meters.Restore(ResourceKind.Hp, 40, T0 + 3_600_000)); // revive
        Assert.Equal(47, meters.Current(ResourceKind.Hp, T0 + 3_601_000));
    }

    [Fact]
    public void Mp_and_stamina_regenerate_from_zero()
    {
        var (_, meters) = Entity();
        meters.TrySpend(ResourceKind.Mp, 50, T0);
        meters.TrySpend(ResourceKind.Stamina, 100, T0);

        Assert.Equal(5, meters.Current(ResourceKind.Mp, T0 + _config.MpRegenDelayMs + 1_000));
        Assert.Equal(20, meters.Current(ResourceKind.Stamina, T0 + 2_000));
    }

    [Fact]
    public void Restore_is_capped_at_max_and_does_not_restart_the_delay()
    {
        var (_, meters) = Entity();
        meters.Drain(ResourceKind.Hp, 30, T0);

        Assert.Equal(10, meters.Restore(ResourceKind.Hp, 10, T0 + 1_000));
        Assert.Equal(20, meters.Restore(ResourceKind.Hp, 500, T0 + 2_000));
        Assert.Equal(0, meters.Restore(ResourceKind.Hp, 0, T0 + 2_000));
        Assert.Equal(200, meters.Current(ResourceKind.Hp, T0 + 2_000));

        meters.Drain(ResourceKind.Hp, 100, T0 + 10_000);
        meters.Restore(ResourceKind.Hp, 10, T0 + 12_000); // inside the 5 s HP delay
        Assert.Equal(117, meters.Current(ResourceKind.Hp, T0 + 16_000)); // 110 + 7: regen began at T0 + 15000
    }

    [Fact]
    public void A_lower_max_clamps_the_current_value_and_a_higher_max_leaves_room_to_regenerate()
    {
        var (stats, meters) = Entity();
        var ids = _config.Ids;
        stats.Add(new StatModifier(ids.MaxStamina, -40, 0, StatSourceType.Debuff, 1));
        Assert.Equal(60, meters.Current(ResourceKind.Stamina, T0));
        Assert.Equal(60, meters.Max(ResourceKind.Stamina, T0));

        stats.RemoveSource(StatSourceType.Debuff, 1);
        Assert.Equal(60, meters.Current(ResourceKind.Stamina, T0 + 10));
        Assert.Equal(80, meters.Current(ResourceKind.Stamina, T0 + 1_010)); // no spend: no delay
    }

    [Fact]
    public void A_regen_change_applies_from_the_moment_the_meters_settle()
    {
        var (stats, meters) = Entity();
        meters.TrySpend(ResourceKind.Stamina, 100, T0);
        Assert.Equal(20, meters.Current(ResourceKind.Stamina, T0 + 2_000));

        stats.Add(new StatModifier(_config.Ids.StaminaRegen, 20, 0, StatSourceType.Buff, 7, T0 + 4_000));
        meters.Settle(T0 + 2_000);
        Assert.Equal(60, meters.Current(ResourceKind.Stamina, T0 + 3_000)); // 20 + 40/s x 1 s

        stats.ExpireModifiers(T0 + 4_000);
        meters.Settle(T0 + 4_000);
        Assert.Equal(100, meters.Current(ResourceKind.Stamina, T0 + 4_000));
    }

    [Fact]
    public void Set_restores_a_saved_value_and_regeneration_starts_at_once()
    {
        var (_, meters) = Entity();
        meters.Set(ResourceKind.Hp, 10, T0);
        Assert.Equal(10, meters.Current(ResourceKind.Hp, T0));
        Assert.Equal(17, meters.Current(ResourceKind.Hp, T0 + 1_000));

        meters.Set(ResourceKind.Hp, 5_000, T0 + 1_000); // clamped on read
        Assert.Equal(200, meters.Current(ResourceKind.Hp, T0 + 1_000));
    }

    [Fact]
    public void A_long_absence_fills_the_meter_without_overflow()
    {
        var (_, meters) = Entity(maxHp: 1_000_000_000, hpRegen: 1_000_000);
        meters.Drain(ResourceKind.Hp, 999_999_999, T0);
        Assert.Equal(1_000_000_000, meters.Current(ResourceKind.Hp, long.MaxValue));
    }

    [Fact]
    public void A_clock_going_backwards_adds_nothing()
    {
        var (_, meters) = Entity();
        meters.TrySpend(ResourceKind.Stamina, 50, T0);
        Assert.Equal(50, meters.Current(ResourceKind.Stamina, T0 - 60_000));
        Assert.Equal(SpendResult.Ok, meters.TrySpend(ResourceKind.Stamina, 10, T0 - 60_000));
        Assert.Equal(40, meters.Current(ResourceKind.Stamina, T0 - 59_000));
        Assert.Equal(42, meters.Current(ResourceKind.Stamina, T0 - 58_900));
    }

    [Fact]
    public void Meters_need_a_stat_block_built_from_the_same_rules()
    {
        var other = new StatBlock(new StatRules(StatRows()));
        Assert.Throws<ArgumentException>(() => new ResourceMeters(other, _config, T0));
    }

    [Fact]
    public void Spending_and_reading_meters_does_not_allocate()
    {
        var (stats, meters) = Entity();
        long now = T0;
        void Cycle()
        {
            now += 50;
            meters.TrySpend(ResourceKind.Stamina, 10, now);
            meters.Current(ResourceKind.Stamina, now);
            meters.Drain(ResourceKind.Hp, 1, now);
            meters.Restore(ResourceKind.Hp, 1, now);
            stats.SetBase(_config.Ids.MaxMp, (int)(now % 2) + 50);
            meters.Current(ResourceKind.Mp, now);
        }

        Cycle(); // warm up the JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
            Cycle();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
