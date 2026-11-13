using System.Data.Common;
using System.Text;
using Dapper;
using Npgsql;

namespace GameServer.Persistence;

/// <summary>
/// Death loot bags in the realm database (spec 4.3 R-4.3-03, 7.1 schema v21; WP-M4-9): <c>loot_bags</c>,
/// <c>loot_bag_items</c> and <c>loot_bag_ops</c>. A bag is created and taken from <b>inside a character's save transaction</b>
/// (<see cref="WriteAsync"/>, after that save's inventory and mail batches, so the units that left or entered a bag commit with
/// the bag rows: both or neither); the expiry is one standalone transaction (<see cref="ExpireAsync"/>). Only the world's
/// death loot service produces either. Called from persistence workers, the expiry task and tests, never from the tick thread.
/// </summary>
public sealed class LootBagRepository(NpgsqlDataSource realmDb) : ILootBagExpirer
{
    internal const string BagKeyConstraint = "loot_bags_pkey";
    internal const string OpKeyConstraint = "loot_bag_ops_pkey";

    /// <summary>Every open bag with its rows, oldest first (world start; one world in M4, TODO(M7): only this world's bags).</summary>
    public async Task<IReadOnlyList<LootBagRecord>> LoadOpenAsync(CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var bags = (await connection.QueryAsync<BagRow>(new CommandDefinition("""
            SELECT bag_id AS BagId, owner_character_id AS OwnerCharacterId, region_id AS RegionId, x_cm AS XCm, y_cm AS YCm,
                height_cm AS HeightCm, created_at AS CreatedAt, owner_window_until AS OwnerWindowUntil, expires_at AS ExpiresAt
            FROM loot_bags WHERE state = 1 ORDER BY created_at, bag_id
            """, cancellationToken: ct))).ToList();
        if (bags.Count == 0)
            return Array.Empty<LootBagRecord>();
        var items = await ItemsOfAsync(connection, null, bags.Select(b => b.BagId).ToArray(), ct);
        return bags.Select(b => b.ToRecord(items)).ToList();
    }

    /// <summary>One bag's state and rows (tests and admin tools); null when unknown.</summary>
    public async Task<(LootBagState State, LootBagRecord Bag)?> FindAsync(string bagId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var bag = await connection.QuerySingleOrDefaultAsync<BagRow>(new CommandDefinition("""
            SELECT bag_id AS BagId, owner_character_id AS OwnerCharacterId, region_id AS RegionId, x_cm AS XCm, y_cm AS YCm,
                height_cm AS HeightCm, created_at AS CreatedAt, owner_window_until AS OwnerWindowUntil, expires_at AS ExpiresAt,
                state AS State
            FROM loot_bags WHERE bag_id = @bagId
            """, new { bagId }, cancellationToken: ct));
        if (bag == null)
            return null;
        var items = await ItemsOfAsync(connection, null, new[] { bagId }, ct);
        return ((LootBagState)bag.State, bag.ToRecord(items));
    }

    /// <summary>The operation rows of a bag, oldest first: (op key, kind, character, outcome, items JSON) (tests and admin tools).</summary>
    public async Task<IReadOnlyList<(string OpKey, LootBagOpKind Kind, long CharacterId, LootBagTakeOutcome Outcome, string Items)>> OpsAsync(
        string bagId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<OpRow>(new CommandDefinition("""
            SELECT op_key AS OpKey, kind AS Kind, character_id AS CharacterId, outcome AS Outcome, items::text AS Items
            FROM loot_bag_ops WHERE bag_id = @bagId ORDER BY created_at, op_key
            """, new { bagId }, cancellationToken: ct));
        return rows.Select(r => (r.OpKey, (LootBagOpKind)r.Kind, r.CharacterId, (LootBagTakeOutcome)r.Outcome, r.Items)).ToList();
    }

    /// <summary>
    /// Writes <paramref name="batch"/> inside the character save's <paramref name="transaction"/>: each new bag (the bag row, its
    /// stacks, its drop op), then each take. A take locks the bag row first (the same order as the expiry, so the two never
    /// deadlock), needs it open, and lowers or deletes each row only where the count is still the one the world saw; any
    /// mismatch throws <see cref="LootBagConflictException"/> (the take that leaves no row closes the bag) and the caller rolls the whole save back. A key applied before
    /// throws a unique violation on <see cref="BagKeyConstraint"/> or <see cref="OpKeyConstraint"/> (a duplicate save).
    /// </summary>
    public static async Task WriteAsync(DbConnection connection, DbTransaction transaction, long characterId, LootBagBatch batch,
        Guid saveId, DateTime now, CancellationToken ct)
    {
        foreach (var bag in batch.Creates)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO loot_bags (bag_id, owner_character_id, region_id, x_cm, y_cm, height_cm, created_at, owner_window_until,
                    expires_at, state, create_save_id)
                VALUES (@BagId, @OwnerCharacterId, @RegionId, @XCm, @YCm, @HeightCm, @CreatedAt, @OwnerWindowUntil, @ExpiresAt, 1,
                    @saveId)
                """, new
            {
                bag.BagId, bag.OwnerCharacterId, bag.RegionId, bag.XCm, bag.YCm, bag.HeightCm,
                CreatedAt = DbTime.ToUtc(bag.CreatedUtcMs), OwnerWindowUntil = DbTime.ToUtc(bag.OwnerWindowUntilUtcMs),
                ExpiresAt = DbTime.ToUtc(bag.ExpiresUtcMs), saveId,
            }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO loot_bag_items (bag_id, idx, def_id, rarity, count, stolen, stolen_from, stolen_at)
                VALUES (@BagId, @Idx, @DefId, @Rarity, @Count, @Stolen, @StolenFrom, @StolenAt)
                """, bag.Items.Select(i => new
            {
                bag.BagId, Idx = (short)i.Index, i.DefId, Rarity = (short)i.Rarity, i.Count, i.Stolen,
                StolenFrom = StolenMarkColumns.From(i.StolenFrom), StolenAt = StolenMarkColumns.At(i.StolenAtMs),
            }), transaction, cancellationToken: ct));
            await InsertOpAsync(connection, transaction, bag.BagId, LootBagOpKind.Drop, bag.BagId, bag.OwnerCharacterId,
                LootBagTakeOutcome.None, ItemsJson(bag.Items.Select(i => (i.Index, i.DefId, i.Rarity, i.Count))), saveId,
                DbTime.ToUtc(bag.CreatedUtcMs), ct);
        }

        foreach (var take in batch.Takes)
        {
            var state = await connection.QuerySingleOrDefaultAsync<short?>(new CommandDefinition(
                "SELECT state FROM loot_bags WHERE bag_id = @BagId FOR UPDATE", new { take.BagId }, transaction, cancellationToken: ct));
            if (state != (short)LootBagState.Open)
                throw new LootBagConflictException(take.BagId);
            foreach (var row in take.Rows)
            {
                int changed = row.CountTaken == row.CountSeen
                    ? await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM loot_bag_items WHERE bag_id = @BagId AND idx = @Idx AND count = @Seen",
                        new { take.BagId, Idx = (short)row.Index, Seen = row.CountSeen }, transaction, cancellationToken: ct))
                    : await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE loot_bag_items SET count = count - @Taken WHERE bag_id = @BagId AND idx = @Idx AND count = @Seen",
                        new { take.BagId, Idx = (short)row.Index, Seen = row.CountSeen, Taken = row.CountTaken }, transaction,
                        cancellationToken: ct));
                if (changed != 1)
                    throw new LootBagConflictException(take.BagId);
            }
            // The take that leaves no row closes the bag (the bag row is locked above, so takes on one bag commit one at a time).
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE loot_bags SET state = 2, closed_at = @at
                WHERE bag_id = @BagId AND state = 1 AND NOT EXISTS (SELECT 1 FROM loot_bag_items WHERE bag_id = @BagId)
                """, new { take.BagId, at = DbTime.ToUtc(take.AtUtcMs) }, transaction, cancellationToken: ct));
            await InsertOpAsync(connection, transaction, take.OpKey, LootBagOpKind.Take, take.BagId, take.CharacterId, take.Outcome,
                ItemsJson(take.Rows.Select(r => (r.Index, r.DefId, r.Rarity, r.CountTaken))), saveId, DbTime.ToUtc(take.AtUtcMs), ct);
        }
    }

    /// <summary>
    /// Expires one bag in a standalone transaction (the expiry job, off the tick): the bag row locked and closed as expired, its
    /// rows deleted, an op row (<c>bagExpire:{bagId}</c>) with what was destroyed. Idempotent: a retry after an unknown outcome
    /// finds it expired and reads the destroyed rows back from the op row; an emptied or unknown bag is <see cref="LootBagExpireStatus.Gone"/>.
    /// </summary>
    public async Task<LootBagExpireResult> ExpireAsync(string bagId, long atUtcMs, CancellationToken ct = default)
    {
        var at = DbTime.ToUtc(atUtcMs);
        string opKey = ExpireOpKey(bagId);
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var bag = await connection.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
            "SELECT state AS State, owner_character_id AS Owner FROM loot_bags WHERE bag_id = @bagId FOR UPDATE", new { bagId },
            transaction, cancellationToken: ct));
        if (bag is not { } found)
        {
            await transaction.RollbackAsync(ct);
            return new LootBagExpireResult(LootBagExpireStatus.Gone, Array.Empty<LootBagItemRow>());
        }
        if (found.State == (short)LootBagState.Expired)
        {
            var json = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                "SELECT items::text FROM loot_bag_ops WHERE op_key = @opKey", new { opKey }, transaction, cancellationToken: ct));
            await transaction.RollbackAsync(ct);
            return new LootBagExpireResult(LootBagExpireStatus.AlreadyExpired, ParseItems(json));
        }
        if (found.State != (short)LootBagState.Open)
        {
            await transaction.RollbackAsync(ct);
            return new LootBagExpireResult(LootBagExpireStatus.Gone, Array.Empty<LootBagItemRow>());
        }
        var items = (await ItemsOfAsync(connection, transaction, new[] { bagId }, ct))[bagId].ToList();
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM loot_bag_items WHERE bag_id = @bagId", new { bagId }, transaction,
            cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE loot_bags SET state = 3, closed_at = @at WHERE bag_id = @bagId", new { bagId, at }, transaction,
            cancellationToken: ct));
        await InsertOpAsync(connection, transaction, opKey, LootBagOpKind.Expire, bagId, found.Owner, LootBagTakeOutcome.None,
            ItemsJson(items.Select(i => (i.Index, i.DefId, i.Rarity, i.Count))), null, at, ct);
        await transaction.CommitAsync(ct);
        return new LootBagExpireResult(LootBagExpireStatus.Expired, items);
    }

    /// <summary>The expiry's operation key: one per bag, so a bag is expired once ever.</summary>
    public static string ExpireOpKey(string bagId) => $"bagExpire:{bagId}";

    private static Task InsertOpAsync(DbConnection connection, DbTransaction transaction, string opKey, LootBagOpKind kind, string bagId,
        long characterId, LootBagTakeOutcome outcome, string items, Guid? saveId, DateTime at, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO loot_bag_ops (op_key, kind, bag_id, character_id, outcome, items, save_id, created_at)
            VALUES (@opKey, @Kind, @bagId, @characterId, @Outcome, @items::jsonb, @saveId, @at)
            """, new { opKey, Kind = (short)kind, bagId, characterId, Outcome = (short)outcome, items, saveId, at }, transaction,
            cancellationToken: ct));

    /// <summary>The <c>items</c> JSON of an op row: <c>[{"i": idx, "d": defId, "r": rarity, "c": count}]</c> (ids are plain ascii).</summary>
    private static string ItemsJson(IEnumerable<(int Index, string DefId, int Rarity, int Count)> items)
    {
        var text = new StringBuilder("[");
        foreach (var (index, defId, rarity, count) in items)
        {
            if (text.Length > 1)
                text.Append(',');
            text.Append("{\"i\":").Append(index).Append(",\"d\":\"").Append(defId.Replace("\\", "\\\\").Replace("\"", "\\\""))
                .Append("\",\"r\":").Append(rarity).Append(",\"c\":").Append(count).Append('}');
        }
        return text.Append(']').ToString();
    }

    private static IReadOnlyList<LootBagItemRow> ParseItems(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return Array.Empty<LootBagItemRow>();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => new LootBagItemRow(e.GetProperty("i").GetInt32(),
            e.GetProperty("d").GetString() ?? "", e.GetProperty("r").GetInt32(), e.GetProperty("c").GetInt32(), false)).ToList();
    }

    private static async Task<ILookup<string, LootBagItemRow>> ItemsOfAsync(DbConnection connection, DbTransaction? transaction,
        string[] bagIds, CancellationToken ct)
    {
        var rows = await connection.QueryAsync<ItemRow>(new CommandDefinition("""
            SELECT bag_id AS BagId, idx AS Idx, def_id AS DefId, rarity AS Rarity, count AS Count, stolen AS Stolen,
                coalesce(stolen_from, 0) AS StolenFrom, stolen_at AS StolenAt
            FROM loot_bag_items WHERE bag_id = ANY(@bagIds) ORDER BY bag_id, idx
            """, new { bagIds }, transaction, cancellationToken: ct));
        return rows.ToLookup(r => r.BagId, r => new LootBagItemRow(r.Idx, r.DefId, r.Rarity, r.Count, r.Stolen, r.StolenFrom,
            StolenMarkColumns.ToMs(r.StolenAt)));
    }

    private sealed class BagRow
    {
        public string BagId { get; init; } = "";
        public long OwnerCharacterId { get; init; }
        public string RegionId { get; init; } = "";
        public int XCm { get; init; }
        public int YCm { get; init; }
        public int HeightCm { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime OwnerWindowUntil { get; init; }
        public DateTime ExpiresAt { get; init; }
        public short State { get; init; }

        public LootBagRecord ToRecord(ILookup<string, LootBagItemRow> items) => new(BagId, OwnerCharacterId, RegionId, XCm, YCm,
            HeightCm, DbTime.ToUtcMs(CreatedAt), DbTime.ToUtcMs(OwnerWindowUntil), DbTime.ToUtcMs(ExpiresAt), items[BagId].ToList());
    }

    private sealed class ItemRow
    {
        public string BagId { get; init; } = "";
        public short Idx { get; init; }
        public string DefId { get; init; } = "";
        public short Rarity { get; init; }
        public int Count { get; init; }
        public bool Stolen { get; init; }
        public long StolenFrom { get; init; }
        public DateTime? StolenAt { get; init; }
    }

    private sealed class StateRow
    {
        public short State { get; init; }
        public long Owner { get; init; }
    }

    private sealed class OpRow
    {
        public string OpKey { get; init; } = "";
        public short Kind { get; init; }
        public long CharacterId { get; init; }
        public short Outcome { get; init; }
        public string Items { get; init; } = "[]";
    }
}
