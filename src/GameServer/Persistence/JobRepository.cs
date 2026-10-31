using System.Data.Common;
using Dapper;
using Npgsql;
using Shared.Simulation.Jobs;

namespace GameServer.Persistence;

/// <summary>
/// Jobs in the realm database (WP-M3-1, WP-M3-7, spec 5.1, 7.1): the job records, their learned tree nodes and the job
/// operations. Reads them for a login; writes a <see cref="JobBatch"/> inside the transaction of the character save that
/// carries it (<see cref="CharacterRepository.SaveAsync"/>), so a pick, its audit row and its starter items commit together,
/// and a learn or respec with its nodes and points (rule 5). Only <c>JobService</c> produces batches. Never called from
/// the tick thread.
/// </summary>
public sealed class JobRepository(NpgsqlDataSource realmDb)
{
    /// <summary>Unique keys of <c>job_ops</c> and <c>job_progress</c>: a violation means the pick (or a second pick into a slot) was applied.</summary>
    internal const string OpKeyConstraint = "job_ops_pkey";

    internal const string OnePickConstraint = "job_ops_one_pick_per_slot";

    internal const string SlotConstraint = "job_progress_slot";

    public async Task<JobRecord> LoadAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<ProgressRow>(new CommandDefinition("""
            SELECT job_id AS JobId, slot AS Slot, level AS Level, xp AS Xp, points AS Points, recent::text AS Recent
            FROM job_progress WHERE character_id = @characterId ORDER BY slot
            """, new { characterId }, cancellationToken: ct));
        var nodes = (await connection.QueryAsync<NodeRow>(new CommandDefinition("""
            SELECT job_id AS JobId, node_id AS NodeId, level AS Level
            FROM job_nodes WHERE character_id = @characterId ORDER BY job_id, node_id
            """, new { characterId }, cancellationToken: ct))).ToLookup(n => n.JobId, StringComparer.Ordinal);
        return new JobRecord(rows.Select(r => new JobProgressRow(r.JobId, (JobSlot)r.Slot, r.Level, r.Xp, r.Points, r.Recent)
        {
            Nodes = nodes[r.JobId].Select(n => new JobNodeRow(n.NodeId, n.Level)).ToList(),
        }).ToList());
    }

    /// <summary>The job operations of a character, oldest first (tests and admin tools).</summary>
    public async Task<IReadOnlyList<JobOpRecord>> OpsAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<OpRow>(new CommandDefinition("""
            SELECT op_key AS OpKey, op AS Op, job_id AS JobId, slot AS Slot, level_at_pick AS LevelAtPick, starter_items AS StarterItems,
                   node_id AS NodeId, from_level AS FromLevel, to_level AS ToLevel, points_before AS PointsBefore, points_after AS PointsAfter
            FROM job_ops WHERE character_id = @characterId ORDER BY created_at, op_key
            """, new { characterId }, cancellationToken: ct));
        return rows.Select(r => new JobOpRecord(r.OpKey, (JobOp)r.Op, r.JobId, (JobSlot)r.Slot, r.LevelAtPick, r.StarterItems)
        {
            NodeId = r.NodeId, FromLevel = r.FromLevel, ToLevel = r.ToLevel, PointsBefore = r.PointsBefore, PointsAfter = r.PointsAfter,
        }).ToList();
    }

    /// <summary>
    /// Writes <paramref name="batch"/> inside <paramref name="transaction"/>: the operations first (a key or a slot picked
    /// before throws a unique violation on <see cref="OpKeyConstraint"/> or <see cref="OnePickConstraint"/>, and the caller
    /// rolls back), then every job record as it is now, then the whole set of learned nodes.
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, long characterId, JobBatch batch,
        Guid saveId, DateTime now, CancellationToken ct)
    {
        if (batch.Ops.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO job_ops (op_key, character_id, op, job_id, slot, level_at_pick, starter_items, node_id, from_level, to_level,
                                     points_before, points_after, save_id, created_at)
                VALUES (@OpKey, @characterId, @Op, @JobId, @Slot, @LevelAtPick, @StarterItems, @NodeId, @FromLevel, @ToLevel,
                        @PointsBefore, @PointsAfter, @saveId, @now)
                """, batch.Ops.Select(o => new
            {
                o.OpKey, characterId, Op = (short)o.Op, o.JobId, Slot = (short)o.Slot, LevelAtPick = (short)o.LevelAtPick,
                StarterItems = o.StarterItems.ToArray(), o.NodeId, FromLevel = (short)o.FromLevel, ToLevel = (short)o.ToLevel,
                o.PointsBefore, o.PointsAfter, saveId, now,
            }), transaction, cancellationToken: ct));
        }
        if (batch.Jobs.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO job_progress (character_id, job_id, slot, level, xp, points, recent, updated_at)
                VALUES (@characterId, @JobId, @Slot, @Level, @Xp, @Points, CAST(@RecentJson AS jsonb), @now)
                ON CONFLICT (character_id, job_id) DO UPDATE SET
                    level = EXCLUDED.level, xp = EXCLUDED.xp, points = EXCLUDED.points, recent = EXCLUDED.recent,
                    updated_at = EXCLUDED.updated_at
                """, batch.Jobs.Select(j => new
            {
                characterId, j.JobId, Slot = (short)j.Slot, Level = (short)j.Level, j.Xp, j.Points, j.RecentJson, now,
            }), transaction, cancellationToken: ct));
            // The learned nodes as they are now (WP-M3-7): the whole set, so a retried save writes the same.
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM job_nodes WHERE character_id = @characterId", new { characterId }, transaction, cancellationToken: ct));
            var nodes = batch.Jobs.SelectMany(j => j.Nodes.Select(n => new
            {
                characterId, j.JobId, n.NodeId, Level = (short)n.Level, now,
            })).ToList();
            if (nodes.Count > 0)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO job_nodes (character_id, job_id, node_id, level, updated_at) VALUES (@characterId, @JobId, @NodeId, @Level, @now)
                    """, nodes, transaction, cancellationToken: ct));
            }
        }
    }

    private sealed class ProgressRow
    {
        public string JobId { get; set; } = "";
        public short Slot { get; set; }
        public short Level { get; set; }
        public long Xp { get; set; }
        public int Points { get; set; }
        public string Recent { get; set; } = "";
    }

    private sealed class NodeRow
    {
        public string JobId { get; set; } = "";
        public string NodeId { get; set; } = "";
        public short Level { get; set; }
    }

    private sealed class OpRow
    {
        public string OpKey { get; set; } = "";
        public short Op { get; set; }
        public string JobId { get; set; } = "";
        public short Slot { get; set; }
        public short LevelAtPick { get; set; }
        public string[] StarterItems { get; set; } = Array.Empty<string>();
        public string NodeId { get; set; } = "";
        public short FromLevel { get; set; }
        public short ToLevel { get; set; }
        public int PointsBefore { get; set; }
        public int PointsAfter { get; set; }
    }
}
