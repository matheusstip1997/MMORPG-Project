using GameServer.Combat;
using GameServer.Tests.Support;

namespace GameServer.Tests.Combat;

/// <summary>
/// R-3.3-01 poise on the repository's starting values (20 Hz): regeneration 25% of the bar per second from 2 s after the
/// last poise hit (40 ticks), a 1 s stagger, 1.5 s (30 ticks) of stagger immunity during which poise damage is ignored.
/// </summary>
public class PoiseSystemTests
{
    private static readonly CombatRules Rules = TestCombat.Rules();

    private static PoiseState Bar(int current, uint lastHitTick, int max = 100) =>
        new() { Current = current, Max = max, LastHitTick = lastHitTick };

    [Fact]
    public void The_starting_values_are_read_in_ticks()
    {
        Assert.Equal(40, Rules.PoiseRegenDelayTicks);
        Assert.Equal(25, Rules.PoiseRegenPercentPerSec);
        Assert.Equal(1_000, Rules.StaggerMs);
        Assert.Equal(30, Rules.StaggerImmunityTicks);
    }

    [Theory]
    [InlineData(100u, 50)] // the hit's tick
    [InlineData(140u, 50)] // the delay just ended
    [InlineData(141u, 51)] // 1 tick = 1.25 points, rounded down
    [InlineData(160u, 75)] // 1 s of regeneration = 25% of 100
    [InlineData(180u, 100)]
    [InlineData(5_000u, 100)] // never above the max
    public void Regeneration_is_lazy_from_the_last_poise_hit(uint tick, int expected) =>
        Assert.Equal(expected, PoiseSystem.CurrentAt(Bar(50, lastHitTick: 100), tick, Rules));

    [Fact]
    public void A_hit_subtracts_from_the_regenerated_bar_and_restarts_the_delay()
    {
        var poise = Bar(50, lastHitTick: 100);

        Assert.False(PoiseSystem.Apply(ref poise, 10, 160, Rules)); // 75 regenerated, minus 10

        Assert.Equal(65, poise.Current);
        Assert.Equal(160u, poise.LastHitTick);
        Assert.Equal(65, PoiseSystem.CurrentAt(poise, 200, Rules)); // the delay counts from the new hit
    }

    [Fact]
    public void Reaching_zero_breaks_the_bar_resets_it_and_starts_stagger_immunity()
    {
        var poise = Bar(10, lastHitTick: 100);

        Assert.True(PoiseSystem.Apply(ref poise, 10, 105, Rules));

        Assert.Equal(100, poise.Current);
        Assert.Equal(105u + 30, poise.ImmuneUntilTick);
        Assert.True(PoiseSystem.IsImmune(poise, 134));
        Assert.False(PoiseSystem.IsImmune(poise, 135));
    }

    [Fact]
    public void Poise_damage_is_ignored_during_stagger_immunity()
    {
        var poise = Bar(100, lastHitTick: 100);
        poise.ImmuneUntilTick = 130;

        Assert.False(PoiseSystem.Apply(ref poise, 500, 129, Rules));
        Assert.Equal(100, poise.Current);
        Assert.Equal(100u, poise.LastHitTick);

        Assert.True(PoiseSystem.Apply(ref poise, 500, 130, Rules)); // a full bar's worth again once it ends
    }

    [Fact]
    public void A_hit_without_poise_damage_changes_nothing()
    {
        var poise = Bar(40, lastHitTick: 100);

        Assert.False(PoiseSystem.Apply(ref poise, 0, 120, Rules));

        Assert.Equal(40, poise.Current);
        Assert.Equal(100u, poise.LastHitTick);
    }

    [Fact]
    public void Ticks_wrapping_around_keep_working()
    {
        var poise = Bar(50, lastHitTick: uint.MaxValue - 10);
        Assert.Equal(75, PoiseSystem.CurrentAt(poise, 49, Rules)); // 60 ticks later: 40 of delay, 20 of regeneration
    }
}
