using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;

namespace Shared.Tests.Simulation.Crafting;

/// <summary>
/// Enchanting's pure functions (spec 5.3 R-5.3-04, 3.4; WP-M3-8): the job tree's bonus moved to +1 from -1 first, then from
/// no change (user answer), the outcome of a draw at the weight boundaries, the bounds +0..+9, the replayable draw of a
/// logged seed, the enchant lines of +0 and +3, and <see cref="EnchantRules"/> on the committed tables (the item's job, tier
/// and station). The numbers are placeholders; the tests pin the rules.
/// </summary>
public class EnchantMathTests
{
    [Theory]
    // plus, same, minus, bonus -> plus, same, minus
    [InlineData(60, 30, 10, 0, 60, 30, 10)]
    [InlineData(60, 30, 10, 4, 64, 30, 6)] // from -1 only
    [InlineData(60, 30, 10, 10, 70, 30, 0)] // -1 emptied exactly
    [InlineData(60, 30, 10, 15, 75, 25, 0)] // then from no change
    [InlineData(60, 30, 10, 200, 100, 0, 0)] // never more than both hold: +1 is at most 100
    [InlineData(60, 30, 10, -5, 60, 30, 10)] // a negative bonus counts as 0
    [InlineData(100, 0, 0, 6, 100, 0, 0)]
    public void The_bonus_moves_points_to_plus_from_minus_then_same(int plus, int same, int minus, int bonus, int ePlus, int eSame, int eMinus)
    {
        var weights = EnchantMath.Weights(plus, same, minus, bonus);
        Assert.Equal((ePlus, eSame, eMinus), (weights.Plus, weights.Same, weights.Minus));
        Assert.Equal(100, weights.Plus + weights.Same + weights.Minus);
    }

    [Theory]
    [InlineData(0, EnchantOutcome.Up)]
    [InlineData(59, EnchantOutcome.Up)]
    [InlineData(60, EnchantOutcome.Same)]
    [InlineData(89, EnchantOutcome.Same)]
    [InlineData(90, EnchantOutcome.Down)]
    [InlineData(99, EnchantOutcome.Down)]
    public void A_draw_falls_into_plus_then_same_then_minus(int draw, EnchantOutcome expected) =>
        Assert.Equal(expected, EnchantMath.Outcome(new EnchantWeights(60, 30, 10), draw));

    [Theory]
    [InlineData(0, EnchantOutcome.Down, 0)] // never below +0
    [InlineData(0, EnchantOutcome.Up, 1)]
    [InlineData(5, EnchantOutcome.Same, 5)]
    [InlineData(5, EnchantOutcome.Down, 4)]
    [InlineData(8, EnchantOutcome.Up, 9)]
    [InlineData(9, EnchantOutcome.Up, 9)] // never above +9
    public void The_level_stays_within_zero_and_nine(int level, EnchantOutcome outcome, int expected) =>
        Assert.Equal(expected, EnchantMath.Apply(level, outcome));

    [Fact]
    public void A_logged_seed_replays_its_draw()
    {
        for (ulong seed = 1; seed < 200; seed++)
        {
            int draw = EnchantMath.Draw(seed);
            Assert.InRange(draw, 0, 99);
            Assert.Equal(draw, EnchantMath.Draw(seed));
        }
    }

    [Fact]
    public void The_draws_cover_the_whole_range()
    {
        var seen = new HashSet<int>();
        for (ulong seed = 1; seed < 5000; seed++)
            seen.Add(EnchantMath.Draw(seed));
        Assert.Equal(100, seen.Count);
    }

    [Fact]
    public void An_item_at_plus_zero_adds_nothing_and_at_plus_three_its_percent_of_each_line()
    {
        var lines = new[] { new ItemStatBonus("atk", 25, 0), new ItemStatBonus("def", 7, 4), new ItemStatBonus("hp", 3, 0) };
        var none = new List<ItemStatBonus>();
        EnchantMath.AddBonus(lines, 0, none); // +0: no EnchantBonus row
        Assert.Empty(none);

        var three = new List<ItemStatBonus>();
        EnchantMath.AddBonus(lines, 30, three); // +3 at the placeholder 30 %
        Assert.Equal(2, three.Count); // hp 3 x 30 % truncates to 0 and is left out
        Assert.Equal(("atk", 7, 0), (three[0].StatId, three[0].Flat, three[0].Percent));
        Assert.Equal(("def", 2, 1), (three[1].StatId, three[1].Flat, three[1].Percent));
    }

    private static ConfigSet Committed()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var tables = Path.Combine(directory!.FullName, "data", "tables");
        var errors = new List<ValidationError>();
        var set = ConfigTableReader.LoadAndValidate(ConfigTables.All, name =>
        {
            var path = Path.Combine(tables, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, errors);
        Assert.Empty(errors);
        return set;
    }

    [Fact]
    public void The_committed_tables_build_the_enchant_rules()
    {
        var rules = EnchantRules.From(Committed())!;
        Assert.NotNull(rules);
        var sword = rules.Item("m3.forgedSword")!;
        Assert.Equal("blacksmith", rules.JobOf(sword));
        Assert.Equal(1, rules.TierOf(sword)); // the sword mold's tier
        Assert.Equal(NpcInteraction.Forge, rules.StationOf("blacksmith"));
        Assert.Equal(NpcInteraction.JewelerBench, rules.StationOf("jeweler"));
        Assert.True(rules.TryMaterial(sword, out var stone, out int count));
        Assert.Equal("m3.enchantStone", stone.Id);
        Assert.Equal(1, count);
        for (int level = 0; level < EnchantLimits.MaxLevel; level++)
            Assert.NotNull(rules.Odds(level));
        Assert.Null(rules.Odds(EnchantLimits.MaxLevel));
        Assert.Equal(0, rules.StatPercent(0));
        Assert.True(rules.StatPercent(9) >= rules.StatPercent(1));
        // Dropped gear by category (decision 10): a ring is the Jeweler's, tier 1.
        var ring = rules.Item("m2.copperRing")!;
        Assert.Equal("jeweler", rules.JobOf(ring));
        Assert.Equal(1, rules.TierOf(ring));
        Assert.NotEqual(0, rules.SafeCode);
        Assert.NotEqual(0, rules.EquippedCode);
        Assert.True(rules.IsTargetContainer(rules.SafeCode) && rules.IsTargetContainer(rules.EquippedCode));
        Assert.False(rules.IsTargetContainer(rules.NormalCode));
    }

    [Fact]
    public void The_shared_check_refuses_like_the_server()
    {
        var rules = EnchantRules.From(Committed())!;
        var sword = rules.Item("m3.forgedSword");
        Func<string, bool> smith = job => job == "blacksmith";
        Assert.Equal(EnchantReason.None, rules.Check(sword, NpcInteraction.Forge, smith, 0));
        Assert.Equal(EnchantReason.WrongStation, rules.Check(sword, NpcInteraction.JewelerBench, smith, 0));
        Assert.Equal(EnchantReason.JobRequired, rules.Check(sword, NpcInteraction.Forge, job => job == "jeweler", 0));
        Assert.Equal(EnchantReason.EnchantMaxed, rules.Check(sword, NpcInteraction.Forge, smith, EnchantLimits.MaxLevel));
        Assert.Equal(EnchantReason.NotEnchantable, rules.Check(rules.Item("m3.enchantStone"), NpcInteraction.Forge, smith, 0));
        Assert.Equal(EnchantReason.NotEnchantable, rules.Check(null, NpcInteraction.Forge, smith, 0));
    }

    [Fact]
    public void The_job_state_names_the_held_jobs()
    {
        var set = Committed();
        var rules = EnchantRules.From(set)!;
        var smith = JobDef.Find(set, "blacksmith")!;
        var state = new JobState { Jobs = new[] { new JobEntry { JobNetId = (byte)smith.NetId, Slot = JobSlot.Main, Level = 1 } } };
        Assert.True(rules.Holds(state, "blacksmith"));
        Assert.False(rules.Holds(state, "jeweler"));
        Assert.False(rules.Holds(null, "blacksmith"));
    }
}
