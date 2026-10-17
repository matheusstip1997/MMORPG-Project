using Dapper;
using Npgsql;
using Shared.Util;

namespace GameServer.Persistence;

public sealed record Account(long AccountId, long SteamId, long? OwnerSteamId, long CreatedAtUtcMs);

/// <summary>Accounts in the global database (R-2.1-01, R-2.1-03). Called by the Auth API and the dev login.</summary>
public sealed class AccountRepository(NpgsqlDataSource globalDb, IClock clock)
{
    /// <summary>Returns the account for <paramref name="steamId"/>, creating it on first login. Safe to call twice at once.</summary>
    public async Task<Account> GetOrCreateBySteamIdAsync(long steamId, long? ownerSteamId, CancellationToken ct = default)
    {
        // "DO UPDATE" with an unchanged value makes RETURNING yield the existing row as well.
        const string sql = """
            INSERT INTO accounts (steam_id, owner_steam_id, created_at)
            VALUES (@steamId, @ownerSteamId, @now)
            ON CONFLICT (steam_id) DO UPDATE SET steam_id = EXCLUDED.steam_id
            RETURNING account_id AS AccountId, steam_id AS SteamId, owner_steam_id AS OwnerSteamId, created_at AS CreatedAt
            """;

        await using var connection = await globalDb.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleAsync<AccountRow>(new CommandDefinition(sql,
            new { steamId, ownerSteamId, now = DbTime.ToUtc(clock.UtcNowMs) }, cancellationToken: ct));
        return row.ToAccount();
    }

    public async Task<Account?> FindAsync(long accountId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT account_id AS AccountId, steam_id AS SteamId, owner_steam_id AS OwnerSteamId, created_at AS CreatedAt
            FROM accounts WHERE account_id = @accountId
            """;

        await using var connection = await globalDb.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition(sql, new { accountId }, cancellationToken: ct));
        return row?.ToAccount();
    }

    private sealed class AccountRow
    {
        public long AccountId { get; init; }
        public long SteamId { get; init; }
        public long? OwnerSteamId { get; init; }
        public DateTime CreatedAt { get; init; }

        public Account ToAccount() => new(AccountId, SteamId, OwnerSteamId, DbTime.ToUtcMs(CreatedAt));
    }
}
