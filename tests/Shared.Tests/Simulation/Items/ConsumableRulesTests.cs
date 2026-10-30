using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace Shared.Tests.Simulation.Items;

/// <summary>
/// The meter caps of consumable use as pure functions (R-3.4-03 "you cannot overeat"; spec 3.4 consumable use, WP-M3-3).
/// Caps 100, decay 10 and 5 points a minute: the placeholder <c>ConsumptionMeterSettings</c>.
/// </summary>
public class ConsumableRulesTests
{
    private static readonly ConsumptionMeterSettings Meters = new()
    {
        ToxicityMax = 100, ToxicityDecayPerMinute = 10, SatietyMax = 100, SatietyDecayPerMinute = 5,
    };

    private static ConsumableDef Potion(int toxicity = 25, int cooldownMs = 0) =>
        new() { Id = "potion", Kind = ConsumableKind.Potion, HealHp = 60, Toxicity = toxicity, CooldownMs = cooldownMs };

    private static ConsumableDef Food(int satiety = 30) =>
        new() { Id = "bread", Kind = ConsumableKind.Food, StatusId = "wellFed", Satiety = satiety };

    [Theory]
    [InlineData(0, UseItemReason.None)]
    [InlineData(75_000, UseItemReason.None)] // reaches the cap exactly
    [InlineData(75_001, UseItemReason.TooToxic)] // a thousandth over
    [InlineData(100_000, UseItemReason.TooToxic)]
    public void A_potion_is_refused_when_its_toxicity_would_pass_the_cap(long toxicityMilli, UseItemReason expected)
    {
        Assert.Equal(expected, ConsumableRules.Check(Potion(), toxicityMilli, 0, Meters));
    }

    [Theory]
    [InlineData(70_000, UseItemReason.None)]
    [InlineData(70_001, UseItemReason.TooFull)]
    public void Food_is_refused_when_its_satiety_would_pass_the_cap(long satietyMilli, UseItemReason expected)
    {
        Assert.Equal(expected, ConsumableRules.Check(Food(), 0, satietyMilli, Meters));
    }

    [Fact]
    public void Each_meter_limits_only_its_own_kind()
    {
        Assert.Equal(UseItemReason.None, ConsumableRules.Check(Potion(), 0, 100_000, Meters)); // full stomach, potion fine
        Assert.Equal(UseItemReason.None, ConsumableRules.Check(Food(), 100_000, 0, Meters)); // poisoned, bread fine
    }

    [Fact]
    public void Max_uses_per_hour_follow_the_meter_decay_and_the_cooldown()
    {
        Assert.Equal(24, ConsumableRules.MaxUsesPerHour(Potion(), Meters)); // 600 points an hour / 25
        Assert.Equal(10, ConsumableRules.MaxUsesPerHour(Food(), Meters)); // 300 / 30
        Assert.Equal(12, ConsumableRules.MaxUsesPerHour(Potion(cooldownMs: 300_000), Meters)); // the cooldown binds
        var noDecay = new ConsumptionMeterSettings { ToxicityMax = 100, SatietyMax = 100 };
        Assert.Equal(0, ConsumableRules.MaxUsesPerHour(Potion(), noDecay));
    }
}
