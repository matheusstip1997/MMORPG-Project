using BotClient.LoadTest;
using GameServer.Tests.Support;

namespace GameServer.Tests.Bots;

/// <summary>
/// The economy report's M4 section over a recorded <c>m4-exit</c> run (WP-M4-15; spec 12.5; fixture
/// <c>Fixtures/economy-m4-exit-run</c>, README there): the counters the run's real records give, so the section reads the
/// schema the M4 WPs write (the hand-written sample, <c>EconomyReportM4Tests</c>, covers the kinds a run does not reach).
/// </summary>
public class EconomyReportM4RunTests
{
    private static readonly string Telemetry =
        Path.Combine(RepositoryPaths.Root(), "tests", "GameServer.Tests", "Bots", "Fixtures", "economy-m4-exit-run", "telemetry");

    [Fact]
    public void The_M4_counters_of_the_recorded_run()
    {
        var report = EconomyReport.Read(Telemetry);
        var m4 = report.M4;

        Assert.Equal((416L, 89L), (report.GoldCreated, report.GoldDestroyed)); // outpost sales; the fees (39) and a vendor buy (50)
        Assert.Equal(new FeeTotals(2, 38, 38, 0), m4.FeesByType["listing"]);
        Assert.Equal(new FeeTotals(1, 1, 1, 0), m4.FeesByType["saleTax"]);
        Assert.Equal((1L, 10L, 1L, 5L, 0L), (m4.Trades, m4.TradeGold, m4.TradeUnits, m4.TradeValue, m4.TradesCancelled));
        Assert.Equal(new ListingTotals(2, 19, 1, 15, 0, 1, 0), m4.ListingsByKind["auction"]);
        Assert.Equal(new ListingTotals(2, 19, 1, 20, 1, 1, 0), m4.ListingsByKind["stall"]);
        Assert.Equal((2L, 2L), (m4.StallsOpened, m4.StallsClosed));
        Assert.Equal((4L, 65L, 1L, 1L, 2L, 20L, 140L),
            (m4.BagsCreated, m4.BagUnits, m4.BagsRecovered, m4.BagsStolenFrom, m4.BagsExpired, m4.BagUnitsDestroyed, m4.BagValueDestroyed));
        Assert.Equal((24L, 0L), (m4.RollParts, m4.RollUnitsMailed));
        Assert.Equal(10, m4.RollsByParty[1][1]);
        Assert.Equal(20, m4.RollsByParty[1][2]);
        Assert.Equal(new MailTotals(2, 34, 0), m4.MailSent);
        Assert.Equal(new MailTotals(4, 34, 2), m4.MailClaimed);
        Assert.Equal(new MailTotals(0, 0, 0), m4.MailExpired);

        var lines = report.Describe(EconomyNames.None).ToList();
        Assert.Contains("M4 trades 1: gold moved 10, item units moved 1 (value 5); cancelled 0", lines);
        Assert.Contains(lines, l => l.StartsWith("M4 gold and items by reason", StringComparison.Ordinal) && l.Contains("fee 0/39", StringComparison.Ordinal));
    }
}
