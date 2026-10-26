namespace GameServer.Admin;

/// <summary>
/// What an admin did (spec 7.4: every admin action is logged with who did it). Stored as text in
/// <c>admin_actions.action</c> (realm migration V0003); the names below are the stored values.
/// </summary>
public static class AdminActionKind
{
    public const string SignIn = "sign_in";
    public const string Search = "search";
    public const string ViewCharacter = "view_character";
    public const string ViewLogs = "view_logs";
    public const string ViewTelemetry = "view_telemetry";

    /// <summary>The only state-changing action of panel v0: executed by the game server that holds the character.</summary>
    public const string Kick = "kick";
}

/// <summary>Where an action is: <c>pending</c> until a game server claims it, <c>claimed</c> while it runs, <c>done</c> with an outcome.</summary>
public static class AdminActionState
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Done = "done";
}

/// <summary>The outcome stored with a finished action (<c>admin_actions.outcome</c>, the enum name).</summary>
public enum AdminOutcome : byte
{
    /// <summary>A read action (search, view), recorded when it was served.</summary>
    Done = 1,

    /// <summary>The character's client was disconnected with <c>ConnectRejectReason.Kicked</c>.</summary>
    Kicked = 2,

    /// <summary>No game server holds the character (not loaded anywhere, or no longer in the world that claimed the kick).</summary>
    NotOnline = 3,

    /// <summary>The character is in the world but has no client: it is lingering after a disconnect (R-2.1-08).</summary>
    NotConnected = 4,

    /// <summary>No game server finished the action within <c>AdminSettings.CommandExpiryMs</c> (crashed or stopped server).</summary>
    Expired = 5,
}

/// <summary>
/// Why the admin panel refused a request (reason codes for refused actions, CLAUDE.md). The panel shows a text for each.
/// </summary>
public enum AdminReasonCode : byte
{
    None = 0,

    /// <summary>No admin session, or it expired.</summary>
    NotSignedIn = 1,

    /// <summary>A malformed request: missing or bad field, bad request id, text too long.</summary>
    RequestInvalid = 2,

    /// <summary>No character with that id in this realm.</summary>
    CharacterNotFound = 3,

    /// <summary>The same request id was already used for another action or target (idempotency key reuse).</summary>
    RequestConflict = 4,

    /// <summary>The database or Redis failed; retry later.</summary>
    ServerUnavailable = 5,

    /// <summary>This build or configuration has no admin sign-in (production admin auth is not decided: TODO(open)).</summary>
    SignInUnavailable = 6,

    /// <summary>No admin action with that request id.</summary>
    ActionNotFound = 7,
}

/// <summary>One <c>admin_actions</c> row (who, when, what, target, and for commands the state and outcome).</summary>
public sealed record AdminActionRecord(
    long ActionId,
    Guid RequestId,
    int RealmId,
    string AdminName,
    string Action,
    long? TargetCharacterId,
    long? TargetAccountId,
    string? TargetServer,
    string? Detail,
    string State,
    string? Outcome,
    long RequestedAtUtcMs,
    long? ClaimedAtUtcMs,
    string? ClaimedBy,
    long? CompletedAtUtcMs);

/// <summary>A new action to record. <see cref="TargetServer"/> names the game server that must execute a command.</summary>
public sealed record AdminActionRequest(
    Guid RequestId,
    string AdminName,
    string Action,
    long? TargetCharacterId = null,
    long? TargetAccountId = null,
    string? TargetServer = null,
    string? Detail = null);

/// <summary>A claimed command for the world to execute.</summary>
public readonly record struct AdminCommand(long ActionId, string Action, long CharacterId);

/// <summary>The result of <see cref="AdminActionRepository.SubmitAsync"/>.</summary>
public enum AdminSubmitStatus : byte
{
    /// <summary>The row was written by this call.</summary>
    Created = 1,

    /// <summary>A row with the same request id and the same action and target exists: a double submit or a retry.</summary>
    AlreadyRecorded = 2,

    /// <summary>The request id names a different action or target.</summary>
    Conflict = 3,
}
