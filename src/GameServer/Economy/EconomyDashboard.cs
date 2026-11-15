using GameServer.Persistence;

namespace GameServer.Economy;

/// <summary>One snapshot's gold in circulation (the gold online characters held) and active players.</summary>
public sealed record EconomyCirculationPoint(long TakenAtMs, long GoldOnline, int ActivePlayers);

/// <summary>One world's circulation series, oldest first.</summary>
public sealed record EconomyCirculationSeries(int WorldId, IReadOnlyList<EconomyCirculationPoint> Points);

/// <summary>
/// The economy dashboard's figures over a window (WP-M4-15, M4 decision 13; spec 7.4): the faucet and sink totals per UTC hour
/// and by reason (the ledger's, <see cref="EconomyRepository.HourlyTotalsAsync"/>, mail expiry included), their window
/// totals, the gold in circulation and the active players per world over time, and the items destroyed by reason (both from
/// the window's snapshot rows). The admin panel serves exactly this object, and a test compares it with
/// <c>BotClient report --kind economy</c> over a recorded run.
/// </summary>
public sealed record EconomyDashboardFigures(
    IReadOnlyList<EconomyHourTotals> Hours,
    long GoldCreated,
    long GoldDestroyed,
    IReadOnlyDictionary<string, long> CreatedByReason,
    IReadOnlyDictionary<string, long> DestroyedByReason,
    IReadOnlyList<EconomyCirculationSeries> Circulation,
    IReadOnlyDictionary<string, long> ItemsDestroyedByReason);

/// <summary>Builds <see cref="EconomyDashboardFigures"/> (pure; no I/O).</summary>
public static class EconomyDashboard
{
    public static EconomyDashboardFigures Build(IReadOnlyList<EconomyHourTotals> hours, IReadOnlyList<EconomySnapshotRow> snapshots)
    {
        var circulation = snapshots
            .GroupBy(s => s.WorldId)
            .OrderBy(g => g.Key)
            .Select(g => new EconomyCirculationSeries(g.Key,
                g.OrderBy(s => s.TakenAtMs).Select(s => new EconomyCirculationPoint(s.TakenAtMs, s.GoldOnline, s.ActivePlayers)).ToList()))
            .ToList();
        var items = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            foreach (var (reason, figures) in snapshot.ByReason)
            {
                if (figures.ItemsDestroyed != 0)
                    items[reason] = items.GetValueOrDefault(reason) + figures.ItemsDestroyed;
            }
        }
        return new EconomyDashboardFigures(hours, hours.Sum(h => h.GoldCreated), hours.Sum(h => h.GoldDestroyed),
            SumByReason(hours.Select(h => h.CreatedByReason)), SumByReason(hours.Select(h => h.DestroyedByReason)), circulation, items);
    }

    /// <summary>The window's total per reason, whatever hour each change fell in.</summary>
    public static SortedDictionary<string, long> SumByReason(IEnumerable<IReadOnlyDictionary<string, long>> perHour)
    {
        var total = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var hour in perHour)
        {
            foreach (var (reason, amount) in hour)
                total[reason] = total.GetValueOrDefault(reason) + amount;
        }
        return total;
    }
}
