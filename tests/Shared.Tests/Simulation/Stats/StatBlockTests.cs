using Shared.Simulation.Stats;
using static Shared.Tests.Simulation.Stats.StatsTestConfig;

namespace Shared.Tests.Simulation.Stats;

/// <summary>StatBlock (spec 3.4 implementation): stacking, clamping, sources, expiry by UTC ms, cache invalidation.</summary>
public class StatBlockTests
{
    private readonly CombatConfig _config = Build();
    private int Atk => _config.Ids.Atk;
    private int Str => _config.Stats.IdFor("str");
    private int FireRes => _config.Stats.IdFor("fireRes");

    private StatBlock Block(int atkBase = 100)
    {
        var block = new StatBlock(_config.Stats);
        block.SetBase(Atk, atkBase);
        return block;
    }

    private StatModifier Mod(int stat, int flat = 0, int percent = 0, StatSourceType type = StatSourceType.Gear,
        long source = 1, long expiresAt = 0) => new(stat, flat, percent, type, source, expiresAt);

    // ---- Stacking and clamping ----

    [Fact]
    public void Flats_add_to_base_and_percents_add_up_before_multiplying()
    {
        var block = Block(100);
        block.Add(Mod(Atk, flat: 20));
        block.Add(Mod(Atk, percent: 10, source: 2));
        block.Add(Mod(Atk, percent: 15, type: StatSourceType.Buff, source: 3));

        Assert.Equal(150, block.Get(Atk)); // (100 + 20) x (100 + 25) / 100
        Assert.Equal(100, block.GetBase(Atk));
    }

    [Theory]
    [InlineData(100, 0, -50, 50)]
    [InlineData(100, 0, -150, 0)]       // percent sum floored at -100
    [InlineData(10, 5, 33, 19)]         // 19.95 rounds down
    [InlineData(-1, 0, 50, -2)]         // -1.5 floors toward negative infinity
    [InlineData(-3, 0, -50, -2)]        // -1.5 again
    [InlineData(0, -20, 0, -20)]
    [InlineData(600, 600, 0, 1000)]     // StatDef max of str
    [InlineData(-600, -600, 0, -1000)]  // StatDef min of str
    public void Final_value_floors_and_clamps_to_the_stat_def(int baseValue, int flat, int percent, int expected)
    {
        var block = new StatBlock(_config.Stats);
        block.SetBase(Str, baseValue);
        block.Add(Mod(Str, flat, percent));

        Assert.Equal(expected, block.Get(Str));
    }

    [Fact]
    public void A_stat_below_its_min_is_raised_to_the_min()
    {
        var block = new StatBlock(_config.Stats);
        block.Add(Mod(FireRes, flat: -5_000));
        Assert.Equal(-900, block.Get(FireRes));

        block.Add(Mod(Atk, flat: -10)); // atk min 0
        Assert.Equal(0, block.Get(Atk));
    }

    [Fact]
    public void Huge_modifier_sums_saturate_instead_of_wrapping()
    {
        var block = Block(int.MaxValue);
        for (int i = 0; i < 10; i++)
            block.Add(Mod(Atk, flat: int.MaxValue, percent: int.MaxValue, source: i));
        Assert.Equal(1_000_000_000, block.Get(Atk));

        // Twelve minimums outweigh the base and the ten maximums: the flat sum ends below int.MinValue.
        for (int i = 0; i < 12; i++)
            block.Add(Mod(Atk, flat: int.MinValue, percent: int.MinValue, source: 100 + i));
        Assert.Equal(0, block.Get(Atk));
    }

    [Fact]
    public void The_result_does_not_depend_on_the_order_modifiers_were_added()
    {
        var mods = new[]
        {
            Mod(Atk, flat: 7), Mod(Atk, percent: 13, source: 2), Mod(Atk, flat: -3, percent: -4, source: 3),
            Mod(Str, flat: 9, source: 4), Mod(Atk, percent: 21, source: 5),
        };
        var forward = Block(37);
        var backward = Block(37);
        foreach (var mod in mods) forward.Add(mod);
        foreach (var mod in mods.Reverse()) backward.Add(mod);

        Assert.Equal(forward.Get(Atk), backward.Get(Atk));
        Assert.Equal(forward.Get(Str), backward.Get(Str));
    }

    // ---- Sources ----

    [Fact]
    public void RemoveSource_removes_only_that_type_and_id_and_keeps_order()
    {
        var block = Block(100);
        block.Add(Mod(Atk, flat: 10, type: StatSourceType.Gear, source: 5));
        block.Add(Mod(Atk, flat: 20, type: StatSourceType.Buff, source: 5));
        block.Add(Mod(Str, flat: 1, type: StatSourceType.Gear, source: 5));
        block.Add(Mod(Atk, flat: 40, type: StatSourceType.Gear, source: 6));

        Assert.Equal(2, block.RemoveSource(StatSourceType.Gear, 5));

        Assert.Equal(2, block.ModifierCount);
        Assert.Equal(20, block.ModifierAt(0).Flat);
        Assert.Equal(40, block.ModifierAt(1).Flat);
        Assert.Equal(160, block.Get(Atk));
        Assert.Equal(0, block.Get(Str));
        Assert.Equal(0, block.RemoveSource(StatSourceType.Gear, 5));
    }

    [Fact]
    public void The_modifier_list_grows_past_its_initial_capacity()
    {
        var block = new StatBlock(_config.Stats, modifierCapacity: 1);
        for (int i = 0; i < 40; i++)
            block.Add(Mod(Str, flat: 1, source: i));

        Assert.Equal(40, block.ModifierCount);
        Assert.Equal(40, block.Get(Str));
    }

    [Fact]
    public void Unknown_stats_are_refused()
    {
        var block = new StatBlock(_config.Stats);
        Assert.Throws<ArgumentOutOfRangeException>(() => block.Add(Mod(_config.Stats.Count)));
        Assert.Throws<ArgumentOutOfRangeException>(() => block.Add(Mod(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => block.SetBase(_config.Stats.Count, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => block.ModifierAt(0));
    }

    // ---- Expiry by UTC ms ----

    [Fact]
    public void A_modifier_is_active_until_its_expiry_time_and_removed_at_it()
    {
        var block = Block(100);
        block.Add(Mod(Atk, flat: 50, type: StatSourceType.Buff, expiresAt: 10_000));
        block.Add(Mod(Atk, flat: 5, type: StatSourceType.Gear, source: 2)); // permanent

        Assert.Equal(10_000, block.NextExpiryMs);
        Assert.Equal(0, block.ExpireModifiers(9_999));
        Assert.Equal(155, block.Get(Atk));

        Assert.Equal(1, block.ExpireModifiers(10_000));
        Assert.Equal(105, block.Get(Atk));
        Assert.Equal(long.MaxValue, block.NextExpiryMs);
        Assert.Equal(0, block.ExpireModifiers(long.MaxValue)); // permanent ones never expire
    }

    [Fact]
    public void Next_expiry_follows_the_earliest_remaining_modifier()
    {
        var block = Block(100);
        block.Add(Mod(Atk, flat: 1, source: 1, expiresAt: 3_000));
        block.Add(Mod(Atk, flat: 2, source: 2, expiresAt: 1_000));
        block.Add(Mod(Atk, flat: 4, source: 3, expiresAt: 2_000));
        Assert.Equal(1_000, block.NextExpiryMs);

        Assert.Equal(2, block.ExpireModifiers(2_000));
        Assert.Equal(3_000, block.NextExpiryMs);
        Assert.Equal(101, block.Get(Atk));

        block.RemoveSource(StatSourceType.Gear, 1);
        Assert.Equal(long.MaxValue, block.NextExpiryMs);
    }

    [Fact]
    public void A_modifier_added_already_expired_goes_on_the_next_expiry_pass()
    {
        var block = Block(100);
        block.Add(Mod(Atk, flat: 10, expiresAt: 500));

        Assert.Equal(1, block.ExpireModifiers(1_000));
        Assert.Equal(100, block.Get(Atk));
    }

    // ---- Cache ----

    [Fact]
    public void Values_are_recomputed_only_after_a_change()
    {
        var block = Block(100);
        block.Get(Atk);
        int recomputes = block.RecomputeCount;

        block.Get(Atk);
        block.Get(Str);
        block.ExpireModifiers(1_000_000); // nothing due
        block.SetBase(Atk, 100);          // same value
        block.RemoveSource(StatSourceType.Food, 9);
        Assert.Equal(recomputes, block.RecomputeCount);

        block.Add(Mod(Atk, flat: 1));
        Assert.Equal(recomputes, block.RecomputeCount); // lazy: nothing read yet
        Assert.Equal(101, block.Get(Atk));
        block.Get(Atk);
        Assert.Equal(recomputes + 1, block.RecomputeCount);

        block.SetBase(Atk, 200);
        Assert.Equal(201, block.Get(Atk));
        Assert.Equal(recomputes + 2, block.RecomputeCount);
    }

    [Fact]
    public void Version_changes_only_when_a_final_value_changes()
    {
        var block = Block(100);
        block.Refresh();
        int version = block.Version;

        block.Add(Mod(Atk, flat: 0, percent: 0)); // no effect on any value
        Assert.False(block.Refresh());
        Assert.Equal(version, block.Version);

        block.Add(Mod(Atk, flat: 3, source: 2));
        Assert.True(block.Refresh());
        Assert.Equal(version + 1, block.Version);
        Assert.False(block.Refresh()); // nothing pending
    }

    [Fact]
    public void Reading_and_changing_stats_does_not_allocate()
    {
        var block = new StatBlock(_config.Stats, modifierCapacity: 8);
        void Cycle()
        {
            block.Add(Mod(Atk, flat: 5, type: StatSourceType.Buff, source: 1, expiresAt: 2_000));
            block.Add(Mod(Atk, percent: 10, type: StatSourceType.Gear, source: 2));
            block.SetBase(Atk, 90);
            block.Get(Atk);
            block.ExpireModifiers(2_000);
            block.RemoveSource(StatSourceType.Gear, 2);
            block.SetBase(Atk, 100);
            block.Refresh();
        }

        Cycle(); // warm up the JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
            Cycle();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
