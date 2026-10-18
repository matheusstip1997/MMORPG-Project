using System.Numerics;
using Shared.Simulation.Stats;
using Shared.Util;
using static Shared.Tests.Simulation.Stats.StatsTestConfig;

namespace Shared.Tests.Simulation.Stats;

/// <summary>Damage formula v1 (R-3.4-04): each step, the whole pipeline on a grid of cases, overflow and determinism.</summary>
public class DamageCalcTests
{
    private const int NoCritRoll = 99;
    private const int CritRoll = 0;

    private readonly CombatConfig _config = Build();

    private int Damage(DamageInput input, int roll = NoCritRoll) => DamageCalc.ComputeWithRoll(input, _config, roll).Damage;

    // ---- Whole pipeline, hand-computed ----

    [Theory]
    // power, multiplier %, defense, expected: raw x C / (def + C) with C = 100 (attacker rank F)
    [InlineData(100, 100, 0, 100)]
    [InlineData(100, 150, 100, 75)]      // 150 x 100 / 200
    [InlineData(100, 100, 300, 25)]      // 100 x 100 / 400
    [InlineData(100, 100, 200, 33)]      // 33.33 rounds to 33
    [InlineData(1, 100, 100, 1)]         // 0.5 rounds half up to 1
    [InlineData(1, 100, 200, 0)]         // 0.33 rounds to 0: no minimum damage in v1
    [InlineData(0, 100, 0, 0)]
    [InlineData(100, 0, 0, 0)]
    public void Pipeline_matches_hand_computed_values(int power, int multiplier, int defense, int expected)
    {
        Assert.Equal(expected, Damage(Input(power, multiplier, defense)));
    }

    [Fact]
    public void A_crit_multiplies_by_base_crit_damage_and_rounds_half_up()
    {
        var input = Input(100, 150, 100); // 75.00 before the crit

        var crit = DamageCalc.ComputeWithRoll(input, _config, CritRoll);

        Assert.True(crit.IsCrit);
        Assert.Equal(113, crit.Damage); // 112.5 rounds up
    }

    [Fact]
    public void Flat_skill_damage_adds_to_raw_and_negative_flat_never_makes_raw_negative()
    {
        var input = Input(100);
        input.SkillFlat = 25;
        Assert.Equal(125, Damage(input));

        input.SkillFlat = -500;
        Assert.Equal(0, Damage(input));
    }

    // ---- Step 2: penetration ----

    [Theory]
    [InlineData(100, 50, 0, 5000)]       // 100 x 50%
    [InlineData(100, 0, 30, 7000)]
    [InlineData(100, 50, 60, 0)]         // max(0, 50 - 60)
    [InlineData(100, 100, 0, 0)]
    [InlineData(100, 250, 0, 0)]         // pen% clamped to 100
    [InlineData(100, -20, -10, 10000)]   // negative penetration counts as 0
    [InlineData(-5, 0, 0, 0)]
    [InlineData(int.MaxValue, 0, 0, 214748364700)]
    public void Effective_defense_applies_percent_then_flat_penetration(int def, int penPercent, int penFlat, long expectedScaled)
    {
        Assert.Equal(expectedScaled, DamageCalc.EffectiveDefenseScaled(def, penPercent, penFlat));
    }

    [Fact]
    public void Penetration_raises_damage_against_defense()
    {
        var input = Input(100, 100, 100);
        Assert.Equal(50, Damage(input));

        input.PenetrationPercent = 50; // effDef 50: 100 x 100 / 150
        Assert.Equal(67, Damage(input));

        input.PenetrationFlat = 50; // effDef 0
        Assert.Equal(100, Damage(input));
    }

    // ---- Step 3: mitigation and C by attacker rank ----

    [Fact]
    public void Mitigation_constant_is_taken_from_the_attacker_rank()
    {
        var input = Input(100, 100, 100);
        input.AttackerRank = Rank.SS; // C = 800: 100 x 800 / 900
        input.DefenderRank = Rank.F;
        Assert.Equal(89, Damage(input));

        input.AttackerRank = Rank.F; // C = 100
        input.DefenderRank = Rank.SS;
        Assert.Equal(50, Damage(input));
    }

    [Fact]
    public void More_defense_never_raises_damage()
    {
        foreach (var power in new[] { 1, 10, 137, 10_000, 1_000_000 })
        {
            int previous = int.MaxValue;
            for (int defense = 0; defense <= 5_000; defense += 37)
            {
                int damage = Damage(Input(power, 100, defense));
                Assert.True(damage <= previous, $"power {power}, defense {defense}");
                previous = damage;
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100_001)]
    public void Mitigation_refuses_a_constant_outside_the_config_bounds(int c)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DamageCalc.Mitigate(100, 0, c));
    }

    // ---- Step 4: rank factor ----

    [Fact]
    public void Rank_factor_comes_from_the_table_and_the_arena_flag_makes_it_neutral()
    {
        var config = Build(Set(ranks: RankRows((a, d) => a == Rank.SS && d == Rank.F ? 200 : a == Rank.F && d == Rank.SS ? 50 : 100)));
        var input = Input(100);
        input.AttackerRank = Rank.SS;
        input.DefenderRank = Rank.F;
        Assert.Equal(200, DamageCalc.ComputeWithRoll(input, config, NoCritRoll).Damage);

        input.NeutralRankFactor = true; // R-6.6-05
        Assert.Equal(100, DamageCalc.ComputeWithRoll(input, config, NoCritRoll).Damage);

        input = Input(100);
        input.AttackerRank = Rank.F;
        input.DefenderRank = Rank.SS;
        Assert.Equal(50, DamageCalc.ComputeWithRoll(input, config, NoCritRoll).Damage);
    }

    // ---- Step 5: element ----

    [Theory]
    [InlineData(0, 100)]
    [InlineData(30, 70)]
    [InlineData(75, 25)]
    [InlineData(90, 25)]     // capped at 75
    [InlineData(-20, 120)]   // weakness
    [InlineData(-80, 150)]   // weakness capped at -50
    public void Element_factor_is_one_minus_capped_resistance(int resist, int expected)
    {
        var input = Input(100);
        input.Element = 0;
        input.ResistPercent = resist;
        Assert.Equal(expected, Damage(input));
        Assert.Equal(expected, DamageCalc.ElementFactorPercent(resist, -50, 75));
    }

    [Fact]
    public void Resistance_is_ignored_without_an_element_and_an_unknown_element_is_refused()
    {
        var input = Input(100);
        input.ResistPercent = 75;
        Assert.Equal(100, Damage(input));

        input.Element = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => Damage(input));
    }

    // ---- Step 6: crit ----

    [Theory]
    [InlineData(0, 4, true)]     // 5% base: rolls 0-4 crit
    [InlineData(0, 5, false)]
    [InlineData(10, 14, true)]
    [InlineData(10, 15, false)]
    [InlineData(1_000, 74, true)]   // capped at 75
    [InlineData(1_000, 75, false)]
    [InlineData(-50, 0, false)]     // never below 0
    public void Crit_succeeds_when_the_roll_is_below_the_capped_chance(int critRate, int roll, bool expected)
    {
        var input = Input(100);
        input.CritRatePercent = critRate;
        var result = DamageCalc.ComputeWithRoll(input, _config, roll);
        Assert.Equal(expected, result.IsCrit);
        Assert.Equal(roll, result.CritRoll);
    }

    [Theory]
    [InlineData(0, 150)]
    [InlineData(50, 200)]
    [InlineData(150, 300)]
    [InlineData(400, 300)]   // bonus capped at 150
    [InlineData(-30, 150)]   // bonus never below 0
    public void Crit_damage_is_base_plus_capped_bonus(int bonus, int expected)
    {
        var input = Input(100);
        input.CritDamageBonusPercent = bonus;
        Assert.Equal(expected, Damage(input, CritRoll));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void A_crit_roll_outside_0_to_99_is_refused(int roll)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DamageCalc.ComputeWithRoll(Input(), _config, roll));
    }

    // ---- Step 7: buffs, debuffs, parry ----

    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(50, 0, 150)]
    [InlineData(-30, 0, 70)]
    [InlineData(-100, 0, 0)]
    [InlineData(-250, 0, 0)]     // clamped to -100
    [InlineData(0, 50, 50)]      // spec 12.7: uncolored parry reduces by 50%
    [InlineData(0, 100, 0)]
    [InlineData(0, 150, 0)]      // clamped to 100
    [InlineData(0, -50, 100)]    // clamped to 0
    [InlineData(50, 50, 75)]
    public void Extra_percent_and_parry_reduction_apply_last(int extra, int parry, int expected)
    {
        var input = Input(100);
        input.ExtraPercent = extra;
        input.ParryReductionPercent = parry;
        Assert.Equal(expected, Damage(input));
    }

    // ---- Rounding and overflow ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-10, 0)]
    [InlineData(49, 0)]
    [InlineData(50, 1)]
    [InlineData(149, 1)]
    [InlineData(150, 2)]
    [InlineData(214748364649, int.MaxValue - 1)]
    [InlineData(214748364650, int.MaxValue)]
    [InlineData(long.MaxValue, int.MaxValue)]
    public void Points_round_half_up_and_saturate(long scaled, int expected)
    {
        Assert.Equal(expected, DamageCalc.ToPoints(scaled));
    }

    [Fact]
    public void Extreme_inputs_saturate_instead_of_wrapping()
    {
        var input = Input(int.MaxValue, int.MaxValue);
        input.SkillFlat = int.MaxValue;
        input.ExtraPercent = int.MaxValue;
        input.CritDamageBonusPercent = int.MaxValue;
        input.CritRatePercent = int.MaxValue;
        Assert.Equal(int.MaxValue, Damage(input, CritRoll));

        input.Defense = int.MaxValue; // still huge, never negative
        Assert.InRange(Damage(input, CritRoll), 1, int.MaxValue);

        var weak = Input(1, 1, int.MaxValue);
        Assert.Equal(0, Damage(weak));

        var negative = Input(int.MinValue, int.MinValue, int.MinValue);
        negative.SkillFlat = int.MinValue;
        negative.ExtraPercent = int.MinValue;
        negative.CritRatePercent = int.MinValue;
        Assert.Equal(0, Damage(negative));
    }

    [Fact]
    public void MulDiv_is_exact_when_the_product_fits_and_saturates_when_the_result_does_not()
    {
        var rng = new Pcg32Rng(12345);
        for (int i = 0; i < 10_000; i++)
        {
            long a = ((long)rng.NextUInt() << 31) ^ rng.NextUInt();             // up to 2^63
            long b = rng.NextInt(1, 10_000_001);
            long d = rng.NextInt(1, int.MaxValue) * (long)rng.NextInt(1, 100);   // d x b fits in a long
            var exact = new BigInteger(a) * b / d;
            long expected = exact > long.MaxValue ? long.MaxValue : (long)exact;
            Assert.Equal(expected, DamageCalc.MulDiv(a, b, d));
        }
        Assert.Equal(long.MaxValue, DamageCalc.MulDiv(long.MaxValue, 2, 1));
        Assert.Equal(0, DamageCalc.MulDiv(long.MaxValue, 0, 7));
        Assert.Equal(long.MaxValue / 3, DamageCalc.MulDiv(long.MaxValue, 1, 3));
    }

    [Fact]
    public void MulDiv_refuses_negative_operands_and_a_non_positive_divisor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DamageCalc.MulDiv(-1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DamageCalc.MulDiv(1, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DamageCalc.MulDiv(1, 1, 0));
    }

    // ---- Determinism (architecture rule 6) ----

    [Fact]
    public void Compute_draws_exactly_one_roll_and_a_seed_replays_the_same_hits()
    {
        var input = Input(100, 150, 100);
        input.CritRatePercent = 45; // 50%: both outcomes show up

        var rng = new Pcg32Rng(seed: 0xC0FFEE, stream: 7);
        var reference = new Pcg32Rng(seed: 0xC0FFEE, stream: 7);
        var replay = new Pcg32Rng(seed: 0xC0FFEE, stream: 7);
        int crits = 0;
        for (int hit = 0; hit < 1_000; hit++)
        {
            var result = DamageCalc.Compute(input, _config, rng);
            int expectedRoll = reference.NextInt(100);
            Assert.Equal(expectedRoll, result.CritRoll);
            Assert.Equal(DamageCalc.ComputeWithRoll(input, _config, expectedRoll).Damage, result.Damage);
            Assert.Equal(result.Damage, DamageCalc.Compute(input, _config, replay).Damage);
            if (result.IsCrit) crits++;
        }
        Assert.InRange(crits, 400, 600);
        Assert.Equal(reference.NextUInt(), rng.NextUInt()); // still in step: one draw per hit
    }

    [Fact]
    public void Chance_zero_and_chance_cap_still_consume_one_draw()
    {
        var never = Input(100);
        never.CritRatePercent = -5;
        var always = Input(100);
        always.CritRatePercent = 1_000;
        var rng = new Pcg32Rng(99);
        var reference = new Pcg32Rng(99);

        DamageCalc.Compute(never, _config, rng);
        DamageCalc.Compute(always, _config, rng);
        reference.NextInt(100);
        reference.NextInt(100);

        Assert.Equal(reference.NextUInt(), rng.NextUInt());
    }

    // ---- Gather from stat blocks ----

    [Fact]
    public void Gather_reads_physical_or_magic_stats_and_the_element_resistance()
    {
        var ids = _config.Ids;
        var attacker = new StatBlock(_config.Stats);
        attacker.SetBase(ids.Atk, 120);
        attacker.SetBase(ids.Matk, 80);
        attacker.SetBase(ids.PhysPenPercent, 10);
        attacker.SetBase(ids.PhysPenFlat, 3);
        attacker.SetBase(ids.MagicPenPercent, 20);
        attacker.SetBase(ids.MagicPenFlat, 4);
        attacker.SetBase(ids.CritRate, 7);
        attacker.SetBase(ids.CritDamage, 30);
        var defender = new StatBlock(_config.Stats);
        defender.SetBase(ids.Def, 50);
        defender.SetBase(ids.Mdef, 60);
        defender.SetBase(_config.Stats.IdFor("fireRes"), 40);
        Assert.True(_config.TryGetElement("fire", out int fire));

        var physical = DamageCalc.Gather(_config, attacker, Rank.C, defender, Rank.D, DamageKind.Physical, 150, 5, DamageCalc.NoElement);
        Assert.Equal((120, 150, 5, 50, 10, 3, 7, 30, Rank.C, Rank.D, DamageCalc.NoElement, 0),
            (physical.Power, physical.SkillMultiplierPercent, physical.SkillFlat, physical.Defense, physical.PenetrationPercent,
             physical.PenetrationFlat, physical.CritRatePercent, physical.CritDamageBonusPercent, physical.AttackerRank,
             physical.DefenderRank, physical.Element, physical.ResistPercent));

        var magic = DamageCalc.Gather(_config, attacker, Rank.C, defender, Rank.D, DamageKind.Magic, 100, 0, fire);
        Assert.Equal((80, 60, 20, 4, fire, 40),
            (magic.Power, magic.Defense, magic.PenetrationPercent, magic.PenetrationFlat, magic.Element, magic.ResistPercent));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DamageCalc.Gather(_config, attacker, Rank.C, defender, Rank.D, DamageKind.Magic, 100, 0, 5));
    }
}
