using System.Security.Claims;
using AdminPanel.Logs;
using AdminPanel.Queries;
using GameServer.Admin;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Shared.Data.Tables;

namespace AdminPanel;

public sealed record SignInRequest(string? Name);

public sealed record KickRequest(string? RequestId, string? Note);

/// <summary>
/// The panel's JSON API (spec 7.4 implementation, admin panel v0). Every response has <c>ok</c>; a refusal has a
/// <see cref="AdminReasonCode"/> and a status code that follows it. Everything but signing in needs the admin cookie,
/// and every request that changes something also needs the header <c>X-Admin-Request: 1</c> (a cross-site form cannot
/// send it). Every admin action is recorded in <c>admin_actions</c> with who did it (searches, views, kicks); reading the
/// action log itself is not.
/// </summary>
public static class AdminEndpoints
{
    public const string RequestHeader = "X-Admin-Request";

    public static void Map(WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Json(new { ok = true }));

        var api = app.MapGroup("/api").AddEndpointFilter(RequireRequestHeaderOnChanges);
        api.MapPost("/session", SignInAsync);
        api.MapDelete("/session", (Delegate)SignOutAsync);

        var signedIn = api.MapGroup("").AddEndpointFilter(RequireSignedIn);
        signedIn.MapGet("/session", (HttpContext context) => Results.Json(new { ok = true, admin = AdminName(context) }));
        signedIn.MapGet("/characters", SearchAsync);
        signedIn.MapGet("/characters/{characterId:long}", GetCharacterAsync);
        signedIn.MapPost("/characters/{characterId:long}/kick", KickAsync);
        signedIn.MapGet("/actions", ListActionsAsync);
        signedIn.MapGet("/actions/{requestId}", GetActionAsync);
        signedIn.MapGet("/logs", ReadLogsAsync);
        signedIn.MapGet("/telemetry", ReadTelemetryAsync);
    }

    /// <summary>A refusal with its reason code and matching status code.</summary>
    public static IResult Refuse(AdminReasonCode reason) =>
        Results.Json(new { ok = false, reasonCode = reason }, statusCode: reason switch
        {
            AdminReasonCode.NotSignedIn => StatusCodes.Status401Unauthorized,
            AdminReasonCode.SignInUnavailable => StatusCodes.Status403Forbidden,
            AdminReasonCode.CharacterNotFound or AdminReasonCode.ActionNotFound => StatusCodes.Status404NotFound,
            AdminReasonCode.RequestConflict => StatusCodes.Status409Conflict,
            AdminReasonCode.ServerUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        });

    private static async Task<IResult> SignInAsync(HttpContext context, SignInRequest? request, IAdminIdentity identity,
        AdminSettings admin, AdminActionRepository actions, CancellationToken ct)
    {
        var reason = identity.Verify(request?.Name, admin.MaxDetailChars, out var adminName);
        if (reason != AdminReasonCode.None)
            return Refuse(reason);

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, adminName)],
            CookieAuthenticationDefaults.AuthenticationScheme));
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        await RecordAsync(actions, adminName, AdminActionKind.SignIn, ct: ct);
        return Results.Json(new { ok = true, admin = adminName });
    }

    private static async Task<IResult> SignOutAsync(HttpContext context)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Json(new { ok = true });
    }

    /// <summary>By name prefix (ignoring case) and/or account (<c>dev:&lt;n&gt;</c>, a SteamID, or <c>id:&lt;accountId&gt;</c>).</summary>
    private static async Task<IResult> SearchAsync(HttpContext context, string? name, string? account, CharacterQueries characters,
        AdminActionRepository actions, AdminSettings admin, CancellationToken ct)
    {
        name = name?.Trim();
        AccountQuery? accountQuery = null;
        if (!string.IsNullOrWhiteSpace(account))
        {
            if (!AccountQuery.TryParse(account, out var parsed))
                return Refuse(AdminReasonCode.RequestInvalid);
            accountQuery = parsed;
        }
        if ((string.IsNullOrEmpty(name) && accountQuery == null) || (name?.Length ?? 0) + (account?.Length ?? 0) > admin.MaxDetailChars)
            return Refuse(AdminReasonCode.RequestInvalid);

        var found = await characters.SearchAsync(name, accountQuery, admin.SearchMaxResults, ct);
        await RecordAsync(actions, AdminName(context), AdminActionKind.Search, detail: $"name={name} account={account?.Trim()}", ct: ct);
        return Results.Json(new { ok = true, characters = found });
    }

    private static async Task<IResult> GetCharacterAsync(HttpContext context, long characterId, CharacterQueries characters,
        AdminActionRepository actions, AdminSettings admin, CancellationToken ct)
    {
        var details = await characters.GetAsync(characterId, ct);
        if (details == null)
            return Refuse(AdminReasonCode.CharacterNotFound);
        await RecordAsync(actions, AdminName(context), AdminActionKind.ViewCharacter, characterId, details.Summary.AccountId, ct: ct);
        var history = await actions.ListForCharacterAsync(characterId, admin.ActionHistoryLimit, ct);
        return Results.Json(new { ok = true, character = details, actions = history });
    }

    /// <summary>
    /// Records a kick for the game server that holds the character to run on its tick (spec 7.4). Idempotent on
    /// <see cref="KickRequest.RequestId"/>: a double submit or a retry returns the same action. A character nobody holds
    /// is recorded as finished with <see cref="AdminOutcome.NotOnline"/>; the caller polls the action for the outcome.
    /// </summary>
    private static async Task<IResult> KickAsync(HttpContext context, long characterId, KickRequest? request,
        CharacterQueries characters, AdminActionRepository actions, AdminSettings admin, CancellationToken ct)
    {
        if (request == null || !Guid.TryParse(request.RequestId, out var requestId) || requestId == Guid.Empty
            || (request.Note?.Length ?? 0) > admin.MaxDetailChars)
            return Refuse(AdminReasonCode.RequestInvalid);
        var adminName = AdminName(context);

        // A retry finds its row whatever happened to the character since.
        var existing = await actions.GetAsync(requestId, ct);
        if (existing != null)
        {
            var same = existing.RealmId == actions.RealmId && existing.Action == AdminActionKind.Kick
                       && existing.TargetCharacterId == characterId && existing.AdminName == adminName;
            return same ? Kicked(AdminSubmitStatus.AlreadyRecorded, existing) : Refuse(AdminReasonCode.RequestConflict);
        }

        var details = await characters.GetAsync(characterId, ct);
        if (details == null)
            return Refuse(AdminReasonCode.CharacterNotFound);
        var holder = details.Summary.Server;
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var (status, record) = await actions.SubmitAsync(
            new AdminActionRequest(requestId, adminName, AdminActionKind.Kick, characterId, details.Summary.AccountId, holder, note),
            holder == null ? AdminOutcome.NotOnline : null, ct);
        return status == AdminSubmitStatus.Conflict ? Refuse(AdminReasonCode.RequestConflict) : Kicked(status, record);
    }

    private static IResult Kicked(AdminSubmitStatus status, AdminActionRecord record) =>
        Results.Json(new { ok = true, submit = status, action = record });

    private static async Task<IResult> ListActionsAsync(long? characterId, AdminActionRepository actions, AdminSettings admin,
        CancellationToken ct)
    {
        var list = characterId is { } id
            ? await actions.ListForCharacterAsync(id, admin.ActionHistoryLimit, ct)
            : await actions.ListRecentAsync(admin.ActionHistoryLimit, ct);
        return Results.Json(new { ok = true, actions = list });
    }

    /// <summary>One action by its request id; commands no server finished in time are closed as Expired first.</summary>
    private static async Task<IResult> GetActionAsync(string requestId, AdminActionRepository actions, AdminSettings admin,
        CancellationToken ct)
    {
        if (!Guid.TryParse(requestId, out var id))
            return Refuse(AdminReasonCode.RequestInvalid);
        await actions.ExpireAsync(admin.CommandExpiryMs, ct);
        var record = await actions.GetAsync(id, ct);
        return record == null || record.RealmId != actions.RealmId
            ? Refuse(AdminReasonCode.ActionNotFound)
            : Results.Json(new { ok = true, action = record });
    }

    private static async Task<IResult> ReadLogsAsync(HttpContext context, string? file, string? contains, int? lines,
        AdminPanelSettings settings, AdminActionRepository actions, AdminSettings admin, CancellationToken ct)
    {
        if ((file?.Length ?? 0) + (contains?.Length ?? 0) > admin.MaxDetailChars)
            return Refuse(AdminReasonCode.RequestInvalid);
        var count = Math.Clamp(lines ?? admin.LogTailLines, 1, admin.LogTailLines);
        var result = LogTail.Read(settings.LogDirectory, file, contains, count, admin.LogTailMaxBytes);
        await RecordAsync(actions, AdminName(context), AdminActionKind.ViewLogs, detail: $"file={result.File} contains={contains}", ct: ct);
        return Results.Json(new { ok = true, files = result.Files, file = result.File, lines = result.Lines });
    }

    private static async Task<IResult> ReadTelemetryAsync(HttpContext context, long? characterId, long? accountId, string? kind,
        int? limit, AdminPanelSettings settings, AdminActionRepository actions, AdminSettings admin, CancellationToken ct)
    {
        if ((kind?.Length ?? 0) > admin.MaxDetailChars)
            return Refuse(AdminReasonCode.RequestInvalid);
        var max = Math.Clamp(limit ?? admin.TelemetryMaxRecords, 1, admin.TelemetryMaxRecords);
        var records = TelemetryTail.Read(settings.TelemetryDirectory, new TelemetryFilter(characterId, accountId, kind), max,
            admin.TelemetryTailMaxBytes);
        await RecordAsync(actions, AdminName(context), AdminActionKind.ViewTelemetry, characterId, accountId,
            string.IsNullOrEmpty(kind) ? null : $"kind={kind}", ct);
        return Results.Json(new { ok = true, records });
    }

    /// <summary>A read action, recorded as finished (spec 7.4: every admin action is logged with who did it).</summary>
    private static Task RecordAsync(AdminActionRepository actions, string adminName, string action, long? characterId = null,
        long? accountId = null, string? detail = null, CancellationToken ct = default) =>
        actions.SubmitAsync(new AdminActionRequest(Guid.NewGuid(), adminName, action, characterId, accountId, Detail: detail),
            AdminOutcome.Done, ct);

    private static string AdminName(HttpContext context) => context.User.FindFirstValue(ClaimTypes.Name) ?? "";

    private static ValueTask<object?> RequireSignedIn(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.User.Identity?.IsAuthenticated == true && AdminName(context.HttpContext).Length > 0
            ? next(context)
            : ValueTask.FromResult<object?>(Refuse(AdminReasonCode.NotSignedIn));

    private static ValueTask<object?> RequireRequestHeaderOnChanges(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method) || request.Headers[RequestHeader] == "1")
            return next(context);
        return ValueTask.FromResult<object?>(Refuse(AdminReasonCode.RequestInvalid));
    }
}
