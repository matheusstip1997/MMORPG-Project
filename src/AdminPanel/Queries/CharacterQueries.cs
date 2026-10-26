using System.Globalization;
using Dapper;
using GameServer.Sessions;
using Npgsql;
using StackExchange.Redis;

namespace AdminPanel.Queries;

/// <summary>A character as the panel shows it. <see cref="Server"/> is the game server holding it, null when none does.</summary>
public sealed record CharacterSummary(
    long CharacterId,
    string Name,
    long AccountId,
    long? SteamId,
    int Slot,
    string ClassId,
    int Level,
    bool Deleted,
    bool Loaded,
    string? Server);

/// <summary>One character's details: the summary plus its saved position and meters (the last save, not live state).</summary>
public sealed record CharacterDetails(
    CharacterSummary Summary,
    string WorldId,
    int PosXCm,
    int PosYCm,
    int Hp,
    int Mp,
    int Stamina,
    long Version,
    long CreatedAtUtcMs,
    long UpdatedAtUtcMs);

/// <summary>What the account box of a search names: a SteamID (<c>dev:7</c> is the dev login's SteamID 7, or digits) or <c>id:&lt;accountId&gt;</c>.</summary>
public readonly record struct AccountQuery(long? SteamId, long? AccountId)
{
    public static bool TryParse(string? text, out AccountQuery query)
    {
        query = default;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;
        if (text.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryPositive(text[3..], out var accountId))
                return false;
            query = new AccountQuery(null, accountId);
            return true;
        }
        var steam = text.StartsWith("dev:", StringComparison.OrdinalIgnoreCase) ? text[4..] : text;
        if (!TryPositive(steam, out var steamId))
            return false;
        query = new AccountQuery(steamId, null);
        return true;
    }

    private static bool TryPositive(string text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
}

/// <summary>
/// Read-only character queries for the panel: characters from the realm database, SteamIDs from the global database
/// (no join across them, R-7.1-03), and who holds each character from the Redis ownership locks (R-2.1-06). Never world
/// state: the panel runs in its own process.
/// </summary>
public sealed class CharacterQueries(NpgsqlDataSource globalDb, NpgsqlDataSource realmDb, IConnectionMultiplexer redis,
    string redisKeyPrefix, int realmId)
{
    private const string Columns = """
        character_id AS CharacterId, name AS Name, account_id AS AccountId, slot AS Slot, class_id AS ClassId, level AS Level,
        deleted_at IS NOT NULL AS Deleted, world_id AS WorldId, pos_x_cm AS PosXCm, pos_y_cm AS PosYCm, hp AS Hp, mp AS Mp,
        stamina AS Stamina, version AS Version, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    /// <summary>
    /// Characters whose name starts with <paramref name="namePrefix"/> (ignoring case) and/or that belong to the account
    /// <paramref name="account"/> names, by name, at most <paramref name="limit"/>.
    /// </summary>
    public async Task<IReadOnlyList<CharacterSummary>> SearchAsync(string? namePrefix, AccountQuery? account, int limit,
        CancellationToken ct = default)
    {
        long[]? accountIds = null;
        if (account is { } query)
        {
            accountIds = await FindAccountIdsAsync(query, ct);
            if (accountIds.Length == 0)
                return [];
        }

        var sql = $"""
            SELECT {Columns} FROM characters
            WHERE realm_id = @realmId
              AND (@pattern::text IS NULL OR lower(name) LIKE @pattern ESCAPE '\')
              AND (@accountIds::bigint[] IS NULL OR account_id = ANY(@accountIds))
            ORDER BY lower(name), character_id
            LIMIT @limit
            """;
        var pattern = string.IsNullOrEmpty(namePrefix) ? null : EscapeLike(namePrefix.ToLowerInvariant()) + "%";
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(sql,
            new { realmId, pattern, accountIds, limit }, cancellationToken: ct))).ToList();
        return await SummariesAsync(rows, ct);
    }

    public async Task<CharacterDetails?> GetAsync(long characterId, CancellationToken ct = default)
    {
        var sql = $"SELECT {Columns} FROM characters WHERE realm_id = @realmId AND character_id = @characterId";
        Row? row;
        await using (var connection = await realmDb.OpenConnectionAsync(ct))
            row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, new { realmId, characterId }, cancellationToken: ct));
        if (row == null)
            return null;
        var summary = (await SummariesAsync([row], ct))[0];
        return new CharacterDetails(summary, row.WorldId, row.PosXCm, row.PosYCm, row.Hp, row.Mp, row.Stamina, row.Version,
            ToUtcMs(row.CreatedAt), ToUtcMs(row.UpdatedAt));
    }

    /// <summary>The game server holding the character (from its ownership lock token), or null when nobody holds it.</summary>
    public async Task<string?> HolderAsync(long characterId)
    {
        var token = await redis.GetDatabase().StringGetAsync(RedisOwnershipLocks.KeyFor(redisKeyPrefix, realmId, characterId));
        return token.HasValue ? OwnershipLockToken.ServerOf(token.ToString()) : null;
    }

    private async Task<long[]> FindAccountIdsAsync(AccountQuery query, CancellationToken ct)
    {
        const string sql = """
            SELECT account_id FROM accounts
            WHERE (@steamId::bigint IS NOT NULL AND steam_id = @steamId) OR (@accountId::bigint IS NOT NULL AND account_id = @accountId)
            """;
        await using var connection = await globalDb.OpenConnectionAsync(ct);
        var ids = await connection.QueryAsync<long>(new CommandDefinition(sql,
            new { steamId = query.SteamId, accountId = query.AccountId }, cancellationToken: ct));
        return ids.ToArray();
    }

    private async Task<IReadOnlyList<CharacterSummary>> SummariesAsync(List<Row> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return [];

        var accountIds = rows.Select(r => r.AccountId).Distinct().ToArray();
        Dictionary<long, long> steamIds;
        await using (var connection = await globalDb.OpenConnectionAsync(ct))
        {
            var pairs = await connection.QueryAsync<(long AccountId, long SteamId)>(new CommandDefinition(
                "SELECT account_id, steam_id FROM accounts WHERE account_id = ANY(@accountIds)", new { accountIds }, cancellationToken: ct));
            steamIds = pairs.ToDictionary(p => p.AccountId, p => p.SteamId);
        }

        var keys = rows.Select(r => (RedisKey)RedisOwnershipLocks.KeyFor(redisKeyPrefix, realmId, r.CharacterId)).ToArray();
        var tokens = await redis.GetDatabase().StringGetAsync(keys);

        var result = new List<CharacterSummary>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var server = tokens[i].HasValue ? OwnershipLockToken.ServerOf(tokens[i].ToString()) : null;
            result.Add(new CharacterSummary(row.CharacterId, row.Name, row.AccountId,
                steamIds.TryGetValue(row.AccountId, out var steamId) ? steamId : null, row.Slot, row.ClassId, row.Level,
                row.Deleted, server != null, server));
        }
        return result;
    }

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static long ToUtcMs(DateTime value) => (long)(value.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;

    private sealed class Row
    {
        public long CharacterId { get; init; }
        public string Name { get; init; } = "";
        public long AccountId { get; init; }
        public int Slot { get; init; }
        public string ClassId { get; init; } = "";
        public int Level { get; init; }
        public bool Deleted { get; init; }
        public string WorldId { get; init; } = "";
        public int PosXCm { get; init; }
        public int PosYCm { get; init; }
        public int Hp { get; init; }
        public int Mp { get; init; }
        public int Stamina { get; init; }
        public long Version { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
