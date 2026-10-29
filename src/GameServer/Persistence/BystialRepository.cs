using System.Data.Common;
using Dapper;
using Npgsql;
using Shared.Data.Tables;

namespace GameServer.Persistence;

/// <summary>
/// The Bystial book in the realm database (WP-M2-8, spec 14.2, 7.1): <c>bystial_book</c> and its operations
/// <c>bystial_book_ops</c>. Reads the book for a login; writes a <see cref="BystialBatch"/> inside the transaction of the
/// character save that carries it (<see cref="CharacterRepository.SaveAsync"/>), so cards, operations and their audit rows
/// commit together with the version-checked row (rule 5). Only <c>BystialService</c> produces batches. Called from
/// persistence workers and session tasks, never from the tick thread (spec 1.6).
/// </summary>
public sealed class BystialRepository(NpgsqlDataSource realmDb)
{
    /// <summary>The unique key of <c>bystial_book_ops</c>: a violation means an operation was already applied.</summary>
    internal const string OpKeyConstraint = "bystial_book_ops_pkey";

    /// <summary>The character's cards, in monster id order.</summary>
    public async Task<BystialRecord> LoadAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<CardRow>(new CommandDefinition("""
            SELECT monster_def_id AS MonsterDefId, tier AS Tier, slot AS Slot
            FROM bystial_book WHERE character_id = @characterId ORDER BY monster_def_id
            """, new { characterId }, cancellationToken: ct));
        return new BystialRecord(rows.Select(r => new BystialCardRow(r.MonsterDefId, (BystialTier)r.Tier, r.Slot)).ToList());
    }

    /// <summary>The book operations of a character, oldest first (tests and admin tools).</summary>
    public async Task<IReadOnlyList<BystialOpEntry>> OpsAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<BystialOpEntry>(new CommandDefinition("""
            SELECT op_key AS OpKey, kind AS Kind, monster_def_id AS MonsterDefId, other_monster_def_id AS OtherMonsterDefId,
                   from_tier AS FromTier, to_tier AS ToTier, slot AS Slot, kill_count AS KillCount, save_id AS SaveId
            FROM bystial_book_ops WHERE character_id = @characterId ORDER BY created_at, op_key
            """, new { characterId }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Writes <paramref name="batch"/> inside <paramref name="transaction"/>: the operations first (a key applied before
    /// throws a unique violation on <see cref="OpKeyConstraint"/>, and the caller rolls back), then the changed cards: their
    /// slots cleared, then each written whole, so a swap never meets the one-card-per-slot index half done.
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, long characterId,
        BystialBatch batch, Guid saveId, DateTime now, CancellationToken ct)
    {
        if (batch.Ops.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO bystial_book_ops (op_key, character_id, kind, monster_def_id, other_monster_def_id, from_tier,
                                              to_tier, slot, kill_count, save_id, created_at)
                VALUES (@OpKey, @characterId, @Kind, @MonsterDefId, @OtherMonsterDefId, @FromTier, @ToTier, @Slot, @KillCount,
                        @saveId, @now)
                """, batch.Ops.Select(op => new
                {
                    op.OpKey, characterId, Kind = (short)op.Kind, op.MonsterDefId, op.OtherMonsterDefId,
                    FromTier = (short)op.FromTier, ToTier = (short)op.ToTier, Slot = (short?)op.Slot, op.KillCount, saveId, now,
                }), transaction, cancellationToken: ct));
        }

        if (batch.Cards.Count == 0)
            return;
        var ids = batch.Cards.Select(c => c.MonsterDefId).ToArray();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE bystial_book SET slot = NULL WHERE character_id = @characterId AND monster_def_id = ANY(@ids)",
            new { characterId, ids }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO bystial_book (character_id, monster_def_id, tier, slot, earned_at, updated_at)
            VALUES (@characterId, @MonsterDefId, @Tier, @Slot, @now, @now)
            ON CONFLICT (character_id, monster_def_id) DO UPDATE SET tier = EXCLUDED.tier, slot = EXCLUDED.slot,
                                                                   updated_at = EXCLUDED.updated_at
            """, batch.Cards.Select(c => new { characterId, c.MonsterDefId, Tier = (short)c.Tier, Slot = (short?)c.Slot, now }),
            transaction, cancellationToken: ct));
    }

    private sealed class CardRow
    {
        public string MonsterDefId { get; init; } = "";
        public short Tier { get; init; }
        public short? Slot { get; init; }
    }
}

/// <summary>One <c>bystial_book_ops</c> row.</summary>
public sealed class BystialOpEntry
{
    public string OpKey { get; init; } = "";
    public short Kind { get; init; }
    public string MonsterDefId { get; init; } = "";
    public string? OtherMonsterDefId { get; init; }
    public short FromTier { get; init; }
    public short ToTier { get; init; }
    public short? Slot { get; init; }
    public long? KillCount { get; init; }
    public Guid SaveId { get; init; }
}
