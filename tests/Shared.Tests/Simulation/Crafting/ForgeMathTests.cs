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

    // Spec 5.3 M3 decision 13 (WP-M3-14): the quality places each material line inside its range.

    [Theory]
    // low, high, quality, rangeQuality, value
    [InlineData(10, 12, 500, 1000, 11)] // the spec's example: an ingot atk 10..12 at quality 500
    [InlineData(10, 12, 0, 1000, 10)]
    [InlineData(10, 12, 1000, 1000, 12)]
    [InlineData(10, 12, 5000, 1000, 12)] // above the scale: the top
    [InlineData(10, 12, -5, 1000, 10)] // below 0: the bottom
    [InlineData(10, 12, 249, 1000, 10)] // 0.498 of the step rounds down
    [InlineData(10, 12, 250, 1000, 11)] // half a step rounds up
    [InlineData(5, 6, 499, 1000, 5)]
    [InlineData(5, 6, 500, 1000, 6)]
    [InlineData(-10, -4, 500, 1000, -7)] // a negative line
    [InlineData(7, 7, 900, 1000, 7)] // no range
    public void A_material_line_sits_in_its_range_by_the_quality(long low, long high, int quality, int rangeQuality, long value) =>
        Assert.Equal(value, CraftedStatsCalc.Position(low, high, quality, rangeQuality));

    [Fact]
    public void The_position_never_falls_as_the_quality_rises()
    {
        for (int span = 0; span <= 25; span++)
        {
            long previous = long.MinValue;
            for (int quality = 0; quality <= 1200; quality++)
            {
                long value = CraftedStatsCalc.Position(3, 3 + span, quality, 1000);
                Assert.InRange(value, 3, 3 + span);
                Assert.True(value >= previous);
                previous = value;
            }
        }
    }

    [Fact]
    public void Crafted_stats_place_each_line_by_units_sum_per_stat_then_add_the_quality_bonus()
    {
        var uses = new[]
        {
            new MaterialStatUse("atk", 5, 7, 0, 0, 2), // two ingots: 10..14
            new MaterialStatUse("def", 3, 4, 0, 0, 2), // 6..8
            new MaterialStatUse("atk", 1, 2, 1, 1, 1), // a demon ash: 1..2, 1 %
        };
        var low = CraftedStatsCalc.Build(uses, 0, 1000, 0);
        Assert.Equal(new[] { "atk", "def" }, low.Select(s => s.StatId));
        Assert.Equal((11, 1), (low[0].Flat, low[0].Percent));
        Assert.Equal(6, low[1].Flat);

        var middle = CraftedStatsCalc.Build(uses, 500, 1000, 0);
        Assert.Equal(12 + 2, middle[0].Flat); // 10..14 at half is 12; 1..2 at half rounds up to 2
        Assert.Equal(7, middle[1].Flat);

        var rare = CraftedStatsCalc.Build(uses, 500, 1000, 25);
        Assert.Equal(17, rare[0].Flat); // 14 x 125 % = 17.5, rounded toward zero
        Assert.Equal(8, rare[1].Flat); // 7 x 125 % = 8.75

        var top = CraftedStatsCalc.Build(uses, 1000, 1000, 0);
        Assert.Equal((16, 8), (top[0].Flat, top[1].Flat));
    }

    [Fact]
    public void Crafted_stats_leave_out_zero_lines_and_keep_at_most_sixteen()
    {
        var uses = Enumerable.Range(0, 20).Select(i => new MaterialStatUse("s" + i, i == 3 ? 0 : 1, i == 3 ? 0 : 1, 0, 0, 1)).ToList();
        var stats = CraftedStatsCalc.Build(uses, 0, 1000, 0);
        Assert.Equal(ItemProperties.MaxCraftedStats, stats.Count);
        Assert.DoesNotContain(stats, s => s.StatId == "s3");
    }

    [Fact]
    public void The_stat_range_holds_every_result_in_its_quality_range_and_reaches_both_ends()
    {
        var uses = new[]
        {
            new MaterialStatUse("atk", 5, 7, 0, 0, 2),
            new MaterialStatUse("maxHp", -10, -4, 0, 0, 1), // a negative line: the band bonus makes it fall
        };
        foreach (var (qMin, qMax) in new[] { (0, 970), (150, 450), (390, 410), (500, 500) })
        {
            var range = CraftedStatsCalc.Range(uses, qMin, qMax, 1000, Bands);
            int atkLow = int.MaxValue, atkHigh = int.MinValue, hpLow = int.MaxValue, hpHigh = int.MinValue;
            for (int q = qMin; q <= qMax; q++)
            {
                var stats = CraftedStatsCalc.Build(uses, q, 1000, Bands.StatBonusPercent(q));
                int atk = stats.Single(s => s.StatId == "atk").Flat, hp = stats.Single(s => s.StatId == "maxHp").Flat;
                (atkLow, atkHigh, hpLow, hpHigh) = (Math.Min(atkLow, atk), Math.Max(atkHigh, atk), Math.Min(hpLow, hp), Math.Max(hpHigh, hp));
            }
            var atkRange = range.Single(r => r.StatId == "atk");
            var hpRange = range.Single(r => r.StatId == "maxHp");
            Assert.Equal((atkLow, atkHigh), (atkRange.FlatLow, atkRange.FlatHigh));
            Assert.Equal((hpLow, hpHigh), (hpRange.FlatLow, hpRange.FlatHigh));
        }
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
        Assert.Equal(ForgeReason.None, ForgeRules.CheckDefinition(ring, NpcInteraction.JewelerBench, 5)); // craftable since WP-M3-9
        Assert.Equal(ForgeReason.WrongStation, ForgeRules.CheckDefinition(ring, NpcInteraction.Forge, 5));
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
        var ash = rules.Item("m1.demonAsh")!;
        // Decision 13 (WP-M3-14): the sword's two slots are free; any forge input fills either.
        Assert.True(sword.Inputs.All(i => i.IsFree));
        foreach (var material in new[] { ingot, ore, ash, rules.Item("m1.cyclopsHide")!, rules.Item("m3.rubyT1")! })
        {
            Assert.Equal(ForgeReason.None, ForgeRules.CheckInput(sword, 0, material, 1));
            Assert.Equal(ForgeReason.None, ForgeRules.CheckInput(sword, 1, material, 1));
        }
        Assert.Equal(ForgeReason.MaterialsMissing, ForgeRules.CheckInput(sword, 0, ingot, 0));
        Assert.Equal(ForgeReason.SlotMaterialInvalid, ForgeRules.CheckInput(sword, 0, rules.Item("m3.enchantStone")!, 9)); // no forge input
        Assert.Equal(ForgeReason.SlotMaterialInvalid, ForgeRules.CheckInput(sword, 0, rules.Item("m3.roughGemT1")!, 9));
        Assert.Equal(ForgeReason.SlotMaterialInvalid, ForgeRules.CheckInput(sword, 0, rules.Item("m1.demonHorn")!, 9)); // sell only
        Assert.Equal(ForgeReason.SlotMaterialInvalid, ForgeRules.CheckInput(sword, 1, null, 1));
        Assert.Equal(ForgeReason.SlotMaterialInvalid, ForgeRules.CheckInput(sword, 2, ash, 1));
        rules.TryGet(ForgeKind.Mold, 5, out var pickaxeT2);
        Assert.Equal(ForgeReason.SlotMaterialInvalid, ForgeRules.CheckInput(pickaxeT2, 0, ore, 9)); // T1 below the slot's tier 2
        Assert.Equal(ForgeReason.None, ForgeRules.CheckInput(pickaxeT2, 0, rules.Item("m3.oreT2")!, 1));
        Assert.Equal(ForgeReason.None, ForgeRules.CheckInput(pickaxeT2, 0, rules.Item("m2.wolfSinew")!, 1)); // a T2 drop
        rules.TryGet(ForgeKind.Mold, 3, out var ring);
        Assert.Equal(ForgeReason.SlotMaterialInvalid, ForgeRules.CheckInput(ring, 0, ash, 1)); // the ring's stone takes a cut gem
        Assert.Equal(ForgeReason.None, ForgeRules.CheckInput(ring, 0, rules.Item("m3.rubyT1")!, 1));
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
    public void A_sword_from_two_demon_ash_has_the_ash_stats_placed_by_the_quality()
    {
        // The user's check of decision 13: the same item, its stats from the materials used.
        var rules = Rules();
        rules.TryGet(ForgeKind.Mold, 1, out var sword);
        var ash = rules.Item("m1.demonAsh")!;
        var inputs = new[] { ash, ash };
        Assert.Equal(2 * 20, rules.MaterialNumber(sword, inputs)); // two T1 units
        var low = rules.CraftedStats(sword, inputs, quality: 0);
        Assert.Equal(new[] { "maxHp", "atk" }, low.Select(s => s.StatId));
        Assert.Equal((16, 2), (low[0].Flat, low[1].Flat)); // 2 x 8..12 and 2 x 1..2 at their bottom
        var middle = rules.CraftedStats(sword, inputs, quality: 500); // rare: +25 %
        Assert.Equal((25, 5), (middle[0].Flat, middle[1].Flat)); // 2 x 10 and 2 x 2 at half, x 125 %
        var top = rules.CraftedStats(sword, inputs, quality: 1000); // legendary: +100 %
        Assert.Equal((48, 8), (top[0].Flat, top[1].Flat));

        var mixed = rules.CraftedStats(sword, new[] { rules.Item("m3.ingotT1")!, ash }, quality: 0);
        Assert.Equal(new[] { "atk", "def", "maxHp" }, mixed.Select(s => s.StatId)); // another stat line, the same item id
        Assert.Equal((6, 3, 8), (mixed[0].Flat, mixed[1].Flat, mixed[2].Flat));
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
        Assert.Equal(40 + 10 + 0 - 20, min);
        Assert.Equal(40 + 40 + 60 + 150 + 20, max);
    }

    [Fact]
    public void Before_start_the_preview_spans_what_the_materials_can_reach()
    {
        var rules = Rules();
        rules.TryGet(ForgeKind.Mold, 1, out var sword);
        rules.TryGet(ForgeKind.Recipe, 101, out var refine);
        var ingot = rules.Item("m3.ingotT1")!;
        rules.ReachRange(sword, new[] { ingot, ingot }, out int min, out int max);
        Assert.Equal(40 - rules.Caps.SmallTerm, min); // the materials, nothing else, the small term at its lowest
        Assert.Equal(40 + rules.Caps.Actions + rules.Caps.JobTree + rules.Caps.Minigame + rules.Caps.ServerRoll + rules.Caps.SmallTerm, max);
        rules.ReachRange(refine, new[] { rules.Item("m3.oreT1")! }, out int rmin, out int rmax);
        Assert.Equal((0, 0), (rmin, rmax));

        var range = rules.StatRange(sword, new[] { ingot, ingot }, min, max);
        var atk = range.Single(r => r.StatId == "atk");
        Assert.Equal(10, atk.FlatLow); // 2 x 5 at the bottom, common
        Assert.Equal(rules.CraftedStats(sword, new[] { ingot, ingot }, max).Single(s => s.StatId == "atk").Flat, atk.FlatHigh);
        Assert.Empty(rules.StatRange(refine, new[] { rules.Item("m3.oreT1")! }, 0, 0));
    }
}
