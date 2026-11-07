using BotClient.LoadTest;
using GameServer.Tests.Support;

namespace GameServer.Tests.Bots;

/// <summary>
/// The economy report's M3 section (<c>BotClient report --kind economy</c>, spec 12.5, WP-M3-12) on a recorded sample: WP-M3-12's
/// m3-exit run wp12-exit1 (32 of 32 entries done), cut to the economy records (<c>Fixtures/economy-telemetry-m3-sample</c>). The
/// expected numbers are that run's: the Blacksmith mined, refined, forged, enchanted and drank; the Jeweler cut and socketed.
/// </summary>
public class EconomyReportM3SampleTests
{
    private static EconomyReport Report() => EconomyReport.Read(
        Path.Combine(RepositoryPaths.Root(), "tests", "GameServer.Tests", "Bots", "Fixtures", "economy-telemetry-m3-sample"));

    private static EconomyNames Names() => EconomyNames.From(TestCombat.Tables(), RepositoryPaths.M1World());

    [Fact]
    public void Items_created_and_destroyed_by_reason_match_the_recorded_run()
    {
        var report = Report();

        Assert.Equal(new Dictionary<string, long>
        {
            ["craft"] = 2, ["harvest"] = 11, ["jobStart"] = 4, ["lootPickup"] = 55, ["refine"] = 4, ["starterGear"] = 2, ["vendorPurchase"] = 2,
        }, report.ItemsCreatedByReason);
        Assert.Equal(new Dictionary<string, long>
        {
            ["craft"] = 3, ["enchant"] = 1, ["gemSocket"] = 1, ["itemUsed"] = 1, ["outpostSale"] = 29, ["refine"] = 6,
        }, report.ItemsDestroyedByReason);
        Assert.Equal((854L, 75L), (report.GoldCreated, report.GoldDestroyed)); // the pickaxe and the potion: M3's vendor sinks
    }

    [Fact]
    public void Job_xp_per_activity_and_job_and_the_harvest_yields_carry_their_names()
    {
        var report = Report();
        var names = Names();

        Assert.Equal(143, report.JobXpGranted);
        Assert.Equal(new ExpSource(45, 6), report.JobXpByActivity["gather"]);
        Assert.Equal(new ExpSource(38, 4), report.JobXpByActivity["refine"]);
        Assert.Equal(new ExpSource(40, 2), report.JobXpByActivity["craft"]);
        Assert.Equal(new ExpSource(10, 1), report.JobXpByActivity["enchant"]);
        Assert.Equal(new ExpSource(10, 1), report.JobXpByActivity["socket"]);
        Assert.Equal(new Dictionary<string, long> { ["blacksmith"] = 94, ["jeweler"] = 49 },
            report.JobXpByJob.ToDictionary(j => names.Job(j.Key), j => j.Value));
        Assert.Equal(new HarvestTotals(6, 11), Assert.Single(report.HarvestsByNode, h => names.Node(h.Key) == "node.ore.t1").Value);
        Assert.Equal(new HarvestTotals(6, 11), Assert.Single(report.HarvestsByItem, h => names.Item(h.Key) == "m3.oreT1").Value);
    }

    [Fact]
    public void The_forge_enchant_and_gem_outcomes_match_the_recorded_run_and_print()
    {
        var report = Report();
        var lines = report.DescribeM3(Names()).ToList();

        Assert.Equal(new ForgeTotals(2, 0), report.ForgeByKind["craft"]);
        Assert.Equal(new ForgeTotals(4, 0), report.ForgeByKind["refine"]);
        Assert.Equal(new EnchantTotals(1, 0, 0, 1), report.Enchants);
        Assert.Equal(new GemTotals(1, 0, 0, 0), report.GemsByTier[1]);
        Assert.Contains(lines, l => l == "M3 items by reason (created/destroyed units): jobStart 4/0, harvest 11/0, refine 4/6, craft 2/3, convert 0/0, convertFailed 0/0, enchant 0/1, gemSocket 0/1, gemSocketFailed 0/0, gemRemove 0/0, itemUsed 0/1");
        Assert.Contains(lines, l => l == "M3 harvests 6, units 11: node.ore.t1 6x 11 units");
        Assert.Contains(lines, l => l == "M3 harvest yields by item: m3.oreT1 11 units in 6x");
        Assert.Contains(lines, l => l == "M3 forge ops: craft 2 (0 failed), refine 4 (0 failed), convert 0 (0 failed), mold 0 (0 failed); crafted by rarity: common 1, uncommon 1");
        Assert.Contains(lines, l => l == "M3 enchant tries 1: up 1, same 0, down 0; stones consumed 1");
        Assert.Contains(lines, l => l == "M3 gem sockets won 1, lost 0; removals 0, cost 0 units (by tier: T1 1/0/0/0)");
    }
}
