using BotClient.LoadTest;
using GameServer.Events;

namespace GameServer.Tests.Bots;

/// <summary>
/// WP-M3-12 (spec 12.5): the economy report's M3 section, job XP per activity, items created and destroyed by reason and
/// the harvests per node type, with the telemetry field names the M3 WPs write (BotClient README, "M3 telemetry").
/// </summary>
public class EconomyReportM3Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "economy-m3-" + Guid.NewGuid().ToString("N"));

    public EconomyReportM3Tests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Empty_telemetry_prints_every_m3_counter_at_zero()
    {
        var report = EconomyReport.Read(_directory);
        var lines = report.Describe(EconomyNames.None).ToList();

        Assert.Equal(0, report.JobXpGranted);
        Assert.Empty(report.HarvestsByNode);
        var items = Assert.Single(lines, l => l.StartsWith("M3 items by reason", StringComparison.Ordinal));
        foreach (var reason in EconomyReport.M3ItemReasons)
            Assert.Contains($"{reason} 0/0", items, StringComparison.Ordinal);
        var xp = Assert.Single(lines, l => l.StartsWith("M3 job xp 0 by activity", StringComparison.Ordinal));
        foreach (var activity in new[] { "gather", "refine", "craft", "convert", "enchant", "socket" })
            Assert.Contains($"{activity} 0 (0x)", xp, StringComparison.Ordinal);
        Assert.Contains(lines, l => l == "M3 harvests 0, units 0: -");
    }

    [Fact]
    public void Job_xp_harvests_and_items_by_reason_are_counted_from_the_records_the_m3_wps_write()
    {
        File.WriteAllLines(Path.Combine(_directory, "telemetry-1.jsonl"),
        [
            """{"k":"JobXpGained","t":1000,"c":7,"entity":3,"amount":40,"job":1,"activity":1}""",
            """{"k":"JobXpGained","t":2000,"c":7,"entity":3,"amount":25,"job":1,"activity":3}""",
            """{"k":"JobXpGained","t":3000,"c":7,"entity":3,"amount":10,"job":2,"activity":1}""",
            """{"k":"Harvested","t":1000,"r":1,"c":7,"entity":3,"nodeDef":4,"item":501,"count":3,"seed":99}""",
            """{"k":"Harvested","t":1500,"r":1,"c":7,"entity":3,"nodeDef":4,"item":501,"count":2,"seed":98}""",
            """{"k":"ItemCreated","t":1000,"c":7,"item":113,"rarity":0,"count":2,"reason":6}""",
            """{"k":"ItemDestroyed","t":1000,"c":7,"item":1,"rarity":0,"count":5,"reason":5}""",
        ]);

        var report = EconomyReport.Read(_directory);

        Assert.Equal(75, report.JobXpGranted);
        Assert.Equal(new ExpSource(50, 2), report.JobXpByActivity["gather"]);
        Assert.Equal(new ExpSource(25, 1), report.JobXpByActivity["craft"]);
        Assert.Equal((65L, 10L), (report.JobXpByJob[1], report.JobXpByJob[2]));
        Assert.Equal(new HarvestTotals(2, 5), report.HarvestsByNode[4]);
        Assert.Equal(2, report.ItemsCreatedByReason["vendorPurchase"]);
        Assert.Equal(2, report.VendorUnitsByItem[113]);
        Assert.Equal(5, report.ItemsDestroyedByReason["outpostSale"]);
        var lines = report.Describe(EconomyNames.None).ToList();
        Assert.Contains(lines, l => l.StartsWith("M3 job xp 75 by activity: gather 50 (2x), refine 0 (0x), craft 25 (1x)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l == "M3 harvests 2, units 5: node 4 2x 5 units");
    }

    [Fact]
    public void The_report_names_every_inventory_reason_of_the_server_in_order()
    {
        // A WP that appends an InventoryReason (M3: itemUsed, harvest, craft, ...) appends its name to the report too, so the
        // M3 counters find it by name (WP-M3-12).
        // A value another WP reserved and has not landed yet keeps its slot in the list (16 enchant, WP-M3-8; WP-M3-9 took 17-19).
        foreach (var reason in Enum.GetValues<InventoryReason>())
            Assert.Equal(char.ToLowerInvariant(reason.ToString()[0]) + reason.ToString()[1..], EconomyReport.InventoryReasonNames[(int)reason]);
        Assert.Equal(Enum.GetValues<InventoryReason>().Max(r => (int)r) + 1, EconomyReport.InventoryReasonNames.Count);
    }

    [Fact]
    public void The_report_follows_the_servers_activities_forge_kinds_and_job_hash()
    {
        Assert.Equal(Enum.GetValues<Shared.Simulation.Jobs.JobActivity>().Max(a => (int)a) + 1, EconomyReport.JobActivityNames.Count);
        foreach (var kind in Enum.GetValues<GameServer.Persistence.CraftOpKind>().Where(k => k <= GameServer.Persistence.CraftOpKind.Mold))
            Assert.Equal(kind.ToString().ToLowerInvariant(), EconomyReport.ForgeOpNames[(int)kind]);
        foreach (var job in new[] { "blacksmith", "jeweler" })
            Assert.Equal(GameServer.Telemetry.SkillIdHash.Of(job), EconomyNames.JobHash(job));
    }

    [Fact]
    public void Forge_enchant_and_gem_records_and_harvest_yields_are_counted_and_named()
    {
        long blacksmith = EconomyNames.JobHash("blacksmith");
        File.WriteAllLines(Path.Combine(_directory, "telemetry-1.jsonl"),
        [
            """{"k":"ForgeOp","t":1000,"c":7,"entity":3,"def":101,"kind":2,"seed":1,"quality":0,"rarity":0}""",
            """{"k":"ForgeOp","t":1100,"c":7,"entity":3,"def":201,"kind":1,"seed":2,"quality":400,"rarity":2}""",
            """{"k":"ForgeOp","t":1200,"c":7,"entity":3,"def":102,"kind":3,"seed":3,"quality":0,"rarity":-1}""",
            """{"k":"ForgeOp","t":1300,"c":7,"entity":3,"def":102,"kind":3,"seed":4,"quality":0,"rarity":0}""",
            """{"k":"Enchanted","t":2000,"c":7,"entity":3,"item":900,"fromLevel":0,"toLevel":1,"stones":1,"seed":5}""",
            """{"k":"Enchanted","t":2100,"c":7,"entity":3,"item":900,"fromLevel":2,"toLevel":2,"stones":2,"seed":6}""",
            """{"k":"Enchanted","t":2200,"c":7,"entity":3,"item":900,"fromLevel":3,"toLevel":2,"stones":1,"seed":7}""",
            """{"k":"GemSocketed","t":3000,"c":7,"entity":3,"gem":410,"tier":1,"seed":8,"instance":55,"success":1}""",
            """{"k":"GemSocketed","t":3100,"c":7,"entity":3,"gem":410,"tier":1,"seed":9,"instance":55,"success":0}""",
            """{"k":"GemRemoved","t":3200,"c":7,"entity":3,"gem":420,"tier":2,"seed":10,"instance":55,"cost":2}""",
            """{"k":"Harvested","t":4000,"r":1,"c":7,"entity":3,"nodeDef":1,"item":401,"count":3,"seed":11}""",
            """{"k":"Harvested","t":4100,"r":1,"c":7,"entity":3,"nodeDef":1,"item":401,"count":2,"seed":12}""",
            """{"k":"Harvested","t":4200,"r":1,"c":7,"entity":3,"nodeDef":1,"item":405,"count":1,"seed":13}""",
            $$"""{"k":"JobXpGained","t":5000,"c":7,"entity":3,"job":{{blacksmith}},"activity":7,"amount":9,"repeats":0}""",
        ]);

        var report = EconomyReport.Read(_directory);
        var names = EconomyNames.From(Support.TestCombat.Tables(), Support.RepositoryPaths.M1World());
        var lines = report.DescribeM3(names).ToList();

        Assert.Equal(new ForgeTotals(1, 0), report.ForgeByKind["refine"]);
        Assert.Equal(new ForgeTotals(2, 1), report.ForgeByKind["convert"]);
        Assert.Equal(1, report.CraftedByRarity[2]);
        Assert.Equal(new EnchantTotals(1, 1, 1, 4), report.Enchants);
        Assert.Equal(new GemTotals(1, 1, 0, 0), report.GemsByTier[1]);
        Assert.Equal(new GemTotals(0, 0, 1, 2), report.GemsByTier[2]);
        Assert.Equal(new HarvestTotals(2, 5), report.HarvestsByItem[401]);
        Assert.Equal(new ExpSource(9, 1), report.JobXpByActivity["gemRemove"]);
        Assert.Contains(lines, l => l == "M3 harvests 3, units 6: node.ore.t1 3x 6 units");
        Assert.Contains(lines, l => l.StartsWith("M3 harvest yields by item: m3.oreT1 5 units in 2x", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("M3 forge ops: craft 1 (0 failed), refine 1 (0 failed), convert 2 (1 failed), mold 0 (0 failed); crafted by rarity: ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l == "M3 enchant tries 3: up 1, same 1, down 1; stones consumed 4");
        Assert.Contains(lines, l => l == "M3 gem sockets won 1, lost 1; removals 1, cost 2 units (by tier: T1 1/1/0/0, T2 0/0/1/2)");
        Assert.Contains(lines, l => l.Contains("by job: blacksmith 9", StringComparison.Ordinal));
    }
}
