using Shared.Simulation.Items;
using Shared.Util;

namespace Shared.Tests.Simulation.Items;

/// <summary>The party loot roll and the loot debt (spec 4.3 R-4.3-02, M4 decision 2; WP-M4-6): pure functions with fixed seeds.</summary>
public class PartyLootTests
{
    private const int HalfLife = 600_000;

    [Theory]
    [InlineData(1000, 0, 1000)]
    [InlineData(1000, HalfLife, 500)]
    [InlineData(1000, 2 * HalfLife, 250)]
    [InlineData(1000, HalfLife / 2, 750)] // linear inside a half-life: 1 - 1/4
    [InlineData(1000, HalfLife + HalfLife / 2, 375)]
    [InlineData(0, HalfLife, 0)]
    [InlineData(1000, -5, 1000)] // a clock that went back changes nothing
    public void A_debt_halves_every_half_life(long debt, long elapsedMs, long expected) =>
        Assert.Equal(expected, PartyLoot.DecayDebt(debt, elapsedMs, HalfLife));

    [Fact]
    public void A_debt_never_rises_and_reaches_zero()
    {
        long previous = long.MaxValue / 4;
        for (long elapsed = 0; elapsed <= 70L * HalfLife; elapsed += HalfLife / 7)
        {
            long now = PartyLoot.DecayDebt(long.MaxValue / 4, elapsed, HalfLife);
            Assert.InRange(now, 0, previous);
            previous = now;
        }
        Assert.Equal(0, previous);
    }

    [Fact]
    public void A_member_with_no_debt_weighs_the_most()
    {
        Span<long> weights = stackalloc long[3];
        PartyLoot.RollWeights(new long[] { 0, 100, 300 }, 10, 100, weights);
        Assert.Equal(PartyLoot.WeightOne, weights[0]);
        Assert.Equal(PartyLoot.WeightOne / 2, weights[1]); // the half-weight debt
        Assert.Equal(PartyLoot.WeightOne / 4, weights[2]);
    }

    [Fact]
    public void A_zero_value_item_is_uniform_whatever_the_debts()
    {
        Span<long> weights = stackalloc long[3];
        PartyLoot.RollWeights(new long[] { 0, 5_000, 1_000_000 }, 0, 100, weights);
        Assert.Equal(new[] { PartyLoot.WeightOne, PartyLoot.WeightOne, PartyLoot.WeightOne }, weights.ToArray());
    }

    [Fact]
    public void Equal_debts_are_uniform_and_a_huge_debt_still_weighs_at_least_one()
    {
        Span<long> weights = stackalloc long[2];
        PartyLoot.RollWeights(new long[] { 400, 400 }, 7, 100, weights);
        Assert.Equal(weights[0], weights[1]);
        PartyLoot.RollWeights(new long[] { 0, long.MaxValue }, 7, 100, weights);
        Assert.Equal(1, weights[1]);
    }

    [Fact]
    public void A_seed_gives_the_same_roll_every_time()
    {
        var (won1, after1, first1) = Roll(new long[] { 0, 50, 200 }, unitValue: 10, units: 12, seed: 42);
        var (won2, after2, first2) = Roll(new long[] { 0, 50, 200 }, unitValue: 10, units: 12, seed: 42);
        Assert.Equal(won1, won2);
        Assert.Equal(after1, after2);
        Assert.Equal(first1, first2);
        Assert.Equal(12, won1.Sum());
    }

    [Fact]
    public void Every_won_unit_adds_its_value_to_the_winners_debt()
    {
        long[] debts = { 30, 0 };
        var (won, after, first) = Roll(debts, unitValue: 25, units: 9, seed: 7);
        for (int i = 0; i < debts.Length; i++)
            Assert.Equal(debts[i] + 25L * won[i], after[i]);
        Assert.True(first[1] > first[0]); // the first unit's weights are logged: the member without debt weighs more
    }

    [Fact]
    public void A_single_candidate_takes_every_unit()
    {
        var (won, after, first) = Roll(new long[] { 90 }, unitValue: 10, units: 5, seed: 3);
        Assert.Equal(new[] { 5 }, won);
        Assert.Equal(new[] { 140L }, after);
        Assert.Single(first);
    }

    [Fact]
    public void Over_many_rolls_a_stack_is_spread_and_nobody_gets_everything_or_nothing()
    {
        // Two members roll 200 single valuable units one after the other, the debt carried between rolls: the debt pulls the
        // shares toward even (R-4.3-02: "nobody gets everything or nothing").
        long[] debts = { 0, 0 };
        var totals = new int[2];
        var rng = new Pcg32Rng(99, 1);
        for (int roll = 0; roll < 200; roll++)
        {
            var (won, after, _) = Roll(debts, unitValue: 100, units: 1, seed: PartyLoot.NextSeed(rng));
            debts = after;
            totals[0] += won[0];
            totals[1] += won[1];
        }
        Assert.InRange(totals[0], 80, 120);
        Assert.Equal(200, totals[0] + totals[1]);
    }

    [Fact]
    public void Picking_follows_the_weights()
    {
        var rng = new Pcg32Rng(5, 2);
        var counts = new int[2];
        long[] weights = { 3, 1 };
        for (int i = 0; i < 4000; i++)
            counts[PartyLoot.Pick(weights, rng)]++;
        Assert.InRange(counts[0], 2800, 3200);
    }

    [Fact]
    public void A_roll_refuses_no_candidates_and_no_units()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Roll(Array.Empty<long>(), 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Roll(new long[] { 0 }, 1, 0, 1));
    }

    private static (int[] Won, long[] After, long[] First) Roll(long[] debts, long unitValue, int units, ulong seed)
    {
        var won = new int[Math.Max(1, debts.Length)];
        var after = new long[Math.Max(1, debts.Length)];
        var first = new long[Math.Max(1, debts.Length)];
        PartyLoot.RollUnits(debts, unitValue, 100, units, seed, first, won, after);
        return (won[..debts.Length], after[..debts.Length], first[..debts.Length]);
    }
}
