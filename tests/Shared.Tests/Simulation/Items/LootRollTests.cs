using Shared.Simulation.Items;
using Shared.Util;

namespace Shared.Tests.Simulation.Items;

/// <summary>The loot roll (R-4.3-01, spec 10.2: a pure function with a fixed seed).</summary>
public class LootRollTests
{
    private static readonly LootChoice Horn = new(1, 0, 70, 1, 2);
    private static readonly LootChoice Ash = new(2, 0, 30, 1, 3);

    private static List<ItemStack> Roll(ulong seed, int rolls = 1, int nothing = 0, params LootChoice[] choices)
    {
        var drops = new List<ItemStack>();
        LootRoll.Roll(choices.Length == 0 ? new[] { Horn, Ash } : choices, rolls, nothing, new Pcg32Rng(seed, 7), drops);
        return drops;
    }

    [Fact]
    public void A_fixed_seed_gives_the_same_drops_every_time()
    {
        var first = Roll(1234, rolls: 5);
        var second = Roll(1234, rolls: 5);

        Assert.Equal(5, first.Count);
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_fixed_seed_gives_exactly_the_draws_it_spells_out()
    {
        // Spelled out with the same generator: one draw for the weight, one for the count, per roll.
        var rng = new Pcg32Rng(99, 7);
        var expected = new List<ItemStack>();
        for (int i = 0; i < 3; i++)
        {
            int pick = rng.NextInt(100);
            var choice = pick < 70 ? Horn : Ash;
            expected.Add(new ItemStack(choice.ItemNetId, 0, false, rng.NextInt(choice.CountMin, choice.CountMax + 1)));
        }

        Assert.Equal(expected, Roll(99, rolls: 3));
    }

    [Fact]
    public void Counts_stay_inside_the_range_and_weights_shape_the_mix()
    {
        var drops = Roll(42, rolls: 10_000);

        Assert.All(drops, d => Assert.InRange(d.Count, d.ItemNetId == 1 ? 1 : 1, d.ItemNetId == 1 ? 2 : 3));
        int horns = drops.Count(d => d.ItemNetId == 1);
        Assert.InRange(horns, 6_700, 7_300); // 70 % of 10 000, a wide margin
        Assert.All(drops, d => Assert.False(d.Stolen));
    }

    [Fact]
    public void Nothing_weight_makes_empty_rolls_and_a_table_of_only_nothing_drops_nothing()
    {
        var drops = Roll(5, rolls: 10_000, nothing: 100);
        Assert.InRange(drops.Count, 4_700, 5_300); // 100 of 200

        var none = new List<ItemStack>();
        LootRoll.Roll(Array.Empty<LootChoice>(), 10, 1, new Pcg32Rng(5, 7), none);
        Assert.Empty(none);
    }

    [Fact]
    public void A_table_with_no_weight_at_all_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            LootRoll.Roll(Array.Empty<LootChoice>(), 1, 0, new Pcg32Rng(1, 1), new List<ItemStack>()));
    }
}
