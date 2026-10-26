using GameServer.Host;
using GameServer.Persistence;

namespace AdminPanel;

/// <summary>
/// Deployment settings from environment variables (never committed, R-2.5-05). Tuning lives in the <c>AdminSettings</c>
/// table.
/// <list type="bullet">
/// <item><c>MMORPG_GLOBAL_DB</c>, <c>MMORPG_REALM_DB</c>, <c>MMORPG_REALM_ID</c>: as for the game server
/// (<see cref="PersistenceSettings"/>). v0 serves one realm per process.</item>
/// <item><c>MMORPG_REDIS</c>, <c>MMORPG_REDIS_PREFIX</c> (optional, default <c>mmorpg:</c>): the game server's Redis and
/// key prefix, to read who holds a character (ownership locks).</item>
/// <item><c>MMORPG_ADMIN_IDENTITY</c> (optional): <c>none</c> (default: nobody can sign in) or <c>dev</c> (only in
/// builds with the dev login). TODO(open): production admin auth and roles (spec 7.4).</item>
/// <item><c>MMORPG_ADMIN_URLS</c> (optional): listen URLs, default <c>http://127.0.0.1:5090</c>. Plain HTTP on loopback
/// only (R-2.5-05; dev containers: <see cref="HttpListenPolicy"/>).</item>
/// <item><c>MMORPG_TELEMETRY_DIR</c> (optional, default <c>telemetry</c>): the game server's telemetry folder.</item>
/// <item><c>MMORPG_ADMIN_LOG_DIR</c> (optional, default <c>logs</c>): a folder of server <c>*.log</c> files.</item>
/// <item><c>MMORPG_DATA_DIR</c> (optional, default <c>data/tables</c>): config tables directory.</item>
/// </list>
/// </summary>
public sealed record AdminPanelSettings(
    PersistenceSettings Persistence,
    string Redis,
    string RedisKeyPrefix,
    string IdentityMode,
    string TelemetryDirectory,
    string LogDirectory,
    string DataDirectory)
{
    public const string RedisVariable = ServerSettings.RedisVariable;
    public const string RedisKeyPrefixVariable = ServerSettings.RedisKeyPrefixVariable;
    public const string IdentityVariable = "MMORPG_ADMIN_IDENTITY";
    public const string UrlsVariable = "MMORPG_ADMIN_URLS";
    public const string TelemetryDirectoryVariable = ServerSettings.TelemetryDirectoryVariable;
    public const string LogDirectoryVariable = "MMORPG_ADMIN_LOG_DIR";
    public const string DataDirectoryVariable = ServerSettings.DataDirectoryVariable;

    public const string DefaultUrls = "http://127.0.0.1:5090";
    public const string DefaultLogDirectory = "logs";

    public int RealmId => Persistence.RealmId;

    /// <summary>Reads and checks every setting; <paramref name="errors"/> lists everything wrong at once.</summary>
    public static AdminPanelSettings? Read(Func<string, string?> environment, out List<string> errors)
    {
        errors = new List<string>();

        PersistenceSettings? persistence = null;
        try
        {
            persistence = PersistenceSettings.From(environment);
        }
        catch (InvalidOperationException e)
        {
            errors.Add(e.Message);
        }

        var redis = environment(RedisVariable);
        if (string.IsNullOrWhiteSpace(redis))
            errors.Add($"{RedisVariable} is not set.");

        var identity = environment(IdentityVariable);
        identity = string.IsNullOrWhiteSpace(identity) ? AdminIdentities.None : identity.Trim().ToLowerInvariant();
        if (!AdminIdentities.IsAvailable(identity, out var identityError))
            errors.Add($"{IdentityVariable}: {identityError}");

        if (errors.Count > 0)
            return null;
        return new AdminPanelSettings(persistence!, redis!.Trim(),
            OrDefault(environment(RedisKeyPrefixVariable), ServerSettings.DefaultRedisKeyPrefix),
            identity,
            OrDefault(environment(TelemetryDirectoryVariable), ServerSettings.DefaultTelemetryDirectory),
            OrDefault(environment(LogDirectoryVariable), DefaultLogDirectory),
            OrDefault(environment(DataDirectoryVariable), Path.Combine("data", "tables")));
    }

    /// <summary>Never prints connection strings.</summary>
    public override string ToString() =>
        $"realm {RealmId}, identity {IdentityMode}, redis prefix '{RedisKeyPrefix}', telemetry {Path.GetFullPath(TelemetryDirectory)}, " +
        $"logs {Path.GetFullPath(LogDirectory)}, data {Path.GetFullPath(DataDirectory)}";

    private static string OrDefault(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
