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
}
