using System.Data.Common;
using System.Text;
using Dapper;
using Npgsql;

namespace GameServer.Persistence;

/// <summary>
/// Listings in the realm database (spec 4.5, 7.1, 8.2; M4 decision 6; WP-M4-7): the <c>listings</c> and <c>listing_sales</c>
/// rows. Creates and purchases are written inside the character save that carries them (<see cref="WriteAsync"/>, the seller's
/// with the escrow hold and the fee, the buyer's with the release and the proceeds mail); a cancel or an expiry closes the row
/// in the escrow's release-to-mail transaction (<see cref="CloseAsync"/>, as the <see cref="IEscrowHolderSink"/>). Only
/// <c>Market/ListingService</c> produces them. The search (<see cref="SearchAsync"/>) and the load at world start run off the
/// tick thread (spec 1.6), on the indexes of migration V0020.
/// </summary>
public sealed class ListingRepository(NpgsqlDataSource realmDb) : IEscrowHolderSink, IListingSearchStore
{
    internal const string PrimaryKeyConstraint = "listings_pkey";
    internal const string CreateKeyConstraint = "listings_create_op_key";
    internal const string CloseKeyConstraint = "listings_close_op_key";
    internal const string SaleKeyConstraint = "listing_sales_pkey";

    /// <summary>Every active listing (world start; one world in M4) and the highest listing id ever written (the next id follows it).</summary>
    public async Task<(IReadOnlyList<ListingRow> Active, long MaxListingId)> LoadActiveAsync(CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<DbRow>(new CommandDefinition(Select + " WHERE state = 1 ORDER BY listing_id",
            cancellationToken: ct))).Select(r => r.ToRow()).ToList();
        long max = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(MAX(listing_id), 0) FROM listings", cancellationToken: ct));
        return (rows, max);
    }

    /// <summary>One listing whatever its state (tests and admin tools); null when unknown.</summary>
    public async Task<(ListingRow Row, ListingState State)?> FindAsync(long listingId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<DbRow>(new CommandDefinition(Select + " WHERE listing_id = @listingId",
            new { listingId }, cancellationToken: ct));
        return row == null ? null : (row.ToRow(), (ListingState)row.State);
    }

    /// <summary>
    /// One page of the search (see <see cref="ListingQuery"/>): the filters given lead to the matching partial index (the item ids
    /// or the category, else the price); keyset pagination on (unit_price, listing_id). Returns at most <c>Limit</c> rows.
    /// </summary>
    public async Task<IReadOnlyList<ListingSearchRow>> SearchAsync(ListingQuery query, CancellationToken ct = default)
    {
        var (sql, parameters) = BuildSearch(query);
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<SearchDbRow>(new CommandDefinition(sql, parameters, cancellationToken: ct)))
            .Select(r => new ListingSearchRow(r.ListingId, r.UnitPrice, r.CountLeft, DbTime.ToUtcMs(r.ExpiresAt), r.DefId, r.Rarity,
                r.Stolen, r.InstanceId, r.NonTradable, r.Properties))
            .ToList();
    }

    /// <summary>The search's SQL and parameters (public for the query plan test).</summary>
    public static (string Sql, DynamicParameters Parameters) BuildSearch(ListingQuery query)
    {
        var sql = new StringBuilder("""
            SELECT l.listing_id AS ListingId, l.unit_price AS UnitPrice, l.count_left AS CountLeft, l.expires_at AS ExpiresAt,
                   l.def_id AS DefId, l.rarity AS Rarity, i.stolen AS Stolen, i.instance_id AS InstanceId,
                   i.non_tradable AS NonTradable, i.properties::text AS Properties
            FROM listings l JOIN escrow_items i ON i.escrow_id = l.escrow_id AND i.seq = 0
            WHERE l.state = 1 AND l.kind = 1 AND l.expires_at > @now
            """);
        var p = new DynamicParameters();
        p.Add("now", DbTime.ToUtc(query.NowUtcMs));
        if (query.ItemNetIds.Count > 0)
        {
            sql.Append(" AND l.item_net_id = ANY(@items)");
            p.Add("items", query.ItemNetIds.ToArray());
        }
        if (query.Category is { } category)
        {
            sql.Append(" AND l.category = @category");
            p.Add("category", category);
        }
        if (query.RarityMin > 0 || query.RarityMax > 0)
        {
            sql.Append(" AND l.rarity BETWEEN @rarityMin AND @rarityMax");
            p.Add("rarityMin", query.RarityMin);
            p.Add("rarityMax", query.RarityMax > 0 ? query.RarityMax : short.MaxValue);
        }
        if (query.LevelMin > 0 || query.LevelMax > 0)
        {
            sql.Append(" AND l.min_level BETWEEN @levelMin AND @levelMax");
            p.Add("levelMin", query.LevelMin);
            p.Add("levelMax", query.LevelMax > 0 ? query.LevelMax : int.MaxValue);
        }
        if (query.JobNetId > 0)
        {
            sql.Append(" AND l.job_net_id = @job");
            p.Add("job", query.JobNetId);
        }
        if (query.ArmorTier > 0)
        {
            sql.Append(" AND l.armor_tier = @tier");
            p.Add("tier", query.ArmorTier);
        }
        if (query.AfterUnitPrice > 0 || query.AfterListingId > 0)
        {
            sql.Append(" AND (l.unit_price, l.listing_id) > (@afterPrice, @afterId)");
            p.Add("afterPrice", query.AfterUnitPrice);
            p.Add("afterId", query.AfterListingId);
        }
        sql.Append(" ORDER BY l.unit_price, l.listing_id LIMIT @limit");
        p.Add("limit", query.Limit);
        return (sql.ToString(), p);
    }

    /// <summary>
    /// Writes <paramref name="batch"/> inside the character save's transaction: new listings, then purchases (each guarded: a
    /// listing that changed under it throws <see cref="ListingConflictException"/> and the caller rolls the whole save back).
    /// A repeated operation throws a unique violation on one of the constraints above (the save is a duplicate).
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, ListingBatch batch, Guid saveId,
        CancellationToken ct)
    {
        foreach (var row in batch.Creates)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO listings (listing_id, create_op_key, kind, stall_id, seller_id, escrow_id, def_id, item_net_id, category,
                    rarity, min_level, job_net_id, armor_tier, unit_price, count_initial, count_left, fee_paid, created_at, expires_at,
                    state, create_save_id)
                VALUES (@ListingId, @CreateOpKey, @Kind, @StallId, @SellerId, @EscrowId, @DefId, @ItemNetId, @Category, @Rarity,
                    @MinLevel, @JobNetId, @ArmorTier, @UnitPrice, @CountInitial, @CountLeft, @FeePaid, @createdAt, @expiresAt, 1, @saveId)
                """, new
            {
                row.ListingId, row.CreateOpKey, Kind = (short)row.Kind, row.StallId, row.SellerId, row.EscrowId, row.DefId, row.ItemNetId,
                row.Category, row.Rarity, row.MinLevel, row.JobNetId, row.ArmorTier, row.UnitPrice, row.CountInitial, row.CountLeft,
                row.FeePaid, createdAt = DbTime.ToUtc(row.CreatedUtcMs), expiresAt = DbTime.ToUtc(row.ExpiresUtcMs), saveId,
            }, transaction, cancellationToken: ct));
        }
        foreach (var sale in batch.Sales)
        {
            var at = DbTime.ToUtc(sale.AtUtcMs);
            int updated = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE listings SET count_left = @NewCountLeft, escrow_id = @NewEscrowId,
                    state = CASE WHEN @NewCountLeft = 0 THEN 2 ELSE 1 END,
                    closed_at = CASE WHEN @NewCountLeft = 0 THEN @at ELSE NULL END
                WHERE listing_id = @ListingId AND state = 1 AND count_left = @ExpectedCountLeft AND escrow_id = @ExpectedEscrowId
                """, new { sale.NewCountLeft, sale.NewEscrowId, at, sale.ListingId, sale.ExpectedCountLeft, sale.ExpectedEscrowId },
                transaction, cancellationToken: ct));
            if (updated != 1)
                throw new ListingConflictException(sale.ListingId);
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO listing_sales (op_key, listing_id, buyer_id, count, unit_price, price, tax, to_mail, save_id, sold_at)
                VALUES (@OpKey, @ListingId, @BuyerId, @Count, @UnitPrice, @Price, @Tax, @ToMail, @saveId, @at)
                """, new { sale.OpKey, sale.ListingId, sale.BuyerId, sale.Count, sale.UnitPrice, sale.Price, sale.Tax, sale.ToMail, saveId, at },
                transaction, cancellationToken: ct));
        }
    }

    /// <summary>
    /// <see cref="IEscrowHolderSink"/>: a cancel or an expiry closes the active listing held by <paramref name="escrowId"/> in the
    /// escrow's release-to-mail transaction. False (nothing written) when no active listing holds it.
    /// </summary>
    public async Task<bool> CloseAsync(DbConnection connection, DbTransaction transaction, string escrowId, EscrowHolderKind holderKind,
        EscrowHolderClose close, string opKey, DateTime at, CancellationToken ct)
    {
        if (holderKind is not (EscrowHolderKind.Listing or EscrowHolderKind.Stall))
            throw new InvalidOperationException($"escrow {escrowId} is held by a {holderKind}, not a listing");
        if (close.State is not ((short)ListingState.Cancelled or (short)ListingState.Expired))
            throw new ArgumentOutOfRangeException(nameof(close), $"a listing closes as cancelled or expired, not {close.State}");
        int updated = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE listings SET state = @State, closed_at = @at, close_op_key = @opKey
            WHERE escrow_id = @escrowId AND state = 1
            """, new { close.State, at, opKey, escrowId }, transaction, cancellationToken: ct));
        return updated == 1;
    }

    private const string Select = """
        SELECT listing_id AS ListingId, create_op_key AS CreateOpKey, kind AS Kind, stall_id AS StallId, seller_id AS SellerId,
               escrow_id AS EscrowId, def_id AS DefId, item_net_id AS ItemNetId, category AS Category, rarity AS Rarity,
               min_level AS MinLevel, job_net_id AS JobNetId, armor_tier AS ArmorTier, unit_price AS UnitPrice,
               count_initial AS CountInitial, count_left AS CountLeft, fee_paid AS FeePaid, created_at AS CreatedAt,
               expires_at AS ExpiresAt, state AS State
        FROM listings
        """;

    private sealed class DbRow
    {
        public long ListingId { get; init; }
        public string CreateOpKey { get; init; } = "";
        public short Kind { get; init; }
        public string? StallId { get; init; }
        public long SellerId { get; init; }
        public string EscrowId { get; init; } = "";
        public string DefId { get; init; } = "";
        public int ItemNetId { get; init; }
        public short Category { get; init; }
        public short Rarity { get; init; }
        public int MinLevel { get; init; }
        public short JobNetId { get; init; }
        public short ArmorTier { get; init; }
        public long UnitPrice { get; init; }
        public int CountInitial { get; init; }
        public int CountLeft { get; init; }
        public long FeePaid { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime ExpiresAt { get; init; }
        public short State { get; init; }

        public ListingRow ToRow() => new(ListingId, CreateOpKey, (ListingKind)Kind, StallId, SellerId, EscrowId, DefId, ItemNetId, Category,
            Rarity, MinLevel, JobNetId, ArmorTier, UnitPrice, CountInitial, CountLeft, FeePaid, DbTime.ToUtcMs(CreatedAt),
            DbTime.ToUtcMs(ExpiresAt));
    }

    private sealed class SearchDbRow
    {
        public long ListingId { get; init; }
        public long UnitPrice { get; init; }
        public int CountLeft { get; init; }
        public DateTime ExpiresAt { get; init; }
        public string DefId { get; init; } = "";
        public short Rarity { get; init; }
        public bool Stolen { get; init; }
        public long InstanceId { get; init; }
        public bool NonTradable { get; init; }
        public string? Properties { get; init; }
    }
}
