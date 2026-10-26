using AuthApi.Identity;
using GameServer.Net.Tickets;
using GameServer.Persistence;

namespace AuthApi;

/// <summary>
/// Deployment settings from environment variables (never committed, R-2.5-05). Game tuning lives in config tables.
/// <list type="bullet">
/// <item><c>MMORPG_TICKET_SECRET</c>, <c>MMORPG_TICKET_KEY_ID</c>: the game server's ticket secret (base64, at least
/// 32 bytes) and its key ID, so the game server accepts the tickets this API issues.</item>
/// <item><c>MMORPG_AUTH_TOKEN_SECRET</c>: base64, at least 32 bytes; signs access tokens. Must differ from the ticket secret.</item>
/// <item><c>MMORPG_AUTH_TOKEN_KEY_ID</c> (optional, default 0): key ID written into access tokens.</item>
/// <item><c>MMORPG_GLOBAL_DB</c>, <c>MMORPG_REALM_DB</c>, <c>MMORPG_REALM_ID</c>: as for the game server (<see cref="PersistenceSettings"/>).
/// v0 serves one realm per process.</item>
/// <item><c>MMORPG_AUTH_IDENTITY</c> (optional): <c>steam</c> (default) or <c>dev</c> (only in builds with the dev login).</item>
/// <item><c>MMORPG_AUTH_URLS</c> (optional): listen URLs, default <c>http://127.0.0.1:5080</c>. Plain HTTP is allowed on
/// loopback only, because the session key is in the response (R-2.5-02). Exception for dev containers:
/// <c>MMORPG_DEV_CONTAINER_HTTP=true</c> in a dev-login build inside a container allows the wildcard address
/// (<see cref="GameServer.Host.HttpListenPolicy"/>; docker-compose.yml publishes the port on 127.0.0.1 only).</item>
/// <item><c>MMORPG_DATA_DIR</c> (optional): config tables directory, default <c>data/tables</c>.</item>
/// </list>
/// </summary>
public sealed record AuthApiSettings(
    byte[] TicketSecret,
    byte TicketKeyId,
    byte[] AccessTokenSecret,
    byte AccessTokenKeyId,
    PersistenceSettings Persistence,
    string IdentityMode,
    string DataDirectory)
{
    public const string TicketSecretVariable = "MMORPG_TICKET_SECRET";
    public const string TicketKeyIdVariable = "MMORPG_TICKET_KEY_ID";
    public const string AccessTokenSecretVariable = "MMORPG_AUTH_TOKEN_SECRET";
    public const string AccessTokenKeyIdVariable = "MMORPG_AUTH_TOKEN_KEY_ID";
    public const string IdentityVariable = "MMORPG_AUTH_IDENTITY";
    public const string UrlsVariable = "MMORPG_AUTH_URLS";
    public const string DataDirectoryVariable = "MMORPG_DATA_DIR";

    public const string DefaultUrls = "http://127.0.0.1:5080";

    public int RealmId => Persistence.RealmId;

    /// <summary>Reads and checks every setting; <paramref name="errors"/> lists everything wrong at once.</summary>
    public static AuthApiSettings? Read(Func<string, string?> environment, out List<string> errors)
    {
        errors = new List<string>();

        var ticketSecret = ReadSecret(environment, TicketSecretVariable, errors);
        var ticketKeyId = ReadByte(environment, TicketKeyIdVariable, required: true, errors);
        var tokenSecret = ReadSecret(environment, AccessTokenSecretVariable, errors);
        var tokenKeyId = ReadByte(environment, AccessTokenKeyIdVariable, required: false, errors);
        if (ticketSecret != null && tokenSecret != null && ticketSecret.AsSpan().SequenceEqual(tokenSecret))
            errors.Add($"{AccessTokenSecretVariable} must differ from {TicketSecretVariable}.");

        PersistenceSettings? persistence = null;
        try
        {
            persistence = PersistenceSettings.From(environment);
        }
        catch (InvalidOperationException e)
        {
            errors.Add(e.Message);
        }

        var identity = environment(IdentityVariable);
        identity = string.IsNullOrWhiteSpace(identity) ? IdentityProviders.Steam : identity.Trim().ToLowerInvariant();
        if (!IdentityProviders.IsAvailable(identity, out var identityError))
            errors.Add($"{IdentityVariable}: {identityError}");

        var dataDirectory = environment(DataDirectoryVariable);
        if (string.IsNullOrWhiteSpace(dataDirectory))
            dataDirectory = Path.Combine("data", "tables");

        if (errors.Count > 0)
            return null;
        return new AuthApiSettings(ticketSecret!, ticketKeyId, tokenSecret!, tokenKeyId, persistence!, identity, dataDirectory);
    }

    /// <summary>Never prints secrets or connection strings.</summary>
    public override string ToString() =>
        $"realm {RealmId}, identity {IdentityMode}, ticket key {TicketKeyId}, token key {AccessTokenKeyId}, data {Path.GetFullPath(DataDirectory)}";

    private static byte[]? ReadSecret(Func<string, string?> environment, string name, List<string> errors)
    {
        var text = environment(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add($"{name} is not set.");
            return null;
        }
        try
        {
            var secret = Convert.FromBase64String(text.Trim());
            if (secret.Length >= TicketKeyRing.MinKeySize)
                return secret;
            errors.Add($"{name} must decode to at least {TicketKeyRing.MinKeySize} bytes.");
        }
        catch (FormatException)
        {
            errors.Add($"{name} is not valid base64.");
        }
        return null;
    }

    private static byte ReadByte(Func<string, string?> environment, string name, bool required, List<string> errors)
    {
        var text = environment(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            if (required)
                errors.Add($"{name} is not set.");
            return 0;
        }
        if (!byte.TryParse(text.Trim(), out var value))
        {
            errors.Add($"{name} must be an integer from 0 to 255.");
            return 0;
        }
        return value;
    }
}
