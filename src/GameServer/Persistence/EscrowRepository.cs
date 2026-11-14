using System.Data.Common;
using Dapper;
using Npgsql;

namespace GameServer.Persistence;

/// <summary>
/// The mail side of an escrow release (spec 4.5 "M4 mail"; WP-M4-3 owns <c>MailService.Send</c>): writes one system mail
/// with the released items and gold <b>inside the caller's transaction</b>, so the escrow row and the mail commit together.
/// WP-M4-3's mail writer implements it once both are merged; tests use a double.
/// </summary>
public interface IEscrowMailSink
{
    Task WriteAsync(DbConnection connection, DbTransaction transaction, long toCharacterId, short mailKind,
        IReadOnlyList<EscrowItemRow> items, long gold, string opKey, DateTime now, CancellationToken ct);
}

/// <summary>Releases an escrow to a mail off the world thread (implemented by <see cref="EscrowRepository"/>; WP-M4-4).</summary>
public interface IEscrowMailReleaser
{
    /// <summary>
    /// Releases a held escrow to a mail in one transaction; with <paramref name="close"/>, the holder's row closes in it too (a
    /// listing cancelled or expired, WP-M4-7).
    /// </summary>
    Task<EscrowReleaseStatus> ReleaseToMailAsync(string escrowId, long toCharacterId, short mailKind, string releaseOpKey, long atUtcMs,
        EscrowHolderClose? close = null, CancellationToken ct = default);
}

/// <summary>A release to mail ended (<see cref="EscrowRepository.ReleaseToMailAsync"/>).</summary>
public enum EscrowReleaseStatus : byte
{
    Released,
    /// <summary>The escrow was released before by another operation (or does not exist): nothing written.</summary>
    Conflict,
}

/// <summary>An escrow release guard failed inside a character save: the escrow was no longer held (rule 5 backstop).</summary>
public sealed class EscrowConflictException(string escrowId) : Exception($"escrow {escrowId} is not held")
{
    public string EscrowId { get; } = escrowId;
}

/// <summary>
/// Escrow in the realm database (spec 4.5, 5.4; WP-M4-4): the <c>escrow</c> and <c>escrow_items</c> rows. Holds and releases
/// to a character are written inside that character's save transaction (<see cref="WriteAsync"/>); a release to mail is one
/// standalone transaction with the mail row (<see cref="ReleaseToMailAsync"/>). Only <c>EscrowService</c> produces either.
/// Called from persistence workers and tests, never from the tick thread (spec 1.6).
/// </summary>
public sealed class EscrowRepository(NpgsqlDataSource realmDb, IEscrowMailSink? mail = null, IEscrowHolderSink? holders = null)
    : IEscrowMailReleaser
{
    internal const string PrimaryKeyConstraint = "escrow_pkey";
    internal const string ReleaseKeyConstraint = "escrow_release_op_key_key";

    /// <summary>Every escrow still held (world start; one world in M4, TODO(M7): only the world's own holders when worlds shard).</summary>
    public async Task<IReadOnlyList<EscrowRecord>> LoadOpenAsync(CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<EscrowRow>(new CommandDefinition("""
            SELECT escrow_id AS EscrowId, owner_character_id AS OwnerCharacterId, holder_kind AS HolderKind, holder_id AS HolderId,
                gold AS Gold
            FROM escrow WHERE state = 1 ORDER BY escrow_id
            """, cancellationToken: ct))).ToList();
        var items = (await connection.QueryAsync<ItemRow>(new CommandDefinition("""
            SELECT i.escrow_id AS EscrowId, i.def_id AS DefId, i.rarity AS Rarity, i.stolen AS Stolen, i.count AS Count,
                i.instance_id AS InstanceId, i.non_tradable AS NonTradable, i.properties::text AS Properties
            FROM escrow_items i JOIN escrow e ON e.escrow_id = i.escrow_id WHERE e.state = 1 ORDER BY i.escrow_id, i.seq
            """, cancellationToken: ct))).ToLookup(i => i.EscrowId);
        return rows.Select(r => new EscrowRecord(r.EscrowId, r.OwnerCharacterId, (EscrowHolderKind)r.HolderKind, r.HolderId, r.Gold,
            items[r.EscrowId].Select(i => i.ToRow()).ToList())).ToList();
    }

    /// <summary>The state of one escrow (null when unknown; tests and admin tools).</summary>
    public async Task<EscrowState?> StateAsync(string escrowId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var state = await connection.QuerySingleOrDefaultAsync<short?>(new CommandDefinition(
            "SELECT state FROM escrow WHERE escrow_id = @escrowId", new { escrowId }, cancellationToken: ct));
        return state.HasValue ? (EscrowState)state.Value : null;
    }

    /// <summary>
    /// Writes <paramref name="batch"/> inside the character save's transaction: new escrows with their items, then releases
    /// to this character (each guarded: a row no longer held throws <see cref="EscrowConflictException"/>, and the caller
    /// rolls the whole save back).
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, EscrowBatch batch, Guid saveId,
        DateTime now, CancellationToken ct)
    {
        foreach (var hold in batch.Holds)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO escrow (escrow_id, owner_character_id, holder_kind, holder_id, gold, state, created_at, hold_save_id)
                VALUES (@EscrowId, @OwnerCharacterId, @HolderKind, @HolderId, @Gold, 1, @createdAt, @saveId)
                """, new
                {
                    hold.EscrowId, hold.OwnerCharacterId, HolderKind = (short)hold.HolderKind, hold.HolderId, hold.Gold,
                    createdAt = DbTime.ToUtc(hold.CreatedAtUtcMs), saveId,
                }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO escrow_items (escrow_id, seq, def_id, rarity, stolen, count, instance_id, non_tradable, properties)
                VALUES (@EscrowId, @Seq, @DefId, @Rarity, @Stolen, @Count, @InstanceId, @NonTradable, @PropertiesJson::jsonb)
                """, hold.Items.Select((i, seq) => new
                {
                    hold.EscrowId, Seq = (short)seq, i.DefId, Rarity = (short)i.Rarity, i.Stolen, i.Count, i.InstanceId,
                    i.NonTradable, i.PropertiesJson,
                }), transaction, cancellationToken: ct));
        }
        foreach (var release in batch.Releases)
        {
            int updated = await connection.ExecuteAsync(new CommandDefinition(ReleaseSql, new
            {
                release.EscrowId, release.ReleaseOpKey, ToKind = (short)release.ToKind, release.ToId, at = DbTime.ToUtc(release.AtUtcMs),
            }, transaction, cancellationToken: ct));
            if (updated != 1)
                throw new EscrowConflictException(release.EscrowId);
        }
    }

    /// <summary>
    /// Releases a held escrow to a mail in one transaction (spec 4.5: unsold listings, proceeds, contract returns): the row
    /// is marked released, then the mail is written through <see cref="IEscrowMailSink"/> in the same transaction. A retry of
    /// a release that committed (same <paramref name="releaseOpKey"/>) is <see cref="EscrowReleaseStatus.Released"/> again.
    /// </summary>
    public async Task<EscrowReleaseStatus> ReleaseToMailAsync(string escrowId, long toCharacterId, short mailKind, string releaseOpKey,
        long atUtcMs, EscrowHolderClose? close = null, CancellationToken ct = default)
    {
        if (mail == null)
            throw new InvalidOperationException("no mail sink: a release to mail needs MailService (WP-M4-3)");
        if (close != null && holders == null)
            throw new InvalidOperationException("no holder sink: closing a holder with its release needs the listings (WP-M4-7)");
        var at = DbTime.ToUtc(atUtcMs);
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            int updated = await connection.ExecuteAsync(new CommandDefinition(ReleaseSql, new
            {
                EscrowId = escrowId, ReleaseOpKey = releaseOpKey, ToKind = (short)EscrowTargetKind.Mail, ToId = toCharacterId, at,
            }, transaction, cancellationToken: ct));
            if (updated == 1)
            {
                var head = await connection.QuerySingleAsync<(long Gold, short HolderKind)>(new CommandDefinition(
                    "SELECT gold, holder_kind FROM escrow WHERE escrow_id = @escrowId", new { escrowId }, transaction, cancellationToken: ct));
                long gold = head.Gold;
                // WP-M4-7: the holder (a listing) closes in the same transaction, or nothing is released.
                if (close != null && !await holders!.CloseAsync(connection, transaction, escrowId, (EscrowHolderKind)head.HolderKind,
                        close, releaseOpKey, at, ct))
                {
                    await transaction.RollbackAsync(ct);
                    return EscrowReleaseStatus.Conflict;
                }
                var items = (await connection.QueryAsync<ItemRow>(new CommandDefinition("""
                    SELECT escrow_id AS EscrowId, def_id AS DefId, rarity AS Rarity, stolen AS Stolen, count AS Count,
                        instance_id AS InstanceId, non_tradable AS NonTradable, properties::text AS Properties
                    FROM escrow_items WHERE escrow_id = @escrowId ORDER BY seq
                    """, new { escrowId }, transaction, cancellationToken: ct))).Select(i => i.ToRow()).ToList();
                await mail.WriteAsync(connection, transaction, toCharacterId, mailKind, items, gold, releaseOpKey, at, ct);
                await transaction.CommitAsync(ct);
                return EscrowReleaseStatus.Released;
            }
            await transaction.RollbackAsync(ct);
        }
        var done = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT release_op_key FROM escrow WHERE escrow_id = @escrowId", new { escrowId }, cancellationToken: ct));
        return done == releaseOpKey ? EscrowReleaseStatus.Released : EscrowReleaseStatus.Conflict;
    }

    private const string ReleaseSql = """
        UPDATE escrow SET state = 2, released_at = @at, released_to_kind = @ToKind, released_to_id = @ToId,
            release_op_key = @ReleaseOpKey
        WHERE escrow_id = @EscrowId AND state = 1
        """;

    private sealed class EscrowRow
    {
        public string EscrowId { get; init; } = "";
        public long OwnerCharacterId { get; init; }
        public short HolderKind { get; init; }
        public string HolderId { get; init; } = "";
        public long Gold { get; init; }
    }

    private sealed class ItemRow
    {
        public string EscrowId { get; init; } = "";
        public string DefId { get; init; } = "";
        public short Rarity { get; init; }
        public bool Stolen { get; init; }
        public int Count { get; init; }
        public long InstanceId { get; init; }
        public bool NonTradable { get; init; }
        public string? Properties { get; init; }

        public EscrowItemRow ToRow() => new(DefId, Rarity, Stolen, Count, InstanceId, NonTradable, Properties);
    }
}
