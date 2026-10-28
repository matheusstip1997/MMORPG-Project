using Shared.Data.Tables;
using Shared.Simulation.Bystial;

namespace Shared.Tests.Simulation.Bystial;

/// <summary>
/// The Bystial book's pure rules (spec 14.2, WP-M2-8): the tier a kill count reaches on the normal and boss ladders of the
/// user decision of 2026-10-27 (R-14.2-02), the next threshold, the exp bonus, and the capped sum of placed cards and
/// family set bonuses (R-14.2-05, architecture rule 2).
/// </summary>
public class BystialMathTests
{
    private static readonly int[] Normal = { 100, 500, 2_000, 5_000 };
    private static readonly int[] Boss = { 1, 5, 20, 50 };

    // Stat indices of the test caps: 0 maxHp, 1 atk, 2 fireRes, 3 buildUpResistPercent.
    private const int MaxHp = 0, Atk = 1, FireRes = 2, BuildUp = 3;

    [Theory]
    [InlineData(0, BystialTier.None)]
    [InlineData(99, BystialTier.None)]
    [InlineData(100, BystialTier.Normal)]
    [InlineData(499, BystialTier.Normal)]
    [InlineData(500, BystialTier.Rare)]
    [InlineData(1_999, BystialTier.Rare)]
    [InlineData(2_000, BystialTier.Epic)]
    [InlineData(4_999, BystialTier.Epic)]
    [InlineData(5_000, BystialTier.Legendary)]
    [InlineData(1_000_000, BystialTier.Legendary)]
    public void The_normal_ladder_gives_a_tier_at_100_500_2000_and_5000_kills(long kills, BystialTier tier) =>
        Assert.Equal(tier, BystialMath.TierFor(kills, Normal));

    [Theory]
    [InlineData(0, BystialTier.None)]
    [InlineData(1, BystialTier.Normal)]
    [InlineData(4, BystialTier.Normal)]
    [InlineData(5, BystialTier.Rare)]
    [InlineData(19, BystialTier.Rare)]
    [InlineData(20, BystialTier.Epic)]
    [InlineData(49, BystialTier.Epic)]
    [InlineData(50, BystialTier.Legendary)]
    public void The_boss_ladder_gives_a_tier_at_1_5_20_and_50_kills(long kills, BystialTier tier) =>
        Assert.Equal(tier, BystialMath.TierFor(kills, Boss));

    [Fact]
    public void A_shorter_ladder_tops_out_at_its_last_tier()
    {
        Assert.Equal(BystialTier.Rare, BystialMath.TierFor(10_000, new[] { 10, 20 }));
        Assert.Equal(BystialTier.None, BystialMath.TierFor(10_000, Array.Empty<int>()));
    }

    [Theory]
    [InlineData(BystialTier.None, 100)]
    [InlineData(BystialTier.Normal, 500)]
    [InlineData(BystialTier.Rare, 2_000)]
    [InlineData(BystialTier.Epic, 5_000)]
    [InlineData(BystialTier.Legendary, 0)]
    public void The_next_threshold_is_the_next_tiers_kills_and_0_at_the_top(BystialTier tier, long next) =>
        Assert.Equal(next, BystialMath.NextTierKills(tier, Normal));

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(100, 5, 105)]
    [InlineData(99, 5, 103)] // rounded down
    [InlineData(0, 20, 0)]
    [InlineData(100, -200, 0)] // never below 0
    public void The_exp_bonus_multiplies_and_rounds_down(long gain, int percent, long expected) =>
        Assert.Equal(expected, BystialMath.ApplyPercent(gain, percent));

    [Fact]
    public void No_placed_card_gives_nothing()
    {
        var bonuses = BystialMath.Aggregate(Array.Empty<PlacedCard>(), new[] { Set(MonsterFamily.Demon, 2, Stat(MaxHp, 20)) }, Caps());
        Assert.Empty(bonuses.Stats);
        Assert.Equal(0, bonuses.DamagePercent(MonsterFamily.Demon));
    }

    [Fact]
    public void A_placed_card_gives_its_stat_bonus_and_its_resistance_on_the_elements_stat()
    {
        var demon = new PlacedCard(MonsterFamily.Demon, new BystialBonus(MaxHp, 10, 0, BystialEffectKind.ElementResistance, FireRes, 5));
        var bonuses = BystialMath.Aggregate(new[] { demon }, Array.Empty<FamilySetBonus>(), Caps());

        Assert.Equal(new[] { (MaxHp, 10, 0), (FireRes, 5, 0) }, bonuses.Stats.Select(s => (s.StatId, s.Flat, s.Percent)));
    }

    [Fact]
    public void Damage_and_exp_bonuses_apply_against_the_cards_family_only()
    {
        var cyclops = new PlacedCard(MonsterFamily.Beast, new BystialBonus(Atk, 3, 0, BystialEffectKind.FamilyDamage, -1, 3));
        var alien = new PlacedCard(MonsterFamily.Aberration, new BystialBonus(Atk, 1, 0, BystialEffectKind.FamilyExp, -1, 4));
        var bonuses = BystialMath.Aggregate(new[] { cyclops, alien }, Array.Empty<FamilySetBonus>(), Caps());

        Assert.Equal(3, bonuses.DamagePercent(MonsterFamily.Beast));
        Assert.Equal(0, bonuses.DamagePercent(MonsterFamily.Demon));
        Assert.Equal(4, bonuses.ExpPercent(MonsterFamily.Aberration));
        Assert.Equal(0, bonuses.ExpPercent(MonsterFamily.Beast));
        Assert.Equal(new[] { (Atk, 4, 0) }, bonuses.Stats.Select(s => (s.StatId, s.Flat, s.Percent)));
    }

    [Fact]
    public void Family_set_bonuses_need_their_card_count_and_add_up()
    {
        var sets = new[]
        {
            Set(MonsterFamily.Demon, 2, Stat(MaxHp, 20)),
            Set(MonsterFamily.Demon, 4, new BystialBonus(-1, 0, 0, BystialEffectKind.FamilyDamage, -1, 5)),
        };
        PlacedCard Demon() => new(MonsterFamily.Demon, Stat(Atk, 1));

        var one = BystialMath.Aggregate(new[] { Demon() }, sets, Caps());
        var three = BystialMath.Aggregate(new[] { Demon(), Demon(), Demon() }, sets, Caps());
        var four = BystialMath.Aggregate(new[] { Demon(), Demon(), Demon(), Demon() }, sets, Caps());
        var mixed = BystialMath.Aggregate(new[] { Demon(), new PlacedCard(MonsterFamily.Beast, Stat(Atk, 1)) }, sets, Caps());

        Assert.DoesNotContain(one.Stats, s => s.StatId == MaxHp);
        Assert.Contains(three.Stats, s => s.StatId == MaxHp && s.Flat == 20);
        Assert.Equal(0, three.DamagePercent(MonsterFamily.Demon));
        Assert.Contains(four.Stats, s => s.StatId == MaxHp && s.Flat == 20); // the 2-card bonus stays at 4 cards
        Assert.Equal(5, four.DamagePercent(MonsterFamily.Demon));
        Assert.DoesNotContain(mixed.Stats, s => s.StatId == MaxHp); // one demon and one beast: no demon set
    }

    [Fact]
    public void Every_sum_is_capped()
    {
        var cards = Enumerable.Range(0, 8).Select(_ => new PlacedCard(MonsterFamily.Demon,
            new BystialBonus(MaxHp, 100, 9, BystialEffectKind.ElementResistance, FireRes, 10))).ToList();
        cards.AddRange(Enumerable.Range(0, 8).Select(_ => new PlacedCard(MonsterFamily.Beast,
            new BystialBonus(Atk, -100, 0, BystialEffectKind.FamilyDamage, -1, 10))));
        cards.AddRange(Enumerable.Range(0, 8).Select(_ => new PlacedCard(MonsterFamily.Aberration,
            new BystialBonus(-1, 0, 0, BystialEffectKind.BuildUpResistance, BuildUp, 10))));
        cards.AddRange(Enumerable.Range(0, 8).Select(_ => new PlacedCard(MonsterFamily.Undead,
            new BystialBonus(-1, 0, 0, BystialEffectKind.FamilyExp, -1, 10))));
        var bonuses = BystialMath.Aggregate(cards, Array.Empty<FamilySetBonus>(), Caps());

        var stats = bonuses.Stats.ToDictionary(s => s.StatId);
        Assert.Equal((200, 10), (stats[MaxHp].Flat, stats[MaxHp].Percent)); // the stat cap, flat and percent apart
        Assert.Equal(-40, stats[Atk].Flat); // a negative sum stops at -cap
        Assert.Equal(30, stats[FireRes].Flat); // the element resistance cap
        Assert.Equal(30, stats[BuildUp].Flat); // the build-up resistance cap
        Assert.Equal(20, bonuses.DamagePercent(MonsterFamily.Beast));
        Assert.Equal(20, bonuses.ExpPercent(MonsterFamily.Undead));
    }

    [Fact]
    public void A_bonus_on_a_stat_without_a_cap_gives_nothing()
    {
        var caps = new BystialCaps(new int[4], new int[4], new int[BystialEffectKinds.ArrayLength]);
        var card = new PlacedCard(MonsterFamily.Demon, new BystialBonus(MaxHp, 10, 0, BystialEffectKind.FamilyDamage, -1, 5));
        var bonuses = BystialMath.Aggregate(new[] { card }, Array.Empty<FamilySetBonus>(), caps);
        Assert.Empty(bonuses.Stats);
        Assert.Equal(0, bonuses.DamagePercent(MonsterFamily.Demon));
    }

    private static BystialBonus Stat(int statId, int flat) => new(statId, flat, 0, BystialEffectKind.None, -1, 0);

    private static FamilySetBonus Set(MonsterFamily family, int cards, BystialBonus bonus) => new(family, cards, bonus);

    /// <summary>The placeholder caps: maxHp 200/10 %, atk 40/10 %, the effect kinds 30, 30, 20, 20.</summary>
    private static BystialCaps Caps()
    {
        var effect = new int[BystialEffectKinds.ArrayLength];
        effect[(int)BystialEffectKind.ElementResistance] = 30;
        effect[(int)BystialEffectKind.BuildUpResistance] = 30;
        effect[(int)BystialEffectKind.FamilyDamage] = 20;
        effect[(int)BystialEffectKind.FamilyExp] = 20;
        return new BystialCaps(new[] { 200, 40, 0, 0 }, new[] { 10, 10, 0, 0 }, effect);
    }
}
