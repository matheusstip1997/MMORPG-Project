using Shared.Data.Tables;
using Shared.Simulation.Stats;
using Shared.Util;
using static Shared.Tests.Simulation.Stats.StatsTestConfig;

namespace Shared.Tests.Simulation.Stats;

/// <summary>
/// Backlog D6 in damage formula v1 (R-3.4-04): step 5 with the 10 elements of R-3.4-07 by <see cref="Element"/>, step 7
/// from the damage dealt and taken stats (status modifiers), and status damage that never crits.
/// </summary>
public class DamageElementTests
{
    private readonly CombatConfig _config = Build();

    private (StatBlock Attacker, StatBlock Defender) Blocks(int resist, Element element)
    {
        var attacker = new StatBlock(_config.Stats);
        attacker.SetBase(_config.Ids.Matk, 100);
        var defender = new StatBlock(_config.Stats);
        if (element != Element.None)
            defender.SetBase(_config.ElementResistStat(_config.ElementIndex(element)), resist);
        return (attacker, defender);
    }

    private int Damage(StatBlock attacker, StatBlock defender, Element element, int roll = 99)
    {
        var input = DamageCalc.Gather(_config, attacker, Rank.F, defender, Rank.F, DamageKind.Magic, 100, 0, _config.ElementIndex(element));
        return DamageCalc.ComputeWithRoll(input, _config, roll).Damage;
    }

    [Theory]
    [InlineData(Element.Fire, 0, 100)]
    [InlineData(Element.Ice, 40, 60)]
    [InlineData(Element.Poison, 75, 25)]
    [InlineData(Element.Blood, 90, 25)]   // resistance capped at 75
    [InlineData(Element.Holy, -30, 130)]  // weakness
    [InlineData(Element.Dark, -80, 150)]  // weakness capped at -50
    public void Each_element_is_resisted_by_its_own_stat_within_its_caps(Element element, int resist, int expected)
    {
        var (attacker, defender) = Blocks(resist, element);
        Assert.Equal(expected, Damage(attacker, defender, element));
    }

    [Fact]
    public void Another_elements_resistance_does_not_count_and_none_skips_step_5()
    {
        var (attacker, defender) = Blocks(75, Element.Water);
        Assert.Equal(100, Damage(attacker, defender, Element.Wind));
        Assert.Equal(100, Damage(attacker, defender, Element.None));
        Assert.Equal(25, Damage(attacker, defender, Element.Water));
    }

    [Fact]
    public void Every_element_has_a_caps_row_and_none_has_no_index()
    {
        for (int value = 1; value <= Elements.Count; value++)
        {
            int index = _config.ElementIndex((Element)value);
            Assert.InRange(index, 0, _config.ElementCount - 1);
            Assert.True(Elements.TryParse(_config.ElementId(index), out var parsed));
            Assert.Equal((Element)value, parsed);
        }
        Assert.Equal(DamageCalc.NoElement, _config.ElementIndex(Element.None));
    }

    [Fact]
    public void Step_7_adds_the_attackers_damage_dealt_and_the_defenders_damage_taken()
    {
        var (attacker, defender) = Blocks(0, Element.None);
        attacker.Add(new StatModifier(_config.Ids.DamageDealtPercent, 30, 0, StatSourceType.Buff, 1));
        defender.Add(new StatModifier(_config.Ids.DamageTakenPercent, -20, 0, StatSourceType.Buff, 2)); // Magic Barrier
        var input = DamageCalc.Gather(_config, attacker, Rank.F, defender, Rank.F, DamageKind.Magic, 100, 0, DamageCalc.NoElement);
        Assert.Equal(10, input.ExtraPercent);
        Assert.Equal(110, DamageCalc.ComputeWithRoll(input, _config, 99).Damage);

        defender.RemoveSource(StatSourceType.Buff, 2);
        Assert.Equal(130, Damage(attacker, defender, Element.None));
    }

    [Fact]
    public void Status_damage_never_crits_and_draws_no_roll()
    {
        var (attacker, defender) = Blocks(0, Element.None);
        var input = DamageCalc.Gather(_config, attacker, Rank.F, defender, Rank.F, DamageKind.Magic, 100, 0, DamageCalc.NoElement);
        input.CritRatePercent = 100;
        input.NoCrit = true;
        var rng = new CountingRng();
        var result = DamageCalc.Compute(input, _config, rng);
        Assert.False(result.IsCrit);
        Assert.Equal(100, result.Damage);
        Assert.Equal(0, rng.Draws);
        Assert.False(DamageCalc.ComputeWithRoll(input, _config, 0).IsCrit);
    }

    private sealed class CountingRng : IRng
    {
        public int Draws { get; private set; }

        public uint NextUInt() { Draws++; return 0; }

        public int NextInt(int maxExclusive) { Draws++; return 0; }

        public int NextInt(int minInclusive, int maxExclusive) { Draws++; return minInclusive; }

        public bool RollPercent(int percent) { Draws++; return true; }
    }
}
