using BotClient.LoadTest;
using GameServer.Tests.Support;

namespace GameServer.Tests.Bots;

/// <summary>
/// The economy dashboard report (<c>BotClient report --kind economy</c>, R-4.2-07, WP-M2-12) on a recorded sample: QA wave 1's
/// 20-minute m2-exit run on main b9986fe (5 bots, the M2 loop), cut to the economy and exp records
/// (<c>Fixtures/economy-telemetry-sample</c>). The expected numbers are that run's, as QA reported them (1056 gold from
/// 106 units sold, 23 potions bought, 588 exp from 2 run claims).
/// </summary>
public class EconomyReportSampleTests
{
    private static string SampleDirectory() =>
        Path.Combine(RepositoryPaths.Root(), "tests", "GameServer.Tests", "Bots", "Fixtures", "economy-telemetry-sample");

    private static EconomyNames Names() => EconomyNames.From(TestCombat.Tables(), RepositoryPaths.M1World());

    [Fact]
    public void Faucet_sink_and_exp_totals_match_the_recorded_run()
    {
        var report = EconomyReport.Read(SampleDirectory());

        Assert.Equal((1056L, 575L, 106L), (report.GoldCreated, report.GoldDestroyed, report.UnitsSold));
        Assert.Equal(481, report.GoldOnline);
        Assert.Equal(2, report.Hours.Count);
        Assert.Equal((374L, 300L, 1110L), (report.Hours[0].GoldCreated, report.Hours[0].GoldDestroyed, report.Hours[0].ExpGranted));
        Assert.Equal((682L, 275L, 2638L), (report.Hours[1].GoldCreated, report.Hours[1].GoldDestroyed, report.Hours[1].ExpGranted));
        Assert.Equal(575, report.GoldDestroyedByReason["vendorPurchase"]);
        Assert.Equal(3748, report.ExpGranted);
    }

    [Fact]
    public void Sales_vendor_goods_and_exp_per_source_carry_their_names()
    {
        var report = EconomyReport.Read(SampleDirectory());
        var names = Names();
        long Item(string id) => report.SalesByItem.Keys.Concat(report.VendorUnitsByItem.Keys).First(k => names.Item(k) == id);

        Assert.Equal(new ItemSales(90, 832), report.SalesByItem[Item("m1.demonHorn")]);
        Assert.Equal(new ItemSales(16, 224), report.SalesByItem[Item("m1.alienGland")]);
        Assert.Equal(23, report.VendorUnitsByItem[Item("m2.potionHpSmall")]);
        var byMonster = report.SaleGoldByMonster(names, out long shared);
        Assert.Equal(0, shared);
        Assert.Equal(["m1.demon 832", "m1.alien 224"], byMonster.Select(m => $"{names.Monster(m.Key)} {m.Value}"));
        Assert.Equal(new ExpSource(588, 2), report.ExpBySource[0]); // run claims
        Assert.Equal(["m1.demon 1660", "m1.alien 1500"], report.ExpBySource.Where(e => e.Key != 0).Select(e => $"{names.Monster(e.Key)} {e.Value.Exp}"));
    }

    [Fact]
    public void Prices_and_demand_multipliers_are_traced_per_item_and_per_monster_type_and_region()
    {
        var report = EconomyReport.Read(SampleDirectory());
        var names = Names();

        var horn = report.PriceByItem.Single(p => names.Item(p.Key) == "m1.demonHorn").Value;
        Assert.Equal(new Trend(100, 89, 100, 89), horn);
        Assert.Equal(89, report.PricePercentByItem.Single(p => names.Item(p.Key) == "m1.demonHorn").Value);
        Assert.Equal(9, report.DemandByTypeAndRegion.Count);
        var demons = report.DemandByTypeAndRegion.Single(d =>
            names.Monster(d.Key.MonsterDef) == "m1.demon" && names.Region(d.Key.Region) == "m1.combat").Value;
        Assert.Equal(new Trend(122, 122, 152, 152), demons.SpawnDelayPercent);
        Assert.Equal(new Trend(104, 74, 104, 74), demons.RewardPercent);

        var lines = report.Describe(names).ToList();
        Assert.Contains("sale gold by monster type: m1.demon 832, m1.alien 224", lines);
        Assert.Contains(lines, l => l.StartsWith("  m1.demon@m1.combat: spawn 122/122/152/152, reward 104/74/104/74", StringComparison.Ordinal));
        Assert.Contains("exp granted 3748: run claims 588 (2x), m1.demon 1660 (83x), m1.alien 1500 (50x)", lines);
    }

    [Fact]
    public void Without_names_the_report_prints_the_numbers()
    {
        var lines = EconomyReport.Read(SampleDirectory()).Describe(EconomyNames.None).ToList();

        Assert.Contains(lines, l => l.StartsWith("sales (faucet): item 1 90 units 832 gold", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("  monster 1@region 6: spawn", StringComparison.Ordinal));
    }
}
