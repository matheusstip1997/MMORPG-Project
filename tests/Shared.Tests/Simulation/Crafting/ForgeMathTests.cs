using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;

namespace Shared.Tests.Simulation.Crafting;

/// <summary>
/// The forge's pure functions (spec 5.3 R-5.3-02, R-5.3-03, 5.2 R-5.2-04; WP-M3-6): <see cref="QualityCalc"/> with every
/// component bounded, the rarity bands, the crafted stats snapshot, the seeded rolls (replayable from the logged seed), the
/// conversion roll and the UTC day, and the shared start checks of <see cref="ForgeRules"/> on the committed tables. The
/// numbers are placeholders; the tests pin the rules, not the tuning.
/// </summary>
public class ForgeMathTests
{
    private static readonly QualityCapSet Caps = new(materials: 300, actions: 250, jobTree: 100, minigame: 150, serverRoll: 150, smallTerm: 20);

    private static QualityInputs Raw(int materials = 0, int actions = 0, int jobTree = 0, int minigame = 0, int serverRoll = 0, int small = 0) =>
        new() { Materials = materials, Actions = actions, JobTree = jobTree, Minigame = minigame, ServerRoll = serverRoll, SmallTerm = small };

    [Theory]
    // materials, actions, jobTree, minigame, serverRoll, small, total
    [InlineData(0, 0, 0, 0, 0, 0, 0)]
    [InlineData(60, 70, 0, 0, 40, 5, 175)]
    [InlineData(100, 100, 50, 75, 100, -10, 415)]
    [InlineData(300, 250, 100, 150, 150, 20, 970)] // every component at its cap
    [InlineData(900, 900, 900, 900, 900, 900, 970)] // every component over its cap: the caps hold
    [InlineData(10, 0, 0, 0, 0, -20, 0)] // the small term cannot take the quality below 0
    [InlineData(-50, -50, -50, -50, -50, 0, 0)] // negative inputs count as 0
    public void Quality_is_the_sum_of_the_capped_components(int materials, int actions, int jobTree, int minigame, int serverRoll,
        int small, int total)
    {
        var raw = Raw(materials, actions, jobTree, minigame, serverRoll, small);
        Assert.Equal(total, QualityCalc.Compute(in raw, Caps).Total);
    }

    [Theory]
    [InlineData(QualityComponent.Materials)]
    [InlineData(QualityComponent.Actions)]
    [InlineData(QualityComponent.JobTree)]
    [InlineData(QualityComponent.Minigame)]
    [InlineData(QualityComponent.ServerRoll)]
    [InlineData(QualityComponent.SmallTerm)]
    public void Each_component_stops_at_its_cap(QualityComponent component)
    {
        var raw = component switch
        {
            QualityComponent.Materials => Raw(materials: 10_000),
            QualityComponent.Actions => Raw(actions: 10_000),
            QualityComponent.JobTree => Raw(jobTree: 10_000),
            QualityComponent.Minigame => Raw(minigame: 10_000),
            QualityComponent.ServerRoll => Raw(serverRoll: 10_000),
            _ => Raw(small: 10_000),
        };
        var result = QualityCalc.Compute(in raw, Caps);
        Assert.Equal(Caps.Of(component), result.Total);
        int part = component switch
        {
            QualityComponent.Materials => result.Materials,
            QualityComponent.Actions => result.Actions,
            QualityComponent.JobTree => result.JobTree,
            QualityComponent.Minigame => result.Minigame,
            QualityComponent.ServerRoll => result.ServerRoll,
            _ => result.SmallTerm,
        };
        Assert.Equal(Caps.Of(component), part);
    }

    [Fact]
    public void The_small_term_is_signed_and_bounded_both_ways()
    {
        var low = Raw(materials: 100, small: -500);
        var high = Raw(materials: 100, small: 500);
        Assert.Equal(-20, QualityCalc.Compute(in low, Caps).SmallTerm);
        Assert.Equal(80, QualityCalc.Compute(in low, Caps).Total);
        Assert.Equal(120, QualityCalc.Compute(in high, Caps).Total);
    }

    [Fact]
    public void The_preview_range_holds_every_random_number_at_its_ends()
    {
        QualityCalc.Range(materials: 60, actionsMin: 30, actionsMax: 130, jobTree: 0, minigameMax: 0, Caps, out int min, out int max);
        Assert.Equal(60 + 30 - 20, min);
        Assert.Equal(60 + 130 + 150 + 20, max);
    }

    [Fact]
    public void The_preview_counts_the_minigame_scored_so_far_and_keeps_its_cap()
    {
        // WP-M3-10: 40 scored by closed rounds, an open round may add 30 more.
        QualityCalc.Range(materials: 60, actionsMin: 30, actionsMax: 130, jobTree: 0, minigameMin: 40, minigameMax: 70, Caps,
            out int min, out int max);
        Assert.Equal(60 + 30 + 40 - 20, min);
        Assert.Equal(60 + 130 + 70 + 150 + 20, max);
        QualityCalc.Range(60, 30, 130, 0, minigameMin: 400, minigameMax: 900, Caps, out min, out max);
        Assert.Equal(60 + 30 + 150 - 20, min); // the minigame cap holds at both ends
        Assert.Equal(60 + 130 + 150 + 150 + 20, max);
    }

    [Fact]
    public void The_cap_set_needs_one_row_per_component()
    {
        var rows = new[]
        {
            new QualityCaps { Id = "materials", Cap = 1 }, new QualityCaps { Id = "actions", Cap = 2 }, new QualityCaps { Id = "jobTree", Cap = 3 },
            new QualityCaps { Id = "minigame", Cap = 4 }, new QualityCaps { Id = "serverRoll", Cap = 5 },
        };
        Assert.False(QualityCapSet.TryBuild(rows, out _));
        Assert.True(QualityCapSet.TryBuild(rows.Append(new QualityCaps { Id = "smallTerm", Cap = 6 }), out var caps));
        Assert.Equal(6, caps.SmallTerm);
        Assert.Equal(4, caps.Minigame);
    }

    private static readonly QualityBands Bands = new(new[] { 0, 200, 400, 600, 800 }, new byte[] { 0, 1, 2, 3, 4 },
        new[] { 0, 10, 25, 50, 100 });

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(199, 0, 0)]
    [InlineData(200, 1, 10)]
    [InlineData(399, 1, 10)]
    [InlineData(400, 2, 25)]
    [InlineData(599, 2, 25)]
    [InlineData(600, 3, 50)]
    [InlineData(799, 3, 50)]
    [InlineData(800, 4, 100)]
    [InlineData(65_535, 4, 100)]
    public void Quality_maps_to_the_band_whose_minimum_it_reaches(int quality, byte rarity, int bonus)
    {
        Assert.Equal(rarity, Bands.Rarity(quality));
        Assert.Equal(bonus, Bands.StatBonusPercent(quality));
    }

    [Fact]
    public void Crafted_stats_sum_the_material_lines_per_unit_then_add_the_quality_bonus()
    {
        var uses = new[]
        {
            new MaterialStatUse("atk", 5, 0, 2), // two ingots
            new MaterialStatUse("def", 3, 0, 2),
            new MaterialStatUse("atk", 2, 1, 1), // a hide grip
        };
        var plain = CraftedStatsCalc.Build(uses, 0);
        Assert.Equal(new[] { "atk", "def" }, plain.Select(s => s.StatId));
        Assert.Equal(12, plain[0].Flat);
        Assert.Equal(1, plain[0].Percent);
        Assert.Equal(6, plain[1].Flat);

        var rare = CraftedStatsCalc.Build(uses, 25);
        Assert.Equal(15, rare[0].Flat); // 12 x 125 %
        Assert.Equal(7, rare[1].Flat); // 6 x 125 % = 7.5, rounded toward zero
    }

    [Fact]
    public void Crafted_stats_leave_out_zero_lines_and_keep_at_most_sixteen()
    {
        var uses = Enumerable.Range(0, 20).Select(i => new MaterialStatUse("s" + i, i == 3 ? 0 : 1, 0, 1)).ToList();
        var stats = CraftedStatsCalc.Build(uses, 0);
        Assert.Equal(ItemProperties.MaxCraftedStats, stats.Count);
        Assert.DoesNotContain(stats, s => s.StatId == "s3");
    }

    [Fact]
    public void The_rolls_replay_from_the_logged_seed()
    {
        var min = new[] { 10, 0, 20 };
        var max = new[] { 40, 60, 30 };
        var first = ForgeRolls.Draw(0xC0FFEEUL, min, max, 150, 20, 0);
        var again = ForgeRolls.Draw(0xC0FFEEUL, min, max, 150, 20, 0);
        Assert.Equal(first, again);
        Assert.InRange(first.Actions, 30, 130);
        Assert.InRange(first.ServerRoll, 0, 150);
        Assert.InRange(first.SmallTerm, -20, 20);
        Assert.True(first.Success);
    }

    [Fact]
    public void Every_draw_stays_inside_its_range_over_many_seeds()
    {
        var min = new[] { 10 };
        var max = new[] { 40 };
        bool sawLow = false, sawHigh = false;
        for (ulong seed = 1; seed <= 2_000; seed++)
        {
            var roll = ForgeRolls.Draw(seed, min, max, 150, 20, 0);
            Assert.InRange(roll.Actions, 10, 40);
            Assert.InRange(roll.SmallTerm, -20, 20);
            sawLow |= roll.SmallTerm == -20;
            sawHigh |= roll.SmallTerm == 20;
        }
        Assert.True(sawLow && sawHigh, "both ends of the small term are reachable");
    }

    [Fact]
    public void The_conversion_roll_succeeds_near_its_percent_and_never_always()
    {
        int successes = 0;
        for (ulong seed = 1; seed <= 10_000; seed++)
        {
            if (ForgeRolls.Draw(seed, Array.Empty<int>(), Array.Empty<int>(), 150, 20, 70).Success)
                successes++;
        }
        Assert.InRange(successes, 6_700, 7_300);
        Assert.True(ForgeRolls.Draw(7, Array.Empty<int>(), Array.Empty<int>(), 150, 20, 0).Success); // not a conversion
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(86_399_999L, 0L)]
    [InlineData(86_400_000L, 1L)]
    [InlineData(1_793_404_800_000L, 20_757L)] // 2026-10-31 00:00 UTC
    [InlineData(-1L, -1L)]
    public void The_utc_day_starts_at_midnight(long utcMs, long day) => Assert.Equal(day, ForgeRolls.UtcDay(utcMs));

    // The shared start checks on the committed tables (the server, the window and the bots read the same rules).

    private static ForgeRules Rules()
    {
        var rules = ForgeRules.From(LoadRepository());
        Assert.NotNull(rules);
        return rules!;
    }

    /// <summary>The committed data/tables, read with the client's portable reader and validated.</summary>
    internal static ConfigSet LoadRepository()
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
    public void The_committed_tables_build_the_forge_rules()
    {
        var rules = Rules();
        Assert.True(rules.TryGet(ForgeKind.Mold, 1, out var sword));
        Assert.Equal("m3.moldSword", sword.Id);
        Assert.Equal("m3.forgedSword", sword.Output!.Id);
        Assert.Equal(2, sword.Inputs.Count);
        Assert.True(sword.RollsQuality);
        Assert.True(rules.TryGet(ForgeKind.Recipe, 101, out var refine));
        Assert.Equal(RecipeType.Refine, refine.RecipeType);
        Assert.False(refine.RollsQuality);
        Assert.Empty(refine.Actions);
        Assert.False(rules.TryGet(ForgeKind.Recipe, 1, out _)); // a mold's net id is not a recipe
    }

    [Fact]
    public void The_start_checks_gate_on_station_job_and_level()
    {
        var rules = Rules();
        rules.TryGet(ForgeKind.Mold, 1, out var sword);
        rules.TryGet(ForgeKind.Mold, 3, out var ring);
        Assert.Equal(ForgeReason.RecipeUnavailable, ForgeRules.CheckDefinition(ring, NpcInteraction.JewelerBench, 5));
        Assert.Equal(ForgeReason.WrongStation, ForgeRules.CheckDefinition(sword, NpcInteraction.JewelerBench, 5));
        Assert.Equal(ForgeReason.JobRequired, ForgeRules.CheckDefinition(sword, NpcInteraction.Forge, 0));
        Assert.Equal(ForgeReason.None, ForgeRules.CheckDefinition(sword, NpcInteraction.Forge, 1));
        rules.TryGet(ForgeKind.Recipe, 104, out var pickaxeMold); // minJobLevel 5
        Assert.Equal(ForgeReason.JobLevelTooLow, ForgeRules.CheckDefinition(pickaxeMold, NpcInteraction.Forge, 4));
    }

    [Fact]
    public void The_input_checks_follow_the_slot_tags_tiers_and_counts()
    {
        var rules = Rules();
        rules.TryGet(ForgeKind.Mold, 1, out var sword);
        var ingot = rules.Item("m3.ingotT1")!;
        var ore = rules.Item("m3.oreT1")!;
        var hide = rules.Item("m1.cyclopsHide")!;
        Assert.Equal(ForgeReason.None, ForgeRules.CheckInput(sword, 0, ingot, 2));
        Assert.Equal(ForgeReason.MaterialsMissing, ForgeRules.CheckInput(sword, 0, ingot, 1));
        Assert.Equal(ForgeReason.SlotMaterialInvalid, ForgeRules.CheckInput(sword, 0, ore, 9)); // the blade takes ingots
        Assert.Equal(ForgeReason.None, ForgeRules.CheckInput(sword, 1, hide, 1)); // the grip takes a hide (decision 6)
        Assert.Equal(ForgeReason.SlotMaterialInvalid, ForgeRules.CheckInput(sword, 1, null, 1));
        Assert.Equal(ForgeReason.SlotMaterialInvalid, ForgeRules.CheckInput(sword, 2, hide, 1));
        rules.TryGet(ForgeKind.Mold, 5, out var pickaxeT2);
        Assert.Equal(ForgeReason.SlotMaterialInvalid, ForgeRules.CheckInput(pickaxeT2, 0, ore, 9)); // T1 ore below the head's tier 2
        Assert.Equal(ForgeReason.None, ForgeRules.CheckInput(pickaxeT2, 0, rules.Item("m3.oreT2")!, 3));
    }

    [Theory]
    [InlineData(70, 0, 70)]
    [InlineData(70, 10, 80)]
    [InlineData(70, 1_000, -1)] // clamped to ForgeSettings.maxConversionPercent (never 100 %, R-5.2-04)
    [InlineData(50, int.MaxValue, -1)] // no overflow
    [InlineData(70, -500, 1)] // never below 1
    public void A_conversions_percent_is_its_odds_plus_the_bonus_clamped_below_100(int odds, int bonus, int expected)
    {
        var rules = Rules();
        int ceiling = rules.Settings.MaxConversionPercent;
        Assert.InRange(ceiling, 1, 99);
        Assert.Equal(expected < 0 ? ceiling : expected, rules.ConversionPercent(odds, bonus));
    }

    [Fact]
    public void Actions_are_allowed_by_the_mold_up_to_the_session_count()
    {
        var rules = Rules();
        rules.TryGet(ForgeKind.Mold, 1, out var sword);
        rules.TryGet(ForgeKind.Recipe, 101, out var refine);
        Assert.Equal(ForgeReason.None, rules.CheckAction(sword, 2, 0));
        Assert.Equal(ForgeReason.ActionNotAllowed, rules.CheckAction(sword, 99, 0));
        Assert.Equal(ForgeReason.ActionNotAllowed, rules.CheckAction(refine, 1, 0)); // recipes take no actions
        Assert.Equal(ForgeReason.ActionLimitReached, rules.CheckAction(sword, 1, rules.Settings.MaxActions));
    }

    [Fact]
    public void A_sword_from_ingots_and_a_hide_has_the_material_stats_and_number()
    {
        var rules = Rules();
        rules.TryGet(ForgeKind.Mold, 1, out var sword);
        var inputs = new[] { rules.Item("m3.ingotT1")!, rules.Item("m1.cyclopsHide")! };
        Assert.Equal(3 * 20, rules.MaterialNumber(sword, inputs)); // three T1 units
        var stats = rules.CraftedStats(sword, inputs, quality: 0);
        Assert.Equal(12, stats.Single(s => s.StatId == "atk").Flat); // ingot 5 x 2 + hide 2
        Assert.Equal(6, stats.Single(s => s.StatId == "def").Flat); // ingot 3 x 2
        Assert.Equal(15, rules.CraftedStats(sword, inputs, quality: 450).Single(s => s.StatId == "atk").Flat); // rare: +25 %
    }

    [Fact]
    public void Recipes_preview_no_quality_and_molds_a_range()
    {
        var rules = Rules();
        rules.TryGet(ForgeKind.Mold, 1, out var sword);
        rules.TryGet(ForgeKind.Recipe, 101, out var refine);
        var inputs = new[] { rules.Item("m3.ingotT1")!, rules.Item("m1.cyclopsHide")! };
        rules.Preview(refine, new[] { rules.Item("m3.oreT1")! }, Array.Empty<int>(), 0, 0, out int rmin, out int rmax);
        Assert.Equal(0, rmin);
        Assert.Equal(0, rmax);
        rules.Preview(sword, inputs, new[] { 1, 2 }, 0, 0, out int min, out int max);
        Assert.Equal(60 + 10 + 0 - 20, min);
        Assert.Equal(60 + 40 + 60 + 150 + 20, max);
    }
}
