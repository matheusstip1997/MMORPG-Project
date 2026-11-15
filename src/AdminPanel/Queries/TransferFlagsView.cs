using GameServer.Admin;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Simulation.Economy;
using Shared.Util;

namespace AdminPanel.Queries;

/// <summary>
/// The admin panel's list of large one-way transfers between accounts (WP-M4-15; spec 4.5 R-4.5-02, 7.4, 7.6; M4 decision 17):
/// the <c>transfer_flags</c> rows the game servers' flag scan wrote, newest day first, with the characters' names. Read-only and
/// bounded (<c>AdminSettings.transferFlagMaxRows</c>, <c>cut</c> when the window holds more). Flags only: nothing here acts
/// on a player. Every read is an <c>admin_actions</c> row.
/// </summary>
public static class TransferFlagsView
{
    /// <summary><c>GET /api/transfer-flags?days=&lt;n&gt;</c>: the last <c>n</c> UTC days, today included.</summary>
    public static async Task<IResult> ReadAsync(HttpContext context, int? days, TransferFlagRepository flags, AdminActionRepository actions,
        AdminSettings admin, IClock clock, CancellationToken ct)
    {
        if (days is < 1)
            return AdminEndpoints.Refuse(AdminReasonCode.RequestInvalid);
        int window = Math.Min(days ?? admin.TransferFlagDefaultDays, admin.TransferFlagMaxDays);
        long fromDay = TransferFlagMath.UtcDay(clock.UtcNowMs) - (window - 1);
        // One row past the cap says whether the window held more than the page shows.
        var rows = await flags.ListAsync(fromDay, admin.TransferFlagMaxRows + 1, ct);
        bool cut = rows.Count > admin.TransferFlagMaxRows;
        await AdminEndpoints.RecordAsync(actions, AdminEndpoints.AdminName(context), AdminActionKind.ViewTransferFlags,
            detail: $"days={window}", ct: ct);
        return Results.Json(new
        {
            ok = true,
            windowDays = window,
            flags = cut ? rows.Take(admin.TransferFlagMaxRows).ToList() : rows,
            cut,
            maxRows = admin.TransferFlagMaxRows,
        });
    }
}
