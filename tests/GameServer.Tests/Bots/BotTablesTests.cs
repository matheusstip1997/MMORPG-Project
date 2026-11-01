using System.Globalization;
using BotClient;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;

namespace GameServer.Tests.Bots;

/// <summary>Backlog J: the cross-table rules of the bot tables (DataValidator runs the same).</summary>
public class BotTablesTests
{
    [Fact]
    public void A_scenario_must_fit_the_server_limits_and_the_allowed_classes()
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(NetSecurity), 2, [new NetSecurity { MaxPeers = 40, ConnectRequestsPerSecond = 10 }]));
        set.Add(new ConfigTable(typeof(CharacterCreationConfig), 1,
            [new CharacterCreationConfig { AllowedInitialClasses = ["warrior", "archer"] }]));
        var scenario = new BotScenario { Id = "x", Bots = 50, RampPerSecond = 20, Classes = ["warrior", "mage"] };

        var errors = new RowErrors();
        scenario.Validate(set, errors);

        Assert.Equal(["Bots", "RampPerSecond", "Classes"], errors.Items.Select(e => e.Field));
    }

    [Fact]
    public void Seed_items_name_an_item_with_a_count_it_holds_and_an_exit_run_takes_none()
    {
        // Wave 1 QA F6: BotScenario.seedItemIds, "<itemId>" or "<itemId>:<count>" (as the loop step entries).
        var set = GameServer.Host.Config.ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All).Set;
        string[] Fields(params string[] items)
        {
            var errors = new RowErrors();
            new BotScenario { Id = "x", Bots = 1, RampPerSecond = 1, Classes = ["warrior"], SeedItemIds = items }.Validate(set, errors);
            return errors.Items.Select(e => $"{e.Field}: {e.Message}").ToArray();
        }

        Assert.Empty(Fields("m3.pickaxeT1", "m3.oreT1:4", "m1.cyclopsHide:1"));
        Assert.Single(Fields("no.such.item"));
        Assert.Single(Fields("m3.oreT1:x"));
        Assert.Single(Fields("m3.oreT1:0"));
        Assert.Single(Fields("m3.pickaxeT1:2")); // an instance item takes 1
        Assert.True(BotScenario.TryParseSeedItem("m3.oreT1:4", out string item, out int count) && (item, count) == ("m3.oreT1", 4));

        var errors = new RowErrors();
        new BotScenario { Id = "x", Bots = 1, RampPerSecond = 1, Classes = ["warrior"], SeedItemIds = ["m3.oreT1"], RequireStepsDone = true }
            .Validate(set, errors);
        Assert.Contains(errors.Items, e => e.Field == "SeedItemIds" && e.Message.Contains("earns its items", StringComparison.Ordinal));
    }

    [Fact]
    public void The_repository_scenarios_are_valid_and_name_the_ci_and_exit_runs()
    {
        var result = GameServer.Host.Config.ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.True(result.Set.TryGet(typeof(BotScenario), out var table));
        var scenarios = table.Rows.Cast<BotScenario>().ToDictionary(s => s.Id);
        // Spec 11.5 / user decision 2026-10-26: about 20 bots in CI, 2 minutes on pushes, 5 on main; spec 12.4: 50 bots.
        Assert.Equal((20, 120_000, 50), (scenarios["ci-smoke"].Bots, scenarios["ci-smoke"].DurationMs, scenarios["ci-smoke"].TickP99MaxPercentOfPeriod));
        Assert.Equal((20, 300_000), (scenarios["ci-merge"].Bots, scenarios["ci-merge"].DurationMs));
        Assert.Equal((50, 100), (scenarios["m1-exit"].Bots, scenarios["m1-exit"].TickP99MaxPercentOfPeriod));
        // WP-M2-12, spec 12.2: the M2 exit run plays the loop to level 30; the loop's steps are every known step.
        var exit = scenarios["m2-exit"];
        Assert.Equal((5, 30, "m2-exit"), (exit.Bots, exit.TargetLevel, exit.BehaviourId));
        Assert.Equal(0, scenarios["ci-smoke"].TargetLevel);
        Assert.True(result.Set.TryGet(typeof(BotBehaviour), out var behaviours));
        var loop = behaviours.Rows.Cast<BotBehaviour>().Single(b => b.Id == "m2-loop");
        Assert.Equal(BotStepRegistryTests.M2StepIds, loop.LoopSteps);
        Assert.Empty(behaviours.Rows.Cast<BotBehaviour>().Single(b => b.Id == "field-combat").LoopSteps);
        // Placeholders mirroring the QA kill step (QaTimeouts.ReengageAbovePercent 40, BackOffRecoverSeconds 30).
        Assert.All(behaviours.Rows.Cast<BotBehaviour>().Where(b => b.Id is "field-combat" or "field-combat-hit1" or "m2-loop" or "m2-exit"),
            b => Assert.Equal((40, 30_000), (b.ReengageAbovePercent, b.BackOffRecoverMs)));
    }

    [Fact]
    public void The_exit_run_plays_rounds_of_12_kills_with_planned_back_off_legs_and_the_loop_check_stays_as_it_was()
    {
        var result = GameServer.Host.Config.ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.True(result.Set.TryGet(typeof(BotBehaviour), out var table));
        var rows = table.Rows.Cast<BotBehaviour>().ToDictionary(b => b.Id);
        var exit = rows["m2-exit"];
        var loop = rows["m2-loop"];

        // WP-M2-12: 12 kills per outpost visit (the reasoning is in the row), a step limit that holds them, the QA leg margins.
        Assert.Equal((12, 600_000), (exit.LoopKillsPerRound, exit.LoopStepTimeoutMs));
        Assert.Equal((true, 400, 300), (exit.PlanBackOffLegs, exit.BackOffLeashMarginCm, exit.AvoidAggroMarginCm));
        Assert.Equal(BotStepRegistryTests.M2StepIds, exit.LoopSteps);
        // The field camps only (three monsters each): the M1 combat points are respawn-bound for a bot that waits there.
        Assert.Equal(loop.HomeMarkerIds.Where(h => h.StartsWith("field1.", StringComparison.Ordinal)), exit.HomeMarkerIds);
        Assert.Contains(loop.HomeMarkerIds, h => h.StartsWith("m1.spawn.combat.", StringComparison.Ordinal));
        Assert.True(exit.HomesByLevel);
        // The quick every-step check and the before baseline keep one kill per round and straight legs.
        Assert.Equal((1, 120_000, false), (loop.LoopKillsPerRound, loop.LoopStepTimeoutMs, loop.PlanBackOffLegs));
        // The M3 exit scripts (WP-M3-12) fight like m2-exit.
        Assert.All(rows.Values.Where(b => b.Id is not ("m2-exit" or "m3-exit-blacksmith" or "m3-exit-jeweler")), b => Assert.False(b.PlanBackOffLegs));
    }

    [Fact]
    public void A_planned_back_off_needs_a_leash_margin_below_every_home_monsters_leash()
    {
        var tables = TestCombat.Tables();
        int leash = MonsterDef.Find(tables, "m1.demon")!.LeashRangeCm;
        BotBehaviour Row(bool planned, int marginCm) => new()
        {
            Id = "x", HomeMarkerIds = ["m1.spawn.combat.a"], HealBelowPercent = 50, BackOffBelowPercent = 20,
            PlanBackOffLegs = planned, BackOffLeashMarginCm = marginCm,
        };
        string[] Fields(BotBehaviour row)
        {
            var errors = new RowErrors();
            row.Validate(tables, errors);
            return errors.Items.Select(e => e.Field ?? "").ToArray();
        }

        Assert.Empty(Fields(Row(true, leash - 1)));
        Assert.Equal(["BackOffLeashMarginCm"], Fields(Row(true, leash)));
        Assert.Empty(Fields(Row(false, leash))); // not planned: the margin is unused
    }

    [Fact]
    public void The_m3_exit_run_seeds_two_characters_to_level_12_and_plays_the_blacksmith_and_jeweler_scripts()
    {
        // WP-M3-12, plan docs/plans/m3-jobs-crafting-plan.md section 1: the exit criterion's order.
        var result = GameServer.Host.Config.ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.True(result.Set.TryGet(typeof(BotScenario), out var scenarios));
        Assert.True(result.Set.TryGet(typeof(BotBehaviour), out var behaviours));
        var rows = behaviours.Rows.Cast<BotBehaviour>().ToDictionary(b => b.Id);
        var exit = scenarios.Rows.Cast<BotScenario>().Single(s => s.Id == "m3-exit");
        Assert.Equal((2, 16, true, 1_800_000), (exit.Bots, exit.SeedLevel, exit.RequireStepsDone, exit.DurationMs)); // wave 1 QA fix: the wolf grip leg
        Assert.Equal(["m3-exit-blacksmith", "m3-exit-jeweler"], [exit.BehaviourOf(0), exit.BehaviourOf(1)]);
        Assert.Equal("warrior", exit.Classes[0]); // the sword
        Assert.Equal(["level:16", "takeJob:blacksmith", "fight:12", "pickup", "sell", "fight:12", "pickup", "sell", "fight:12", "pickup",
            "sell", "buy:m3.pickaxeT1", "gather:6",
            "fight:13@field1.spawn.wolf.a1", "pickup", "refine", "refine", "craft", "learnJob:blacksmith", "equip:crafted", "fight", "enchant", "buy:m2.potionHpSmall", "useItem:m2.potionHpSmall"], rows["m3-exit-blacksmith"].LoopSteps);
        Assert.Equal(["level:16", "takeJob:jeweler", "refine", "socket", "learnJob:jeweler"], rows["m3-exit-jeweler"].LoopSteps);
        Assert.All(new[] { rows["m3-exit-blacksmith"], rows["m3-exit-jeweler"] }, b => Assert.True(b.LoopOnce));
        // The M3 loop: the M2 loop's steps with every M3 step but level mixed in.
        var loop = scenarios.Rows.Cast<BotScenario>().Single(s => s.Id == "m3-loop");
        Assert.Equal((0, "m3-loop", false), (loop.SeedLevel, loop.BehaviourOf(3), rows["m3-loop"].LoopOnce));
        // Wave 1 QA F6: the loop's level seed gives the tool and the forge materials; the exit run earns its items.
        Assert.Equal(["m3.pickaxeT1", "m3.oreT1:4", "m3.ingotT1:2", "m1.cyclopsHide"], loop.SeedItemIds);
        Assert.Empty(exit.SeedItemIds);
        Assert.Equal(BotBehaviour.LoopStepIds.Where(i => i != "level").OrderBy(i => i, StringComparer.Ordinal),
            rows["m3-loop"].LoopSteps.Select(e => e.Split(':')[0]).Distinct().OrderBy(i => i, StringComparer.Ordinal));
    }

    /// <summary>Largest chance an exit script's harvests leave its refines short (a placeholder for "practically never").</summary>
    private const double MaxShortChance = 0.01;

    [Theory]
    [InlineData("m3-exit-blacksmith")]
    [InlineData("m3-exit-jeweler")]
    public void An_exit_script_gathers_what_its_refines_need_and_refines_what_its_craft_needs(string behaviourId)
    {
        // Wave 1 QA F3: one gather (1-3 ore) before two refines of 2 ore each failed every run. From the tables, not numbers
        // copied here: each refine takes its recipe's inputs (the first refine recipe of the script's job, as RefineStep
        // picks it); every harvest of an ore entry gives at least one unit (HarvestMath's floor, whatever the repeat
        // percent), so the script is short only when too many harvests roll another entry (the gem or stone, R-5.2-01).
        var data = BotData.Load(RepositoryPaths.Root());
        var entries = data.Behaviour(behaviourId).LoopSteps.Select(e => e.Split(':', 2)).ToList();
        string job = entries.Single(e => e[0] == "takeJob")[1];
        var recipe = data.Rows<RecipeDef>().FirstOrDefault(r => r.Type == RecipeType.Refine && r.JobId == job);
        int refines = entries.Count(e => e[0] == "refine");
        if (recipe == null)
        {
            // No refine content for the job yet (the Jeweler until WP-M3-9): the step reports "not yet" (ForgeStepTests).
            Assert.DoesNotContain(entries, e => e[0] == "gather");
            return;
        }
        Assert.True(refines > 0);

        int harvests = entries.Where(e => e[0] == "gather").Sum(e => e.Length > 1 ? int.Parse(e[1], CultureInfo.InvariantCulture) : 1);
        int toolTier = entries.Where(e => e[0] == "buy" && e.Length > 1)
            .Select(e => data.Rows<ToolDef>().FirstOrDefault(t => t.ItemId == e[1])?.Tier ?? 0).DefaultIfEmpty(0).Max();
        var nodes = data.Rows<NodeDef>().Where(n => n.JobId == job && n.MinToolTier <= toolTier).ToList();
        Assert.NotEmpty(nodes);
        foreach (var input in data.Rows<RecipeInputDef>().Where(i => i.RecipeId == recipe.Id))
        {
            int needed = refines * input.Count;
            foreach (var node in nodes) // the step takes the nearest node of any of these types
            {
                var table = data.Rows<NodeYieldEntry>().Where(y => y.TableId == node.YieldTableId && y.MinJobLevel <= 1).ToList();
                double hit = (double)table.Where(y => y.ItemId == input.ItemId).Sum(y => y.Weight) / table.Sum(y => y.Weight);
                double shortChance = BinomialBelow(harvests, hit, needed);
                Assert.True(shortChance < MaxShortChance,
                    $"{behaviourId}: {harvests} harvests of {node.Id} give {needed} {input.ItemId} for {refines} x {recipe.Id} only "
                    + $"with a {1 - shortChance:P2} chance; raise the gather count");
            }
        }

        // The craft's slots that take the refined output are filled by the refines (the first mold of the job, as CraftStep picks).
        var output = data.Rows<ItemDef>().Single(i => i.Id == recipe.OutputItemId);
        if (entries.Any(e => e[0] == "craft") && data.Rows<MoldDef>().FirstOrDefault(m => m.JobId == job) is { } mold)
        {
            int takes = data.Rows<MoldSlotDef>()
                .Where(s => s.MoldId == mold.Id && s.MinTier <= output.Tier && s.Tags.Intersect(output.MaterialTags).Any())
                .Sum(s => s.Count);
            Assert.True(refines * recipe.OutputCount >= takes, $"{behaviourId}: {refines} refines make too few {output.Id} for {mold.Id}");

            // Every other slot (the sword's grip) comes from a camp fight before the craft: fight:<kills>@<camp> counts only the
            // camp's monster (FightStep), whose loot table must drop a material the slot takes, missing it under 1 % (each kill
            // rolls the table's rolls, every unit picked up by the fight and the pickup after it).
            int craftAt = entries.FindIndex(e => e[0] == "craft");
            foreach (var slot in data.Rows<MoldSlotDef>().Where(s => s.MoldId == mold.Id
                         && !(s.MinTier <= output.Tier && s.Tags.Intersect(output.MaterialTags).Any())))
            {
                double miss = 1;
                foreach (var entry in entries.Take(craftAt).Where(e => e[0] == "fight" && e.Length > 1 && e[1].Contains('@')))
                {
                    int kills = int.Parse(entry[1].Split('@')[0], CultureInfo.InvariantCulture);
                    string camp = entry[1].Split('@')[1];
                    string monster = data.Rows<SpawnPoints>().Single(s => s.MarkerId == camp).MonsterId;
                    var loot = data.Rows<LootTableDef>().Single(t => t.MonsterId == monster);
                    var rows = data.Rows<LootTableEntry>().Where(e => e.TableId == loot.Id).ToList();
                    double fits = rows.Where(e => data.Rows<ItemDef>().Single(i => i.Id == e.ItemId) is var item
                            && item.Tier >= slot.MinTier && item.MaterialTags.Intersect(slot.Tags).Any())
                        .Sum(e => e.Weight) / (double)(rows.Sum(e => e.Weight) + loot.NothingWeight);
                    miss *= Math.Pow(1 - fits, (double)kills * loot.Rolls);
                }
                Assert.True(miss < MaxShortChance, $"{behaviourId}: the camp fights before the craft miss a material for {slot.Id} with a {miss:P2} chance");
            }
        }
    }

    /// <summary>The chance that fewer than <paramref name="needed"/> of <paramref name="trials"/> trials hit (each with <paramref name="p"/>).</summary>
    private static double BinomialBelow(int trials, double p, int needed)
    {
        double sum = 0;
        for (int k = 0; k < needed && k <= trials; k++)
        {
            double choose = 1;
            for (int i = 0; i < k; i++)
                choose = choose * (trials - i) / (i + 1);
            sum += choose * Math.Pow(p, k) * Math.Pow(1 - p, trials - k);
        }
        return sum;
    }

    [Fact]
    public void Step_entries_parse_with_an_argument_and_a_script_may_repeat_entries_and_skip_the_fight()
    {
        Assert.True(BotBehaviour.TryParseLoopStep("buy:m3.pickaxeT1", out string id, out string argument));
        Assert.Equal(("buy", "m3.pickaxeT1"), (id, argument));
        Assert.True(BotBehaviour.TryParseLoopStep("fight", out id, out argument));
        Assert.Equal(("fight", ""), (id, argument));
        Assert.False(BotBehaviour.TryParseLoopStep("buy:", out _, out _));
        Assert.False(BotBehaviour.TryParseLoopStep(":x", out _, out _));

        string[] Errors(BotBehaviour row)
        {
            var errors = new RowErrors();
            row.Validate(errors);
            return errors.Items.Where(e => e.Field == "LoopSteps").Select(e => e.Message).ToArray();
        }
        BotBehaviour Row(bool once, params string[] steps) => new()
        {
            Id = "x", HomeMarkerIds = ["m1.spawn.combat.a"], HealBelowPercent = 50, BackOffBelowPercent = 20, LoopOnce = once, LoopSteps = steps,
        };
        Assert.Empty(Errors(Row(false, "fight", "buy", "buy:m2.potionHpSmall"))); // two entries of one step, different arguments
        Assert.Equal(2, Errors(Row(false, "fight", "fight", "dance:x")).Length); // twice, unknown
        Assert.Single(Errors(Row(false, "sell")));                              // a loop needs the fight
        Assert.Empty(Errors(Row(true, "level:12", "sell", "sell")));           // a script repeats and needs no fight
        Assert.Single(Errors(Row(true)));                                      // but has a step
        Assert.Single(Errors(Row(true, "buy:")));                              // an empty argument
    }

    [Fact]
    public void A_scenario_that_requires_its_steps_done_plays_scripts_and_its_seed_is_a_level_that_exists()
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(BotBehaviour), 1,
            [new BotBehaviour { Id = "loop", LoopSteps = ["fight"] }, new BotBehaviour { Id = "script", LoopOnce = true, LoopSteps = ["level:12"] }]));
        set.Add(new ConfigTable(typeof(ProgressionSettings), 1, [new ProgressionSettings { MaxLevel = 50 }]));
        string[] Fields(BotScenario scenario)
        {
            var errors = new RowErrors();
            scenario.Validate(set, errors);
            return errors.Items.Select(e => e.Field ?? "").ToArray();
        }

        Assert.Empty(Fields(new BotScenario { Id = "a", Classes = ["warrior"], BehaviourId = "loop", BotBehaviourIds = ["script"], RequireStepsDone = true, SeedLevel = 12 }));
        Assert.Equal(["RequireStepsDone"], Fields(new BotScenario { Id = "b", Classes = ["warrior"], BehaviourId = "loop", RequireStepsDone = true }));
        Assert.Equal(["SeedLevel"], Fields(new BotScenario { Id = "c", Classes = ["warrior"], BehaviourId = "script", SeedLevel = 51 }));
    }

    [Fact]
    public void Loop_steps_must_be_known_once_each_and_include_the_fight()
    {
        var behaviour = new BotBehaviour
        {
            Id = "x", HomeMarkerIds = ["m1.spawn.combat.a"], LoopSteps = ["pickup", "dance", "pickup"],
        };
        var errors = new RowErrors();
        behaviour.Validate(errors);
        Assert.Equal(3, errors.Items.Count(e => e.Field == "LoopSteps")); // unknown, twice, no fight

        var fine = new BotBehaviour
        {
            Id = "y", HomeMarkerIds = ["m1.spawn.combat.a"], LoopSteps = ["fight", "sell"], HealBelowPercent = 50, BackOffBelowPercent = 20,
        };
        errors = new RowErrors();
        fine.Validate(errors);
        Assert.Empty(errors.Items);
    }

    [Fact]
    public void The_re_engage_line_lies_between_the_back_off_and_heal_lines_and_the_recovery_time_covers_the_back_off()
    {
        // The QA kill step's recovery rule (QaFightPlan.Recovered): back off below 20 %, fight again at 40 %, heal below 50 %.
        BotBehaviour Row(int reengage, int recoverMs) => new()
        {
            Id = "x", HomeMarkerIds = ["m1.spawn.combat.a"], HealBelowPercent = 50, BackOffBelowPercent = 20, BackOffMs = 3_000,
            ReengageAbovePercent = reengage, BackOffRecoverMs = recoverMs,
        };
        string[] Fields(BotBehaviour row)
        {
            var errors = new RowErrors();
            row.Validate(errors);
            return errors.Items.Select(e => e.Field ?? "").ToArray();
        }

        Assert.Empty(Fields(Row(40, 30_000)));
        Assert.Empty(Fields(Row(20, 3_000)));
        Assert.Empty(Fields(Row(50, 3_000)));
        Assert.Equal(["ReengageAbovePercent"], Fields(Row(19, 30_000)));
        Assert.Equal(["ReengageAbovePercent"], Fields(Row(51, 30_000)));
        Assert.Equal(["BackOffRecoverMs"], Fields(Row(40, 2_999)));
    }
}
