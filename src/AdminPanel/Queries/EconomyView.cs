using System.Text.Json;
using AdminPanel.Logs;
using GameServer.Admin;
using GameServer.Economy;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Util;

namespace AdminPanel.Queries;

/// <summary>The newest <c>EconomyTotals</c> telemetry record: the world's running totals since it started (the live line).</summary>
public sealed record EconomyLive(long T, long WorldId, long GoldCreated, long GoldDestroyed, long GoldOnline, long ActivePlayers);

/// <summary>
/// The admin panel's economy view (WP-M4-5; spec 7.4 "economy dashboards", 7.2 R-7.2-02, M4 decision 13's data side, the page's
/// final shape still Open): faucet and sink totals per UTC hour and by reason from <c>gold_ledger</c> (SQL, the authoritative
/// figures, the same as <c>BotClient report --kind economy</c> over the telemetry), the <c>faucet_sink_snapshots</c> rows (gold
/// in circulation and active players over time) and the newest <c>EconomyTotals</c> telemetry line. Read-only: the databases
/// and the telemetry files, never world state. Every read is an <c>admin_actions</c> row.
/// </summary>
public static class EconomyView
{
    public const string EconomyTotalsKind = "EconomyTotals";

    /// <summary><c>GET /api/economy?hours=&lt;n&gt;</c>: the last <c>n</c> UTC hours, the current one included.</summary>
    public static async Task<IResult> ReadAsync(HttpContext context, int? hours, EconomyRepository economy, AdminPanelSettings settings,
        AdminActionRepository actions, AdminSettings admin, IClock clock, CancellationToken ct)
    {
        if (hours is < 1)
            return AdminEndpoints.Refuse(AdminReasonCode.RequestInvalid);
        int window = Math.Min(hours ?? admin.EconomyDefaultHours, admin.EconomyMaxHours);
        long toMs = clock.UtcNowMs;
        long fromMs = Window(toMs, window);

        var hourly = await economy.HourlyTotalsAsync(fromMs, toMs + 1, ct);
        var snapshots = await economy.SnapshotsAsync(fromMs, toMs + 1, admin.EconomySnapshotMaxRows, ct);
        var live = Live(settings.TelemetryDirectory, admin.TelemetryTailMaxBytes);
        await AdminEndpoints.RecordAsync(actions, AdminEndpoints.AdminName(context), AdminActionKind.ViewEconomy,
            detail: $"hours={window}", ct: ct);

        return Results.Json(new
        {
            ok = true,
            windowHours = window,
            fromMs,
            toMs,
            reasons = EconomyReasonNames.All,
            hours = hourly,
            goldCreated = hourly.Sum(h => h.GoldCreated),
            goldDestroyed = hourly.Sum(h => h.GoldDestroyed),
            createdByReason = SumByReason(hourly.Select(h => h.CreatedByReason)),
            destroyedByReason = SumByReason(hourly.Select(h => h.DestroyedByReason)),
            snapshots,
            live,
        });
    }

    /// <summary>The start of the UTC hour <paramref name="hours"/> - 1 hours before the one holding <paramref name="nowMs"/>.</summary>
    public static long Window(long nowMs, int hours) =>
        nowMs - Mod(nowMs, EconomyRepository.HourMs) - (hours - 1) * EconomyRepository.HourMs;

    /// <summary>The newest <c>EconomyTotals</c> record in the telemetry folder, or null.</summary>
    public static EconomyLive? Live(string telemetryDirectory, int maxBytes)
    {
        var records = TelemetryTail.Read(telemetryDirectory, new TelemetryFilter(null, null, EconomyTotalsKind), 1, maxBytes);
        if (records.Count == 0)
            return null;
        var record = records[0];
        return new EconomyLive(Long(record, "t"), Long(record, "w"), Long(record, "goldCreated"), Long(record, "goldDestroyed"),
            Long(record, "goldOnline"), Long(record, "activePlayers"));
    }

    private static SortedDictionary<string, long> SumByReason(IEnumerable<IReadOnlyDictionary<string, long>> perHour)
    {
        var total = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var hour in perHour)
        {
            foreach (var (reason, amount) in hour)
                total[reason] = total.GetValueOrDefault(reason) + amount;
        }
        return total;
    }

    private static long Long(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var v) ? v : 0;

    private static long Mod(long value, long divisor) => ((value % divisor) + divisor) % divisor;
}
