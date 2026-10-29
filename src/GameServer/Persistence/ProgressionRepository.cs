using System.Data.Common;
using Dapper;
using Npgsql;

namespace GameServer.Persistence;

/// <summary>
/// Progression in the realm database (WP-M2-3, spec 3.6, 7.1, 14.2): the achievement counters on the character row, the
/// kill counts per monster type and the rank history. Reads them for a login; writes a <see cref="ProgressionBatch"/> inside
/// the transaction of the character save that carries it (<see cref="CharacterRepository.SaveAsync"/>), so counters,
/// rank-ups and their audit rows commit together with the version-checked row (rule 5). Only <c>ProgressionStore</c>
/// produces batches. Called from persistence workers and session tasks, never from the tick thread (spec 1.6).
/// </summary>
public sealed class ProgressionRepository(NpgsqlDataSource realmDb)
{
    /// <summary>Unique keys of <c>rank_history</c>: a violation means a rank-up was already applied.</summary>
    internal const string OpKeyConstraint = "rank_history_pkey";

    internal const string StepConstraint = "rank_history_character_from_key";

    /// <summary>Unique key of <c>attribute_ops</c> (WP-M2-13, V0011): a violation means the placement or reset was already applied.</summary>
    internal const string AttributeOpKeyConstraint = "attribute_ops_pkey";

    /// <summary>The character's achievement counters and kill counts (monster id order).</summary>
    public async Task<ProgressionRecord> LoadAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT achievement_counters::text FROM characters WHERE character_id = @characterId",
            new { characterId }, cancellationToken: ct));
        var kills = await connection.QueryAsync<KillRow>(new CommandDefinition("""
            SELECT monster_def_id AS MonsterDefId, count AS Count
            FROM monster_kill_counts WHERE character_id = @characterId ORDER BY monster_def_id
            """, new { characterId }, cancellationToken: ct));
        return new ProgressionRecord(json ?? ProgressionRecord.Empty.AchievementCountersJson,
            kills.Select(k => new MonsterKillCount(k.MonsterDefId, k.Count)).ToList());
    }

    /// <summary>The rank-ups of a character, oldest first (tests and admin tools).</summary>
    public async Task<IReadOnlyList<RankHistoryEntry>> RankHistoryAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<RankHistoryEntry>(new CommandDefinition("""
            SELECT op_key AS OpKey, from_rank AS FromRank, to_rank AS ToRank, counters::text AS CountersJson, save_id AS SaveId
            FROM rank_history WHERE character_id = @characterId ORDER BY created_at, from_rank
            """, new { characterId }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>The attribute placements and resets of a character, oldest first (tests and admin tools; WP-M2-13).</summary>
    public async Task<IReadOnlyList<AttributeOpEntry>> AttributeOpsAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<AttributeOpEntry>(new CommandDefinition("""
            SELECT op_key AS OpKey, op AS Op, attribute AS Attribute, points AS Points, points_before AS PointsBefore,
                points_after AS PointsAfter, placed_before AS PlacedBefore, placed_after AS PlacedAfter, save_id AS SaveId
            FROM attribute_ops WHERE character_id = @characterId ORDER BY created_at, op_key
            """, new { characterId }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Writes <paramref name="batch"/> inside <paramref name="transaction"/>: the attribute placements and resets first and
    /// then the rank-ups (a key or a step applied before throws a unique violation on <see cref="AttributeOpKeyConstraint"/>,
    /// <see cref="OpKeyConstraint"/> or <see cref="StepConstraint"/>, and the caller rolls back), then the counters, then the
    /// kill counts as absolute values.
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, long characterId,
        ProgressionBatch batch, Guid saveId, DateTime now, CancellationToken ct)
    {
        if (batch.AttributeOpList.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO attribute_ops (op_key, character_id, op, attribute, points, points_before, points_after,
                    placed_before, placed_after, save_id, created_at)
                VALUES (@OpKey, @characterId, @Op, @Attribute, @Points, @PointsBefore, @PointsAfter, @PlacedBefore, @PlacedAfter,
                    @saveId, @now)
                """, batch.AttributeOpList.Select(o => new
                {
                    o.OpKey, characterId, Op = (short)o.Kind, Attribute = o.Attribute < 0 ? (short?)null : (short)o.Attribute,
                    o.Points, o.PointsBefore, o.PointsAfter, o.PlacedBefore, o.PlacedAfter, saveId, now,
                }), transaction, cancellationToken: ct));
        }

        if (batch.RankUps.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO rank_history (op_key, character_id, from_rank, to_rank, counters, save_id, created_at)
                VALUES (@OpKey, @characterId, @FromRank, @ToRank, CAST(@Counters AS jsonb), @saveId, @now)
                """, batch.RankUps.Select(r => new
                {
                    r.OpKey, characterId, FromRank = (short)r.FromRank, ToRank = (short)r.ToRank,
                    Counters = r.CountersJson, saveId, now,
                }), transaction, cancellationToken: ct));
        }

        if (batch.AchievementCountersJson != null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE characters SET achievement_counters = CAST(@Counters AS jsonb) WHERE character_id = @characterId",
                new { Counters = batch.AchievementCountersJson, characterId }, transaction, cancellationToken: ct));
        }

        if (batch.KillCounts.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO monster_kill_counts (character_id, monster_def_id, count, updated_at)
                VALUES (@characterId, @MonsterDefId, @Count, @now)
                ON CONFLICT (character_id, monster_def_id) DO UPDATE SET count = EXCLUDED.count, updated_at = EXCLUDED.updated_at
                """, batch.KillCounts.Select(k => new { characterId, k.MonsterDefId, k.Count, now }),
                transaction, cancellationToken: ct));
        }
    }

    private sealed class KillRow
    {
        public string MonsterDefId { get; init; } = "";
        public long Count { get; init; }
    }
}

/// <summary>One <c>attribute_ops</c> row (WP-M2-13): op 1 place, 2 reset; the placed points in STR, DEX, STA, INT order.</summary>
public sealed class AttributeOpEntry
{
    public string OpKey { get; init; } = "";
    public short Op { get; init; }
    public short? Attribute { get; init; }
    public int Points { get; init; }
    public int PointsBefore { get; init; }
    public int PointsAfter { get; init; }
    public int[] PlacedBefore { get; init; } = [];
    public int[] PlacedAfter { get; init; } = [];
    public Guid SaveId { get; init; }
}

/// <summary>One <c>rank_history</c> row.</summary>
public sealed class RankHistoryEntry
{
    public string OpKey { get; init; } = "";
    public short FromRank { get; init; }
    public short ToRank { get; init; }
    public string CountersJson { get; init; } = "";
    public Guid SaveId { get; init; }
}
