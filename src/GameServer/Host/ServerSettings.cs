namespace GameServer.Host;

/// <summary>
/// Deployment settings from environment variables. They are not game tuning (that is config tables) and the ticket
/// secret and connection strings must never be committed (R-2.5-05).
/// <list type="bullet">
/// <item><c>MMORPG_TICKET_SECRET</c>: base64, at least 32 bytes. The Auth API signs tickets with the same secret.</item>
/// <item><c>MMORPG_TICKET_KEY_ID</c>: key id of that secret, 0-255 (tickets name the key they were signed with).</item>
/// <item><c>MMORPG_REALM_ID</c>: the realm this server runs; tickets for other realms are refused.</item>
/// <item><c>MMORPG_GAME_PORT</c>: UDP port to listen on.</item>
/// <item><c>MMORPG_REALM_DB</c>: Npgsql connection string of the realm database (characters, R-7.1-03).</item>
/// <item><c>MMORPG_REDIS</c>: StackExchange.Redis configuration string (ownership locks, ticket nonces; spec 2.1, 2.5).</item>
/// <item><c>MMORPG_REDIS_PREFIX</c> (optional): prefix of every Redis key, default <c>mmorpg:</c>.</item>
/// <item><c>MMORPG_DATA_DIR</c> (optional): config tables directory, default <c>data/tables</c>.</item>
/// <item><c>MMORPG_TELEMETRY_DIR</c> (optional): where telemetry files are written, default <c>telemetry</c>
/// (git-ignored at the repository root).</item>
/// </list>
/// Relative directories are resolved against the working directory.
/// </summary>
public sealed record ServerSettings(
    byte[] TicketSecret,
    byte TicketKeyId,
    int RealmId,
    int Port,
    string DataDirectory,
    string TelemetryDirectory = ServerSettings.DefaultTelemetryDirectory,
    string RealmDatabase = "",
    string Redis = "",
    string RedisKeyPrefix = ServerSettings.DefaultRedisKeyPrefix)
{
    public const string DefaultTelemetryDirectory = "telemetry";
    public const string DefaultRedisKeyPrefix = "mmorpg:";

    public const string TicketSecretVariable = "MMORPG_TICKET_SECRET";
    public const string TicketKeyIdVariable = "MMORPG_TICKET_KEY_ID";
    public const string RealmIdVariable = "MMORPG_REALM_ID";
    public const string PortVariable = "MMORPG_GAME_PORT";
    public const string RealmDatabaseVariable = "MMORPG_REALM_DB";
    public const string RedisVariable = "MMORPG_REDIS";
    public const string RedisKeyPrefixVariable = "MMORPG_REDIS_PREFIX";
    public const string DataDirectoryVariable = "MMORPG_DATA_DIR";
    public const string TelemetryDirectoryVariable = "MMORPG_TELEMETRY_DIR";

    /// <summary>Reads and checks every setting; <paramref name="errors"/> lists everything wrong at once.</summary>
    public static ServerSettings? Read(Func<string, string?> environment, out List<string> errors)
    {
        errors = new List<string>();

        byte[]? secret = null;
        var secretText = environment(TicketSecretVariable);
        if (string.IsNullOrWhiteSpace(secretText))
            errors.Add($"{TicketSecretVariable} is not set.");
        else
        {
            try
            {
                secret = Convert.FromBase64String(secretText.Trim());
                if (secret.Length < Net.Tickets.TicketKeyRing.MinKeySize)
                    errors.Add($"{TicketSecretVariable} must decode to at least {Net.Tickets.TicketKeyRing.MinKeySize} bytes.");
            }
            catch (FormatException)
            {
                errors.Add($"{TicketSecretVariable} is not valid base64.");
            }
        }

        var keyId = ReadInt(environment, TicketKeyIdVariable, 0, byte.MaxValue, errors);
        var realmId = ReadInt(environment, RealmIdVariable, 1, int.MaxValue, errors);
        var port = ReadInt(environment, PortVariable, 1, ushort.MaxValue, errors);
        var realmDatabase = ReadRequired(environment, RealmDatabaseVariable, errors);
        var redis = ReadRequired(environment, RedisVariable, errors);
        var redisPrefix = environment(RedisKeyPrefixVariable);
        if (string.IsNullOrWhiteSpace(redisPrefix))
            redisPrefix = DefaultRedisKeyPrefix;
        var dataDirectory = environment(DataDirectoryVariable);
        if (string.IsNullOrWhiteSpace(dataDirectory))
            dataDirectory = Path.Combine("data", "tables");
        var telemetryDirectory = environment(TelemetryDirectoryVariable);
        if (string.IsNullOrWhiteSpace(telemetryDirectory))
            telemetryDirectory = DefaultTelemetryDirectory;

        if (errors.Count > 0)
            return null;
        return new ServerSettings(secret!, (byte)keyId, realmId, port, dataDirectory, telemetryDirectory,
            realmDatabase, redis, redisPrefix.Trim());
    }

    /// <summary>Never prints the secret or the connection strings (they can hold passwords).</summary>
    public override string ToString() =>
        $"realm {RealmId}, port {Port}, ticket key {TicketKeyId}, data {Path.GetFullPath(DataDirectory)}, " +
        $"telemetry {Path.GetFullPath(TelemetryDirectory)}, redis prefix '{RedisKeyPrefix}'";

    private static string ReadRequired(Func<string, string?> environment, string name, List<string> errors)
    {
        var text = environment(name);
        if (!string.IsNullOrWhiteSpace(text))
            return text.Trim();
        errors.Add($"{name} is not set.");
        return "";
    }

    private static int ReadInt(Func<string, string?> environment, string name, int min, int max, List<string> errors)
    {
        var text = environment(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add($"{name} is not set.");
            return 0;
        }
        if (!int.TryParse(text.Trim(), out var value) || value < min || value > max)
        {
            errors.Add($"{name} must be an integer from {min} to {max}.");
            return 0;
        }
        return value;
    }
}
