using EconomySimulator;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Tests.Support;
using GameServer.World.Nodes;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Crafting;

namespace GameServer.Tests.Tools;

/// <summary>
/// The economy simulator's M3 half (spec 12.5, 10.2, R-4.2-04, R-5.2-04; WP-M3-12): the named sinks read from the real tables, the
/// node demand loop as the game server steps it, the enchant ladder per job, the conversion arbitrage and the gold faucet check.
/// </summary>
public class EconomySimulatorM3Tests
{
    private static SimEconomy Economy(ConfigSet? tables = null) => SimEconomy.From(tables ?? TestCombat.Tables(), RepositoryPaths.M1World());

    private static EconomySimScenario Row(SimEconomy economy, string id = "m3-15", int hours = 2, int? players = null)
    {
        var source = economy.Scenarios.Single(s => s.Id == id);
        return new EconomySimScenario
        {
            Id = source.Id, Players = players ?? source.Players, Hours = hours, Runs = 1, Seed = source.Seed,
            KillsPerPlayerMinuteMilli = source.KillsPerPlayerMinuteMilli, MonsterIds = source.MonsterIds, KillsPerVisit = source.KillsPerVisit,
            BuyItemIds = source.BuyItemIds, BuyKeep = source.BuyKeep, UsedPerPlayerHour = source.UsedPerPlayerHour,
            SampleMinutes = source.SampleMinutes, SettleWindowPercent = source.SettleWindowPercent,
            SettleTolerancePercent = source.SettleTolerancePercent, HarvestsPerPlayerHour = source.HarvestsPerPlayerHour,
            NodeIds = source.NodeIds, JobLevel = source.JobLevel, OneTimeBuyItemIds = source.OneTimeBuyItemIds,
            ConversionsPerPlayerHour = source.ConversionsPerPlayerHour, EnchantTriesPerPlayerHour = source.EnchantTriesPerPlayerHour,
            SocketsPerPlayerHour = source.SocketsPerPlayerHour, GemRemovalsPerPlayerHour = source.GemRemovalsPerPlayerHour,
        };
    }

    private static double Sink(ScenarioResult result, string name) => result.Sinks.Single(s => s.Name == name).Total;

    private static double Flow(ScenarioResult result, string name) => result.Flows.Single(f => f.Name == name).Total;

    [Fact]
    public void A_scenario_that_harvests_needs_the_node_counters_retention_row()
    {
        // WP-M3-12 review: Simulation checked HarvestSettings only; without GatherSettings DemandModel kept no node counters.
        var tables = TestCombat.Tables();
        tables.Add(new ConfigTable(typeof(GatherSettings), 1, Array.Empty<object>()));
        var economy = Economy(tables);
        Assert.Null(economy.Gather);
        var error = Assert.Throws<InvalidDataException>(() => new Simulation(economy, Row(economy)));
        Assert.Contains("GatherSettings", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_node_counters_match_DemandService_step_by_step()
    {
        var tables = TestCombat.Tables();
        var rules = EconomyRules.From(tables)!;
        var nodes = NodeRules.From(tables);
        var bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
        var service = new DemandService(rules, TestMaps.Open(), bus, nodes);
        service.Subscribe(bus);
        var model = new DemandModel(rules.Demand, rules.Prices, rules.SpawnCurve, rules.RewardCurve, rules.PriceCurve, activePlayers: 1,
            tables.Single<GatherSettings>());
        byte region = TestMaps.Open().Grid.RegionIndexAt(default);
        int[] nodeNetIds = [1, 3];

        for (int step = 0; step < 40; step++)
        {
            int players = 1 + step % 7;
            foreach (int netId in nodeNetIds)
            {
                int harvests = step < 25 ? (step * 3 + netId * 5) % 11 : 0; // harvesting, then the counters recover
                for (int h = 0; h < harvests; h++)
                {
                    bus.Publish(new Harvested(1, 2, "blacksmith", 1, netId, 401, 2, "op" + step + "." + netId + "." + h), regionId: region);
                    model.AddHarvest(netId, region);
                }
            }
            bus.DispatchQueued();
            service.Step(players);
            model.Step(players);

            foreach (int netId in nodeNetIds)
            {
                Assert.Equal(service.HarvestsMilli(netId, region), model.HarvestsMilli(netId, region));
                Assert.Equal(service.NodeSpawnDelayPercent(netId, region), model.NodeSpawnDelayPercent(netId, region));
            }
        }
    }

    [Fact]
    public void The_node_pools_come_from_GatherNodes_the_markers_and_the_harvest_actions()
    {
        var economy = Economy();

        Assert.Equal(TestCombat.Tables().TryGet(typeof(GatherNodes), out var rows) ? rows.Rows.Count : 0, economy.NodePools.Sum(p => p.Points));
        var ring3 = economy.NodePools.Single(p => p.NodeId == "node.gemVein");
        Assert.Equal(("field1.ring3", 2, 180_000, 4_600), (ring3.RegionId, ring3.Points, ring3.RespawnMs, ring3.HarvestMs));
        Assert.Equal(economy.NodePools.Select(p => DemandModel.Key(p.NodeNetId, p.RegionIndex)).Order(),
            economy.NodePools.Select(p => DemandModel.Key(p.NodeNetId, p.RegionIndex))); // demand key order
    }

    [Fact]
    public void Every_named_sink_moves_on_the_m3_rows_and_the_repository_rows_pass()
    {
        var economy = Economy();

        foreach (var id in new[] { "m3-15", "m3-300" })
        {
            var scenario = economy.Scenarios.Single(s => s.Id == id);
            Assert.True(SimCrafting.Plays(scenario));
            var result = SimRunner.Play(economy, scenario);

            Assert.True(result.Pass, string.Join("; ", result.Verdicts.Select(v => $"{v.Name} {v.Pass} {v.Detail}")));
            Assert.Equal(M3Names.Sinks, result.Sinks.Select(s => s.Name));
            Assert.All(result.Sinks, s => Assert.True(s.Total > 0, s.Name));
            Assert.True(Flow(result, M3Names.Harvests) > 0);
            Assert.Contains(result.Verdicts, v => v.Name == "node demand loop settles" && v.Pass);
            var cover = result.Verdicts.Single(v => v.Name == "sink cover");
            Assert.True(cover.WarningOnly);
            Assert.Contains("vendor goods used", cover.Detail); // the sink cover line counts consumable use (plan section 1)
            Assert.True(result.Verdicts.Single(v => v.Name == "item sink cover").WarningOnly);
        }
    }

    [Fact]
    public void A_row_without_m3_activity_has_no_m3_lines_and_its_rolls_do_not_depend_on_the_m3_stream()
    {
        var economy = Economy();
        var plain = Row(economy, "m2-15");
        Assert.False(SimCrafting.Plays(plain));
        var withM3 = Row(economy, "m2-15");
        var m3 = Row(economy);
        (withM3.HarvestsPerPlayerHour, withM3.JobLevel, withM3.ConversionsPerPlayerHour, withM3.EnchantTriesPerPlayerHour) =
            (m3.HarvestsPerPlayerHour, m3.JobLevel, m3.ConversionsPerPlayerHour, m3.EnchantTriesPerPlayerHour);

        var first = SimRunner.Play(economy, plain);
        var second = SimRunner.Play(economy, withM3);

        Assert.Empty(first.Sinks);
        Assert.DoesNotContain(first.Verdicts, v => v.Name == "M3 sinks reached");
        // The kills, the loot and the sales are the same seed's: the M3 activity draws from a stream of its own.
        Assert.Equal(first.HourTotals.Select(h => (h.GoldCreated, h.Kills)), second.HourTotals.Select(h => (h.GoldCreated, h.Kills)));
        Assert.True(Flow(second, M3Names.Harvests) > 0);
    }

    [Fact]
    public void Tools_and_molds_are_bought_once_per_character_at_their_vendor_prices()
    {
        var economy = Economy();
        var result = SimRunner.Play(economy, Row(economy, hours: 3));
        // 50 + 40 x 4 per character (NpcVendorStock placeholders): a one-time sink, every character can pay it within 3 h.
        Assert.Equal(15 * 210, Sink(result, M3Names.VendorToolsAndMolds));
        var perHour = result.Sinks.Single(s => s.Name == M3Names.VendorToolsAndMolds).PerHour;
        Assert.Equal(0, perHour[^1]); // never again: tools never wear out (R-5.2-02), molds are reusable (R-5.3-01)
    }

    [Fact]
    public void Each_sink_moves_only_with_its_rate()
    {
        var economy = Economy();
        var none = Row(economy);
        (none.ConversionsPerPlayerHour, none.EnchantTriesPerPlayerHour, none.SocketsPerPlayerHour, none.GemRemovalsPerPlayerHour) = (0, 0, 0, 0);
        none.OneTimeBuyItemIds = [];

        var result = SimRunner.Play(economy, none);

        Assert.True(Flow(result, M3Names.HarvestUnits) > 0);
        foreach (var sink in new[] { M3Names.VendorToolsAndMolds, M3Names.FailedConversions, M3Names.LostEnchantStones, M3Names.FailedSockets, M3Names.GemRemovalCost })
            Assert.Equal(0, Sink(result, sink));
    }

    [Fact]
    public void A_sink_that_can_never_run_fails_the_check()
    {
        var economy = Economy();
        var row = Row(economy, hours: 1, players: 1);
        row.KillsPerVisit = 1_000; // no outpost visit in the hour: nothing is ever spent or tried

        var result = SimRunner.Play(economy, row);

        Assert.False(result.Pass);
        var verdict = result.Verdicts.Single(v => v.Name == "M3 sinks reached");
        Assert.False(verdict.Pass);
        Assert.Contains(M3Names.LostEnchantStones, verdict.Detail);
    }

    [Fact]
    public void Harvest_counters_that_never_decay_fail_the_node_check()
    {
        var tables = TestCombat.Tables();
        tables.Single<GatherSettings>().HarvestRetentionPermille = 1_000; // a loop with unbounded growth (spec 12.5)
        var economy = Economy(tables);

        var result = SimRunner.Play(economy, Row(economy, hours: 4));

        var verdict = result.Verdicts.Single(v => v.Name == "node demand loop settles");
        Assert.False(verdict.Pass);
        Assert.Contains("still growing: harvests node.", verdict.Detail);
    }

    [Fact]
    public void A_daily_cap_stops_the_conversions_of_its_tier()
    {
        var tables = TestCombat.Tables();
        foreach (ConversionOdds odds in tables.TryGet(typeof(ConversionOdds), out var rows) ? rows.Rows : [])
            odds.DailyCap = 1;
        var economy = Economy(tables);
        var row = Row(economy, hours: 6, players: 1);
        row.ConversionsPerPlayerHour = 1_000;

        var result = SimRunner.Play(economy, row);

        // Two tiers, one try each per UTC day, and the run stays within one day: at most 5 inputs lost per try not won.
        double won = Flow(result, M3Names.ConversionsWon);
        Assert.True(won <= 2 && Sink(result, M3Names.FailedConversions) <= (2 - won) * 5, $"won {won}, lost {Sink(result, M3Names.FailedConversions)}");
    }

    [Fact]
    public void Conversion_math_expected_inputs_follow_the_odds_and_the_keep_chance()
    {
        Assert.Equal(5 / 0.7, ConversionArbitrage.ExpectedInputs(5, 1, 70, 0), 6);
        Assert.Equal(5 * (1 - 0.5 * 0.2) / 0.5, ConversionArbitrage.ExpectedInputs(5, 1, 50, 20), 6);
        Assert.Equal(1 / 0.99 * 1, ConversionArbitrage.ExpectedInputs(1, 1, 99, 0), 6);
        Assert.Equal(5 / 0.5 / 2, ConversionArbitrage.ExpectedInputs(5, 2, 50, 0), 6); // two outputs per success
        // The ore ladder at the placeholders: 5 / 70 % then 5 / 50 %, about 71.4 T1 per T3.
        var steps = new List<ConversionArbitrage.ChainStep> { new(5, 1, 70, 0, 10), new(5, 1, 50, 0, 5) };
        Assert.Equal(5 / 0.7 * (5 / 0.5), ConversionArbitrage.ExpectedChainInputs(steps), 6);
    }

    [Theory]
    [InlineData(70, 0)]
    [InlineData(50, 35)]
    [InlineData(82, 35)]
    public void The_monte_carlo_of_a_chain_agrees_with_the_expected_inputs_and_is_seeded(int percent, int keep)
    {
        var steps = new List<ConversionArbitrage.ChainStep> { new(5, 1, percent, keep, 10), new(3, 1, percent, keep, 5) };

        var (inputs, days) = ConversionArbitrage.Trial(steps, 7, trials: 20_000);
        var (again, _) = ConversionArbitrage.Trial(steps, 7, trials: 20_000);
        var (other, _) = ConversionArbitrage.Trial(steps, 8, trials: 20_000);

        double expected = ConversionArbitrage.ExpectedChainInputs(steps);
        Assert.InRange(inputs.Average(), expected * 0.97, expected * 1.03);
        Assert.Equal(inputs, again);
        Assert.NotEqual(inputs, other);
        Assert.True(days > 0);
    }

    [Fact]
    public void With_the_repository_tables_gathering_the_top_tier_beats_buying_and_converting()
    {
        var routes = ConversionArbitrage.Run(Economy(), 1, jobLevel: 15, goldPerPlayerHour: 1_000);

        Assert.True(ConversionArbitrage.Check(routes).Pass, ConversionArbitrage.Check(routes).Detail);
        // Both ways for T1 ore into T3, without the tree and at its caps.
        var ore = routes.Where(r => r.FromItem == "m3.oreT1" && r.ToItem == "m3.oreT3").ToList();
        Assert.Equal(2, ore.Count);
        Assert.InRange(ore[0].MeanInputs, 71.4 * 0.9, 71.4 * 1.1);
        Assert.True(ore[0].RouteSeconds > ore[0].GatheredSeconds);
        Assert.True(ore[1].MeanInputs < ore[0].MeanInputs); // the conversionOddsPercent and keepInputsPercent caps
        Assert.Equal(("node.ore.t3", "node.ore.t1"), (ore[0].GatheredVia, ore[0].InputVia));
        Assert.True(ore[0].GatheredGold < ore[0].RouteGold);
        // Every gem kind's ladder is covered, through the rough gem its cut takes.
        Assert.Contains(routes, r => r.FromItem == "m3.rubyT1" && r.ToItem == "m3.rubyT3" && r.InputSeconds > 0 && r.GatheredSeconds > 0);
        Assert.Equal(24, routes.Count); // 4 ladders, 3 ranges, 2 bonus levels
    }

    [Fact]
    public void A_route_into_the_top_tier_that_beats_gathering_is_a_structural_flaw()
    {
        var tables = TestCombat.Tables();
        foreach (RecipeInputDef input in tables.TryGet(typeof(RecipeInputDef), out var inputs) ? inputs.Rows : [])
        {
            if (input.RecipeId.StartsWith("m3.convert.ore", StringComparison.Ordinal))
                input.Count = 1; // one T2 into one T3 at 50 %: two T2 per T3, far under the T3 node's labour
        }

        var routes = ConversionArbitrage.Run(Economy(tables), 1, jobLevel: 15, goldPerPlayerHour: 1_000);
        var verdict = ConversionArbitrage.Check(routes);

        Assert.False(verdict.Pass);
        Assert.False(verdict.WarningOnly);
        Assert.Contains("m3.oreT2 to m3.oreT3", verdict.Detail);
    }

    [Fact]
    public void A_vendor_selling_a_low_tier_material_cheap_is_priced_in_gold_and_flagged()
    {
        var tables = TestCombat.Tables();
        var stock = (NpcVendorStock)(tables.TryGet(typeof(NpcVendorStock), out var rows) ? rows.Rows[0] : throw new InvalidOperationException());
        stock.ItemId = "m3.oreT1";
        stock.PriceGold = 1;

        var routes = ConversionArbitrage.Run(Economy(tables), 1, jobLevel: 15, goldPerPlayerHour: 100_000);
        var route = routes.First(r => r.FromItem == "m3.oreT1" && r.ToItem == "m3.oreT3");

        Assert.Equal(1, route.VendorGoldPerInput);
        Assert.Equal(route.MeanInputs, route.RouteGold);
        Assert.True(route.BuyingWins);
        Assert.False(ConversionArbitrage.Check(routes).Pass);
    }

    [Fact]
    public void A_leg_below_the_top_tier_that_beats_gathering_is_a_warning_only()
    {
        var route = new ArbitrageRoute("m3.oreT2", 2, false, "m3.oreT1", 0, 0, 5, 5, 5, 5, 1, 100, "node.ore.t2", 1, "node.ore.t1", null, null, null);

        Assert.True(route.BuyingWins);
        var legs = ConversionArbitrage.CheckLegs([route]);
        Assert.False(legs.Pass);
        Assert.True(legs.WarningOnly);
    }

    [Fact]
    public void The_enchant_ladder_covers_the_bonus_each_jobs_tree_reaches()
    {
        var tables = TestCombat.Tables();
        var reach = EnchantMonteCarlo.ReachableBonusByJob(tables, cap: 6);

        // EnchantOdds is one table; the trees differ: Blacksmith's Runesmith and Jeweler's Runecarver reach +5 pp each, cap 6.
        Assert.Equal(new SortedDictionary<string, int> { ["blacksmith"] = 5, ["jeweler"] = 5 }, reach);
        Assert.Equal(2, EnchantMonteCarlo.ReachableBonusByJob(tables, cap: 2).Values.First());

        var ladders = EnchantMonteCarlo.Run(tables, 1);
        Assert.Equal([0, 5, 6], ladders.Select(l => l.BonusPoints));
        Assert.Equal(["blacksmith", "jeweler"], ladders[1].Jobs);
        Assert.Equal([1, 2, 3], ladders[0].StonesByTier.Select(s => s.StonesPerTry)); // EnchantMaterial per item tier
        Assert.Equal(ladders[0].Targets[^1].MeanAttempts * 3, ladders[0].StonesByTier[2].MeanStonesToNine, 6);
    }

    [Fact]
    public void M3_adds_no_gold_faucet()
    {
        var tables = TestCombat.Tables();
        Assert.True(M3TableChecks.NoGoldFaucet(tables).Pass);

        var price = (PriceBase)(tables.TryGet(typeof(PriceBase), out var rows) ? rows.Rows[0] : throw new InvalidOperationException());
        price.ItemId = "m3.oreT1"; // an outpost would pay for gathered ore (R-4.1-01: it never does)
        var verdict = M3TableChecks.NoGoldFaucet(tables);

        Assert.False(verdict.Pass);
        Assert.Contains("m3.oreT1", verdict.Detail);
    }
}
