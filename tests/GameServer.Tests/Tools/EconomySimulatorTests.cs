using EconomySimulator;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Items;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Economy;

namespace GameServer.Tests.Tools;

/// <summary>
/// The economy simulator (spec 12.5, WP-M2-12): its demand model runs the game server's demand loop step for step, the
/// respawn rule is the spawn controller's, the settle checks tell a settled series from a drifting one, runs are
/// deterministic for a seed, a table that never decays fails the check, and the repository's scenarios pass it (what CI
/// runs with <c>--check</c>).
/// </summary>
public class EconomySimulatorTests
{
    [Fact]
    public void The_demand_model_matches_DemandService_step_by_step()
    {
        var tables = TestCombat.Tables();
        var rules = EconomyRules.From(tables)!;
        var map = TestMaps.Open();
        byte region = map.Grid.RegionIndexAt(FixVec2.FromCm(0, 0));
        var bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
        var service = new DemandService(rules, map, null);
        service.Subscribe(bus);
        var model = new DemandModel(rules.Demand, rules.Prices, rules.SpawnCurve, rules.RewardCurve, rules.PriceCurve, activePlayers: 1);
        Assert.True(ItemRules.From(tables)!.TryGetItem("m1.demonHorn", out var horn));
        int[] demonNetIds = [1, 2]; // m1.demon, m1.alien

        for (int step = 0; step < 40; step++)
        {
            int players = 1 + step % 7;
            foreach (int netId in demonNetIds)
            {
                int kills = step < 25 ? (step * 3 + netId * 5) % 11 : 0; // hunting, then the counters recover
                for (int k = 0; k < kills; k++)
                {
                    bus.Publish(new Killed(1, 2, 0, netId, 0, 0), 5);
                    model.AddKill(netId, region);
                }
            }
            bus.DispatchQueued();
            int sold = step % 4 == 0 ? 0 : (step * 7) % 13 + 1;
            if (sold > 0)
            {
                service.RegisterSale(horn, sold);
                model.AddSale(horn.NetId, sold);
            }
            service.Step(players);
            model.Step(players);

            foreach (int netId in demonNetIds)
            {
                Assert.Equal(service.KillsMilli(netId, region), model.KillsMilli(netId, region));
                Assert.Equal(service.SpawnDelayPercent(netId, region), model.SpawnDelayPercent(netId, region));
                Assert.Equal(service.RewardPercent(netId, region), model.RewardPercent(netId, region));
            }
            Assert.Equal(service.SalesMilli(horn.NetId), model.SalesMilli(horn.NetId));
            Assert.Equal(service.ExpectedSalesMilli(), model.ExpectedSalesMilli());
            int servicePrice = DemandMath.Clamp(rules.PriceCurve.Evaluate(DemandMath.RatioPermille(service.SalesMilli(horn.NetId),
                service.ExpectedSalesMilli())), rules.Prices.FloorPercent, rules.Prices.MaxPercent);
            Assert.Equal(servicePrice, model.PricePercent(horn.NetId));
        }
    }

    [Theory]
    [InlineData(30_000, 100, 5_000, 30_000)]
    [InlineData(30_000, 200, 5_000, 60_000)] // heavily hunted: slower (R-4.2-03)
    [InlineData(30_000, 80, 5_000, 24_000)]
    [InlineData(30_000, 10, 5_000, 5_000)] // a shortened delay keeps the floor
    [InlineData(3_000, 50, 5_000, 3_000)] // a base below the floor is not raised to it
    public void The_respawn_delay_follows_the_spawn_controller(int baseMs, int percent, int minMs, long expected) =>
        Assert.Equal(expected, SpawnRules.RespawnDelayMs(baseMs, percent, minMs));

    [Fact]
    public void The_settle_checks_tell_a_settled_series_from_a_moving_one()
    {
        double[] flat = [140, 120, 105, 100, 101, 99, 100, 100, 101, 100];
        double[] swinging = [100, 140, 60, 140, 60, 140, 60, 140, 60, 140];
        double[] climbing = Enumerable.Range(0, 10).Select(i => 10_000.0 * (i + 1)).ToArray();
        int from = SettleCheck.WindowStart(flat.Length, 50);

        Assert.Equal(5, from);
        Assert.Equal(2, SettleCheck.Spread(flat, from));
        Assert.Equal(80, SettleCheck.Spread(swinging, from));
        Assert.False(SettleCheck.Grows(flat, from, 15, DemandMath.Unit));
        Assert.True(SettleCheck.Grows(climbing, from, 15, DemandMath.Unit));
        Assert.Equal(0, SettleCheck.DeviationPercent([500, 500, 500], 0));
        Assert.Equal(20, SettleCheck.DeviationPercent([400, 600], 0), 6);
        Assert.Equal(9, SettleCheck.WindowStart(10, 1)); // at least one sample
    }

    [Fact]
    public void A_run_is_deterministic_for_its_seed()
    {
        var economy = SimEconomy.From(TestCombat.Tables(), RepositoryPaths.M1World());
        var scenario = Short(economy.Scenarios.Single(s => s.Id == "m2-15"));

        var first = SimRunner.Play(economy, scenario);
        var again = SimRunner.Play(economy, scenario);
        var other = SimRunner.Play(economy, scenario, seedOverride: scenario.Seed + 100);

        Assert.Equal(first.HourTotals, again.HourTotals);
        Assert.Equal(first.Samples.Keys, again.Samples.Keys);
        foreach (var (name, values) in first.Samples)
            Assert.Equal(values, again.Samples[name]);
        Assert.NotEqual(first.HourTotals, other.HourTotals);
        Assert.True(first.HourTotals.All(h => h.GoldCreated > 0 && h.Kills > 0));
    }

    [Fact]
    public void Counters_that_never_decay_fail_the_check()
    {
        var tables = TestCombat.Tables();
        tables.Single<DemandSettings>().KillRetentionPermille = 1_000; // a loop with unbounded growth (spec 12.5)
        var economy = SimEconomy.From(tables, RepositoryPaths.M1World());

        var result = SimRunner.Play(economy, Short(economy.Scenarios.Single(s => s.Id == "m2-15"), hours: 4));

        Assert.False(result.Pass);
        var verdict = result.Verdicts.Single(v => v.Name == "counters bounded");
        Assert.False(verdict.Pass);
        Assert.Contains("kills m1.demon@", verdict.Detail);
    }

    [Fact]
    public void Every_repository_scenario_passes_the_check()
    {
        var economy = SimEconomy.From(TestCombat.Tables(), RepositoryPaths.M1World());

        foreach (var scenario in economy.Scenarios)
        {
            var result = SimRunner.Play(economy, scenario);
            Assert.True(result.Pass, $"{scenario.Id}: {string.Join("; ", result.Verdicts.Select(v => $"{v.Name} {v.Pass} {v.Detail}"))}");
            // The sink cover is reported, never a failure (fees are M4); from M3 it counts the vendor goods used.
            var cover = result.Verdicts.Single(v => v.Name == "sink cover");
            Assert.True(cover.WarningOnly);
            Assert.Contains("vendor goods used", cover.Detail);
        }
    }

    [Fact]
    public void Consumable_use_is_live_capped_by_each_goods_meters_and_counted_on_the_sink_line()
    {
        // WP-M3-3 (spec 12.5 consumable use): the placeholder ConsumableDef rows cap the use per hour by the meter decay over
        // the cost and the cooldown; a scenario asking for more uses only what the meters allow, and never more than held.
        var economy = SimEconomy.From(TestCombat.Tables(), RepositoryPaths.M1World());
        var potion = economy.VendorGood("m2.potionHpSmall");
        Assert.Equal(24, potion.MaxUsesPerHour); // 10 toxicity a minute = 600 an hour, 25 a potion (placeholders)
        Assert.Equal(10, economy.VendorGood("m2.breadLoaf").MaxUsesPerHour); // 5 satiety a minute, 30 a loaf

        var source = economy.Scenarios.Single(s => s.Id == "m2-15");
        var asked = Short(source, hours: 3);
        asked.UsedPerPlayerHour = 2;
        var greedy = Short(source, hours: 3);
        greedy.UsedPerPlayerHour = 1_000;
        var none = Short(source, hours: 3);
        none.UsedPerPlayerHour = 0;

        double Used(EconomySimScenario scenario) => SimRunner.Play(economy, scenario).HourTotals.Sum(h => h.GoodsUsed);
        double usedAsked = Used(asked), usedGreedy = Used(greedy);

        Assert.Equal(0, Used(none));
        Assert.True(usedAsked > 0);
        // Three goods, at most their meter caps (24 + 24 + 10 an hour) per player-hour, and what the gold kept in stock.
        Assert.True(usedGreedy <= (24 + 24 + 10) * source.Players * 3.0, $"{usedGreedy}");
        Assert.True(usedGreedy >= usedAsked);
        var line = SimRunner.Play(economy, asked).Verdicts.Single(v => v.Name == "sink cover").Detail;
        Assert.Contains("vendor goods used", line);
        Assert.DoesNotContain("consumable use M3", line);
    }

    [Fact]
    public void The_pools_split_a_type_by_the_region_of_its_spawn_points()
    {
        var economy = SimEconomy.From(TestCombat.Tables(), RepositoryPaths.M1World());

        var demons = economy.Pools.Where(p => p.MonsterId == "m1.demon").ToList();

        Assert.Equal(["field1.ring1", "m1.combat"], demons.Select(p => p.RegionId).Order(StringComparer.Ordinal));
        Assert.Equal(economy.Pools.Select(p => p.Key).Order(), economy.Pools.Select(p => p.Key)); // demand key order
        Assert.Equal(TestCombat.Tables().TryGet(typeof(SpawnPoints), out var points) ? points.Rows.Count : 0, economy.Pools.Sum(p => p.Points));
    }

    [Fact]
    public void The_enchant_ladder_reaches_plus_nine_and_the_tree_bonus_shortens_it()
    {
        var ladders = EnchantMonteCarlo.Run(TestCombat.Tables(), 1);

        Assert.Equal(2, ladders.Count); // without the bonus and at its JobBonusCap
        Assert.All(ladders, l => Assert.Equal(0, l.Unfinished));
        Assert.True(EnchantMonteCarlo.Check(ladders).Pass);
        var plain = ladders[0].Targets;
        Assert.Equal(1, plain[0].MaxAttempts); // +0 rises at 100 % (placeholder)
        Assert.True(plain.Zip(plain.Skip(1)).All(p => p.First.MeanAttempts < p.Second.MeanAttempts)); // each level costs more
        Assert.True(ladders[1].Targets[^1].MeanAttempts < plain[^1].MeanAttempts); // the bonus helps
        Assert.Equal(plain[^1].MeanAttempts * ladders[0].StonesPerTry, plain[^1].MeanStones, 6); // every try sinks its stones
        Assert.Equal(EnchantMonteCarlo.Run(TestCombat.Tables(), 1)[0], ladders[0], new LadderComparer()); // seeded
    }

    [Fact]
    public void A_level_that_cannot_rise_fails_the_enchant_check()
    {
        var odds = Enumerable.Range(0, EnchantLimits.MaxLevel)
            .Select(l => new EnchantOdds { Id = "l" + l, Level = l, PlusPercent = l == 4 ? 0 : 50, SamePercent = l == 4 ? 100 : 50 }).ToList();

        var ladder = EnchantMonteCarlo.Ladder(odds, 0, 1, 1, trials: 5, guard: 1_000);

        Assert.Equal(5, ladder.Unfinished);
        Assert.False(EnchantMonteCarlo.Check([ladder]).Pass);
    }

    private sealed class LadderComparer : IEqualityComparer<EnchantLadder>
    {
        public bool Equals(EnchantLadder? x, EnchantLadder? y) => x != null && y != null && x.Targets.SequenceEqual(y.Targets);
        public int GetHashCode(EnchantLadder obj) => obj.Targets.Count;
    }

    private static EconomySimScenario Short(EconomySimScenario source, int hours = 2) => new()
    {
        Id = source.Id, Players = source.Players, Hours = hours, Runs = 1, Seed = source.Seed,
        KillsPerPlayerMinuteMilli = source.KillsPerPlayerMinuteMilli, MonsterIds = source.MonsterIds, KillsPerVisit = source.KillsPerVisit,
        BuyItemIds = source.BuyItemIds, BuyKeep = source.BuyKeep, UsedPerPlayerHour = source.UsedPerPlayerHour,
        SampleMinutes = source.SampleMinutes, SettleWindowPercent = source.SettleWindowPercent,
        SettleTolerancePercent = source.SettleTolerancePercent,
    };
}
