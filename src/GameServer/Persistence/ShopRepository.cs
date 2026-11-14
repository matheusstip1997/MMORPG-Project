using System.Data.Common;
using Dapper;
using Npgsql;

namespace GameServer.Persistence;

/// <summary>
/// Stalls in the realm database (spec 4.5, 7.1, 8.2; M4 decision 7; WP-M4-8): the <c>shops</c> rows of migration V0021. An open
/// is written inside the owner's character save with its first listing (<see cref="WriteAsync"/>); a close on its own transaction
/// off the tick thread (<see cref="CloseAsync"/>), whether or not the owner is online. Only <c>Market/StallService</c> produces
/// them; the load at world start runs before the world thread.
/// </summary>
public sealed class ShopRepository(NpgsqlDataSource realmDb) : IShopStore
{
    internal const string PrimaryKeyConstraint = "shops_pkey";

    /// <summary>Every open stall (world start; one world in M4), oldest first.</summary>
    public async Task<IReadOnlyList<ShopRow>> LoadOpenAsync(CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<DbRow>(new CommandDefinition(Select + " WHERE state = 1 ORDER BY opened_at, shop_id",
            cancellationToken: ct))).Select(r => r.ToRow()).ToList();
    }

    /// <summary>One stall whatever its state (tests and admin tools): the row, its state and close reason; null when unknown.</summary>
    public async Task<(ShopRow Row, short State, ShopCloseReason Reason)?> FindAsync(string shopId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<DbRow>(new CommandDefinition(Select + " WHERE shop_id = @shopId",
            new { shopId }, cancellationToken: ct));
        return row == null ? null : (row.ToRow(), row.State, (ShopCloseReason)(row.CloseReason ?? 0));
    }

    public async Task<bool> CloseAsync(string shopId, ShopCloseReason reason, long atUtcMs, CancellationToken ct)
    {
        if (reason == ShopCloseReason.None)
            throw new ArgumentOutOfRangeException(nameof(reason), "a stall closes for a reason");
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        int updated = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE shops SET state = 2, closed_at = GREATEST(@at, opened_at), close_reason = @reason
            WHERE shop_id = @shopId AND state = 1
            """, new { at = DbTime.ToUtc(atUtcMs), reason = (short)reason, shopId }, cancellationToken: ct));
        return updated == 1;
    }

    /// <summary>
    /// Writes <paramref name="batch"/> inside the character save's transaction (the stall's first listing rides the same save). A
    /// repeated open throws a unique violation on <see cref="PrimaryKeyConstraint"/> (the save is a duplicate).
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, ShopBatch batch, Guid saveId,
        CancellationToken ct)
    {
        foreach (var row in batch.Opens)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO shops (shop_id, owner_character_id, place_marker_id, owner_name, opened_at, closes_at, state, open_save_id)
                VALUES (@ShopId, @OwnerId, @PlaceMarkerId, @OwnerName, @openedAt, @closesAt, 1, @saveId)
                """, new
            {
                row.ShopId, row.OwnerId, row.PlaceMarkerId, row.OwnerName, openedAt = DbTime.ToUtc(row.OpenedUtcMs),
                closesAt = DbTime.ToUtc(row.ClosesUtcMs), saveId,
            }, transaction, cancellationToken: ct));
        }
    }

    private const string Select = """
        SELECT shop_id AS ShopId, owner_character_id AS OwnerId, place_marker_id AS PlaceMarkerId, owner_name AS OwnerName,
               opened_at AS OpenedAt, closes_at AS ClosesAt, state AS State, close_reason AS CloseReason
        FROM shops
        """;

    private sealed class DbRow
    {
        public string ShopId { get; init; } = "";
        public long OwnerId { get; init; }
        public string PlaceMarkerId { get; init; } = "";
        public string OwnerName { get; init; } = "";
        public DateTime OpenedAt { get; init; }
        public DateTime ClosesAt { get; init; }
        public short State { get; init; }
        public short? CloseReason { get; init; }

        public ShopRow ToRow() => new(ShopId, OwnerId, PlaceMarkerId, OwnerName, DbTime.ToUtcMs(OpenedAt), DbTime.ToUtcMs(ClosesAt));
    }
}
