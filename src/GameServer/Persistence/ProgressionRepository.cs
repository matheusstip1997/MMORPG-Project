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

    /// <summary>
    /// Writes <paramref name="batch"/> inside <paramref name="transaction"/>: the rank-ups first (a key or a step applied
    /// before throws a unique violation on <see cref="OpKeyConstraint"/> or <see cref="StepConstraint"/>, and the caller
    /// rolls back), then the counters, then the kill counts as absolute values.
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, long characterId,
        ProgressionBatch batch, Guid saveId, DateTime now, CancellationToken ct)
    {
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

/// <summary>One <c>rank_history</c> row.</summary>
public sealed class RankHistoryEntry
{
    public string OpKey { get; init; } = "";
    public short FromRank { get; init; }
    public short ToRank { get; init; }
    public string CountersJson { get; init; } = "";
    public Guid SaveId { get; init; }
}
