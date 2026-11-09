using BotClient.LoadTest;
using GameServer.Persistence;

namespace GameServer.Tests.Economy;

/// <summary>
/// Compares the dashboard's ledger totals (<see cref="EconomyRepository.HourlyTotalsAsync"/>, what the admin panel's economy
/// view serves) with the BotClient economy report over the same run's telemetry (WP-M4-5, M4 decision 13: the same figures).
/// </summary>
public static class EconomyDashboardData
{
    /// <summary>Hour by hour: the start, gold created and destroyed, and both by reason.</summary>
    public static void AssertSameHours(IReadOnlyList<EconomyHourTotals> ledger, EconomyReport report)
    {
        Assert.Equal(report.Hours.Select(Describe), ledger.Select(Describe));
        Assert.Equal((report.GoldCreated, report.GoldDestroyed), (ledger.Sum(h => h.GoldCreated), ledger.Sum(h => h.GoldDestroyed)));
    }

    /// <summary>The whole window: gold created and destroyed by reason, whatever hour each change fell in.</summary>
    public static void AssertSameTotals(IReadOnlyList<EconomyHourTotals> ledger, EconomyReport report)
    {
        Assert.Equal(Sum(report.Hours.Select(h => h.CreatedByReason)), Sum(ledger.Select(h => h.CreatedByReason)));
        Assert.Equal(Sum(report.Hours.Select(h => h.DestroyedByReason)), Sum(ledger.Select(h => h.DestroyedByReason)));
        Assert.Equal((report.GoldCreated, report.GoldDestroyed), (ledger.Sum(h => h.GoldCreated), ledger.Sum(h => h.GoldDestroyed)));
    }

    private static string Describe(EconomyHour hour) =>
        Describe(hour.HourStartMs, hour.GoldCreated, hour.GoldDestroyed, hour.CreatedByReason, hour.DestroyedByReason);

    private static string Describe(EconomyHourTotals hour) =>
        Describe(hour.HourStartMs, hour.GoldCreated, hour.GoldDestroyed, hour.CreatedByReason, hour.DestroyedByReason);

    private static string Describe(long start, long created, long destroyed, IReadOnlyDictionary<string, long> createdBy,
        IReadOnlyDictionary<string, long> destroyedBy) =>
        $"{start}: +{created} ({Join(createdBy)}) -{destroyed} ({Join(destroyedBy)})";

    private static string Join(IReadOnlyDictionary<string, long> byReason) =>
        string.Join(", ", byReason.Where(r => r.Value != 0).OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key} {r.Value}"));

    private static string Sum(IEnumerable<IReadOnlyDictionary<string, long>> hours)
    {
        var total = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var hour in hours)
        {
            foreach (var (reason, amount) in hour)
                total[reason] = total.GetValueOrDefault(reason) + amount;
        }
        return Join(total);
    }
}
