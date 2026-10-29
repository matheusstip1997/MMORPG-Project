using System.Data.Common;
using Dapper;
using Npgsql;

namespace GameServer.Persistence;

/// <summary>
/// The skill tree in the realm database (WP-M2-5, spec 3.5, 7.1): learned nodes, the hotbar and the skill operations.
/// Reads them for a login; writes a <see cref="SkillBatch"/> inside the transaction of the character save that carries it
/// (<see cref="CharacterRepository.SaveAsync"/>), so the operations, their audit rows, the nodes and the unspent points in
/// the row commit together (rule 5). Only <c>SkillTreeService</c> produces batches. Never called from the tick thread.
/// </summary>
public sealed class SkillRepository(NpgsqlDataSource realmDb)
{
    /// <summary>Unique keys of <c>skill_ops</c>: a violation means the operation (or a second advance into a tier) was applied.</summary>
    internal const string OpKeyConstraint = "skill_ops_pkey";

    internal const string AdvanceConstraint = "skill_ops_advance_tier";

    public async Task<SkillRecord> LoadAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var hotbar = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT hotbar::text FROM characters WHERE character_id = @characterId", new { characterId }, cancellationToken: ct));
        var rows = await connection.QueryAsync<SkillRow>(new CommandDefinition("""
            SELECT node_id AS NodeId, level AS Level FROM character_skills WHERE character_id = @characterId ORDER BY node_id
            """, new { characterId }, cancellationToken: ct));
        return new SkillRecord(rows.Select(r => new LearnedSkill(r.NodeId, r.Level)).ToList(), hotbar);
    }

    /// <summary>The skill operations of a character, oldest first (tests and admin tools).</summary>
    public async Task<IReadOnlyList<SkillOpRecord>> OpsAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<OpRow>(new CommandDefinition("""
            SELECT op_key AS OpKey, op AS Op, node_id AS NodeId, from_node_id AS FromNodeId, from_level AS FromLevel,
                   to_level AS ToLevel, points_before AS PointsBefore, points_after AS PointsAfter, class_from AS ClassFrom,
                   class_to AS ClassTo, advance_tier AS AdvanceTier
            FROM skill_ops WHERE character_id = @characterId ORDER BY created_at, op_key
            """, new { characterId }, cancellationToken: ct));
        return rows.Select(r => new SkillOpRecord(r.OpKey, (Shared.Protocol.SkillTreeOp)r.Op, r.NodeId, r.FromNodeId, r.FromLevel,
            r.ToLevel, r.PointsBefore, r.PointsAfter, r.ClassFrom, r.ClassTo, r.AdvanceTier)).ToList();
    }

    /// <summary>
    /// Writes <paramref name="batch"/> inside <paramref name="transaction"/>: the operations first (a key or an advance
    /// applied before throws a unique violation on <see cref="OpKeyConstraint"/> or <see cref="AdvanceConstraint"/>, and the
    /// caller rolls back), then the class, the hotbar and the learned set as it is now.
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, long characterId, SkillBatch batch,
        Guid saveId, DateTime now, CancellationToken ct)
    {
        if (batch.Ops.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO skill_ops (op_key, character_id, op, node_id, from_node_id, from_level, to_level, points_before,
                                       points_after, class_from, class_to, advance_tier, save_id, created_at)
                VALUES (@OpKey, @characterId, @Op, @NodeId, @FromNodeId, @FromLevel, @ToLevel, @PointsBefore, @PointsAfter,
                        @ClassFrom, @ClassTo, @AdvanceTier, @saveId, @now)
                """, batch.Ops.Select(o => new
            {
                o.OpKey, characterId, Op = (short)o.Op, o.NodeId, o.FromNodeId, FromLevel = (short)o.FromLevel,
                ToLevel = (short)o.ToLevel, o.PointsBefore, o.PointsAfter, o.ClassFrom, o.ClassTo,
                AdvanceTier = o.AdvanceTier.HasValue ? (short?)o.AdvanceTier.Value : null, saveId, now,
            }), transaction, cancellationToken: ct));
        }
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE characters SET hotbar = CAST(@hotbar AS jsonb), class_id = COALESCE(@classId, class_id)
            WHERE character_id = @characterId
            """, new { hotbar = batch.HotbarJson, classId = batch.ClassId, characterId }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM character_skills WHERE character_id = @characterId", new { characterId }, transaction, cancellationToken: ct));
        if (batch.Learned.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO character_skills (character_id, node_id, level, updated_at) VALUES (@characterId, @NodeId, @Level, @now)
                """, batch.Learned.Select(s => new { characterId, s.NodeId, Level = (short)s.Level, now }), transaction,
                cancellationToken: ct));
        }
    }

    private sealed class SkillRow
    {
        public string NodeId { get; set; } = "";
        public short Level { get; set; }
    }

    private sealed class OpRow
    {
        public string OpKey { get; set; } = "";
        public short Op { get; set; }
        public string NodeId { get; set; } = "";
        public string FromNodeId { get; set; } = "";
        public short FromLevel { get; set; }
        public short ToLevel { get; set; }
        public int PointsBefore { get; set; }
        public int PointsAfter { get; set; }
        public string ClassFrom { get; set; } = "";
        public string ClassTo { get; set; } = "";
        public short? AdvanceTier { get; set; }
    }
}
