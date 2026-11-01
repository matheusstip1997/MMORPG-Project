using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Util;

namespace Shared.Tests.Simulation.Crafting;

/// <summary>
/// The gem rolls and checks (spec 5.3 R-5.3-05, M3 decision 11; WP-M3-9): the socket chance with the job tree's bonus, the
/// removal cost after <c>removalCostPercent</c> rounded by chance, the seed reproducing an outcome, and the checks the server,
/// the client and the bots share. Numbers are the placeholder rows.
/// </summary>
public class GemMathTests
{
    [Theory]
    [InlineData(80, 0, 80)]
    [InlineData(80, 10, 90)]
    [InlineData(95, 12, 100)]
    [InlineData(50, -60, 0)]
    public void The_socket_chance_adds_the_bonus_within_0_and_100(int odds, int bonus, int expected) =>
        Assert.Equal(expected, GemMath.SocketChance(odds, bonus));

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(99)]
    [InlineData(100)]
    public void The_socket_roll_succeeds_at_about_its_chance_and_always_at_100_and_never_at_0(int chance)
    {
        int successes = 0;
        const int Trials = 4000;
        for (ulong seed = 1; seed <= Trials; seed++)
        {
            if (GemMath.Draw(seed, chance, 0, 0).Success)
                successes++;
        }
        if (chance == 0)
            Assert.Equal(0, successes);
        else if (chance == 100)
            Assert.Equal(Trials, successes);
        else
            Assert.InRange(successes * 100 / Trials, chance - 4, Math.Min(100, chance + 4));
    }

    [Fact]
    public void A_seed_reproduces_its_outcome()
    {
        for (ulong seed = 1; seed < 200; seed++)
        {
            var first = GemMath.Draw(seed, 65, 2, 30);
            var again = GemMath.Draw(seed, 65, 2, 30);
            Assert.Equal(first.Success, again.Success);
            Assert.Equal(first.RemovalCost, again.RemovalCost);
        }
    }

    [Theory]
    [InlineData(2, 0, 2, 2)]
    [InlineData(2, 50, 1, 1)]
    [InlineData(2, 100, 0, 0)]
    [InlineData(2, -20, 2, 2)]
    [InlineData(2, 30, 1, 2)]
    public void The_removal_cost_falls_with_the_percent_and_rounds_by_chance(int count, int percent, int min, int max)
    {
        var seen = new HashSet<int>();
        for (ulong seed = 1; seed <= 500; seed++)
        {
            int cost = GemMath.RemovalCost(count, percent, new Pcg32Rng(seed, GemMath.Stream));
            Assert.InRange(cost, min, max);
            seen.Add(cost);
        }
        Assert.Equal(max - min + 1, seen.Count); // both roundings happen when the cost has a remainder
        Assert.Equal(max, GemMath.MaxRemovalCost(count, percent));
    }

    [Fact]
    public void Thirty_percent_off_two_costs_one_point_four_on_average()
    {
        long sum = 0;
        const int Trials = 5000;
        for (ulong seed = 1; seed <= Trials; seed++)
            sum += GemMath.RemovalCost(2, 30, new Pcg32Rng(seed, GemMath.Stream));
        Assert.InRange(sum * 100 / Trials, 135, 145);
    }

    private static readonly ConfigSet Tables = ForgeMathTests.LoadRepository();

    private static GemRules Rules() => GemRules.From(Tables)!;

    [Fact]
    public void The_committed_rules_know_every_cut_gem_and_tier()
    {
        var rules = Rules();
        Assert.Equal(GemKind.Ruby, rules.Gem("m3.rubyT1")!.Kind);
        Assert.Null(rules.Gem("m3.roughGemT1"));
        Assert.Equal("jeweler", rules.JobFor(rules.Gem("m3.emeraldT3")!));
        for (int tier = 1; tier <= 3; tier++)
            Assert.NotNull(rules.Odds(tier));
    }

    [Fact]
    public void A_socket_checks_target_gem_job_slot_level_and_units_in_that_order()
    {
        var rules = Rules();
        var sword = rules.Item("m3.forgedSword");
        var ring = rules.Item("m2.copperRing");
        var ruby = rules.Item("m3.rubyT1");
        var rubyT2 = rules.Item("m3.rubyT2");
        var ore = rules.Item("m3.oreT1");
        Assert.Equal(GemReason.NotSocketable, rules.CheckSocket(ring, 0, ruby, 12, 1));
        Assert.Equal(GemReason.NotAGem, rules.CheckSocket(sword, 0, ore, 12, 1));
        Assert.Equal(GemReason.JobRequired, rules.CheckSocket(sword, 0, ruby, 0, 1));
        Assert.Equal(GemReason.NoGemSlot, rules.CheckSocket(sword, 1, ruby, 12, 1));
        Assert.Equal(GemReason.JobLevelTooLow, rules.CheckSocket(sword, 0, rubyT2, 4, 1));
        Assert.Equal(GemReason.MaterialsMissing, rules.CheckSocket(sword, 0, ruby, 1, 0));
        Assert.Equal(GemReason.None, rules.CheckSocket(sword, 0, ruby, 1, 1));
    }

    [Fact]
    public void A_removal_needs_the_gem_the_job_and_the_most_the_cost_can_come_to()
    {
        var rules = Rules();
        var sword = rules.Item("m3.forgedSword");
        var gems = new[] { "m3.rubyT1" };
        Assert.Equal(GemReason.NoSuchGem, rules.CheckRemove(sword, gems, 1, 12, 9, 0));
        Assert.Equal(GemReason.JobRequired, rules.CheckRemove(sword, gems, 0, 0, 9, 0));
        Assert.Equal(GemReason.MaterialsMissing, rules.CheckRemove(sword, gems, 0, 12, 1, 0));
        Assert.Equal(GemReason.None, rules.CheckRemove(sword, gems, 0, 12, 1, 50)); // 50 % off 2: exactly 1
        Assert.Equal(GemReason.MaterialsMissing, rules.CheckRemove(sword, gems, 0, 12, 1, 30)); // up to 2
        Assert.Equal(GemReason.None, rules.CheckRemove(sword, gems, 0, 12, 2, 0));
    }
}
