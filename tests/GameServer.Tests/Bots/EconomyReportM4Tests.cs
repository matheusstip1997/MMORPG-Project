using BotClient.LoadTest;
using GameServer.Events;
using GameServer.Tests.Support;

namespace GameServer.Tests.Bots;

/// <summary>
/// WP-M4-12 (spec 12.5): the economy report's M4 section with the record kinds and fields the M4 WPs write (BotClient README,
/// "M4 telemetry"), over a hand-written sample in that format (<c>Fixtures/economy-telemetry-m4-sample</c>: no M4 record is
/// written yet), and the M4 reasons looked up by name.
/// </summary>
public class EconomyReportM4Tests : IDisposable
{
    private static readonly string Sample =
        Path.Combine(RepositoryPaths.Root(), "tests", "GameServer.Tests", "Bots", "Fixtures", "economy-telemetry-m4-sample");

    private readonly string _empty = Path.Combine(Path.GetTempPath(), "economy-m4-" + Guid.NewGuid().ToString("N"));

    public EconomyReportM4Tests() => Directory.CreateDirectory(_empty);

    public void Dispose() => Directory.Delete(_empty, recursive: true);

    [Fact]
    public void Empty_telemetry_prints_the_M4_section_at_zero()
    {
        var report = EconomyReport.Read(_empty);
        var lines = report.Describe(EconomyNames.None).ToList();
        Assert.Contains(lines, l => l == "M4 fees by type (count, total, destroyed, owner share): listing 0x 0 (0 destroyed, 0 to owners), "
            + "saleTax 0x 0 (0 destroyed, 0 to owners), contract 0x 0 (0 destroyed, 0 to owners)");
        Assert.Contains(lines, l => l == "M4 trades 0: gold moved 0, item units moved 0");
        Assert.Contains(lines, l => l == "M4 listings (created/fees, sold/gold/taxes, cancelled, expired): auction 0/0, 0/0/0, 0, 0, stall 0/0, 0/0/0, 0, 0");
        Assert.Contains(lines, l => l == "M4 stalls opened 0, closed 0");
        Assert.Contains(lines, l => l == "M4 loot bags created 0 (0 units), recovered 0, stolen from 0, expired 0 (0 units, value 0 destroyed)");
        Assert.Contains(lines, l => l == "M4 contracts completed 0 (gold 0, fees 0), expired 0, cancelled 0 (cost 0)");
        Assert.Contains(lines, l => l == "M4 party rolls 0: -");
        Assert.Contains(lines, l => l == "M4 mail sent 0 (0 gold, 0 units), claimed 0 (0 gold, 0 units), expired 0 (0 gold, 0 units destroyed)");
        var reasons = Assert.Single(lines, l => l.StartsWith("M4 gold and items by reason", StringComparison.Ordinal));
        foreach (var name in EconomyReportM4.M4ReasonNames)
            Assert.Contains(name, reasons, StringComparison.Ordinal);
    }

    [Fact]
    public void The_M4_counters_come_from_the_records_the_M4_WPs_write()
    {
        var report = EconomyReport.Read(Sample);
        var m4 = report.M4;
        Assert.Equal(new FeeTotals(1, 4, 4, 0), m4.FeesByType["listing"]);
        Assert.Equal(new FeeTotals(1, 2, 2, 0), m4.FeesByType["saleTax"]);
        Assert.Equal(new FeeTotals(1, 5, 5, 0), m4.FeesByType["contract"]);
        Assert.Equal((1L, 10L, 2L), (m4.Trades, m4.TradeGold, m4.TradeUnits));
        Assert.Equal(new ListingTotals(1, 4, 1, 40, 2, 0, 1), m4.ListingsByKind["auction"]);
        Assert.Equal(new ListingTotals(1, 1, 0, 0, 0, 1, 0), m4.ListingsByKind["stall"]);
        Assert.Equal((1L, 1L), (m4.StallsOpened, m4.StallsClosed));
        Assert.Equal((2L, 10L, 1L, 1L, 1L, 4L, 36L),
            (m4.BagsCreated, m4.BagUnits, m4.BagsRecovered, m4.BagsStolenFrom, m4.BagsExpired, m4.BagUnitsDestroyed, m4.BagValueDestroyed));
        Assert.Equal((1L, 100L, 5L, 1L, 1L, 10L),
            (m4.ContractsCompleted, m4.ContractGold, m4.ContractFees, m4.ContractsExpired, m4.ContractsCancelled, m4.ContractCancelCost));
        Assert.Equal(2, m4.RollsByParty[4][512]);
        Assert.Equal(1, m4.RollsByParty[4][515]);
        Assert.Equal(new MailTotals(2, 38, 1), m4.MailSent);
        Assert.Equal(new MailTotals(1, 38, 0), m4.MailClaimed);
        Assert.Equal(new MailTotals(1, 5, 2), m4.MailExpired);

        var lines = report.Describe(EconomyNames.None).ToList();
        Assert.Contains(lines, l => l == "M4 party rolls 3: party 4: 512 won 2, 515 won 1");
        Assert.Contains(lines, l => l.StartsWith("M4 fees by type (count, total, destroyed, owner share): listing 1x 4 (4 destroyed, 0 to owners)", StringComparison.Ordinal));
        // The JSON of the dashboard (report --json, key m4) lists the same.
        var json = System.Text.Json.JsonSerializer.Serialize(m4.ToJson(report));
        Assert.Contains("\"partyRolls\":{\"4\":{\"512\":2,\"515\":1}}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reason_the_report_has_no_label_for_prints_as_its_number()
    {
        // The sample's last records carry reason 99, a value no InventoryReason has: the report keeps it, by number.
        var report = EconomyReport.Read(Sample);
        Assert.Equal(3, report.Hours.Sum(h => h.DestroyedByReason.GetValueOrDefault("99")));
        Assert.Equal(1, report.ItemsDestroyedByReason["99"]);
        Assert.Equal("99", EconomyReport.ReasonName(99));
        Assert.Equal("none", EconomyReport.ReasonName(0));
    }

    [Fact]
    public void Every_M4_reason_of_the_enum_is_listed_in_the_M4_section_at_its_name()
    {
        // The guard of the M4 reasons (plan section 6, WP-M4-12): an InventoryReason from 20 on (M4) must be one of the names the
        // M4 section lists; the M3 test (EconomyReportM3Tests) already requires its camelCase name at its value in
        // InventoryReasonNames. So a WP that adds Fee = 20 appends "fee" there and the section finds it by name.
        const int FirstM4Reason = 20;
        foreach (var reason in Enum.GetValues<InventoryReason>().Where(r => (int)r >= FirstM4Reason))
        {
            string name = char.ToLowerInvariant(reason.ToString()[0]) + reason.ToString()[1..];
            Assert.Contains(name, EconomyReportM4.M4ReasonNames);
            Assert.True(EconomyReportM4.InEnum(name));
        }
        // A planned name the enum does not have yet is "not in InventoryReason yet", never an error.
        var reasons = EconomyReport.Read(_empty).Describe(EconomyNames.None).Single(l => l.StartsWith("M4 gold and items by reason", StringComparison.Ordinal));
        foreach (var name in EconomyReportM4.M4ReasonNames.Where(n => !EconomyReportM4.InEnum(n)))
            Assert.Contains(name, reasons[reasons.IndexOf("not in InventoryReason yet", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Equal(EconomyReportM4.M4ReasonNames.Count, EconomyReportM4.M4ReasonNames.Distinct().Count());
        // WP-M4-1's FeeCharged writes the FeeType value as feeType: the report names them in that order.
        Assert.Equal(Enum.GetValues<Shared.Simulation.Economy.FeeType>().Select(t => char.ToLowerInvariant(t.ToString()[0]) + t.ToString()[1..]),
            EconomyReportM4.FeeTypeNames);
        Assert.Equal(Enum.GetValues<Shared.Simulation.Economy.FeeType>().Select(t => (int)t), Enumerable.Range(0, EconomyReportM4.FeeTypeNames.Count));
        Assert.DoesNotContain(EconomyReportM4.M4ReasonNames, n => EconomyReport.InventoryReasonNames.Take(FirstM4Reason).Contains(n));
    }
}
