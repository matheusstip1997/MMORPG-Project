using System.Data.Common;
using Dapper;
using Npgsql;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Persistence;

/// <summary>
/// Outpost runs in the realm database (WP-M2-6, spec 5.5, 7.1): <c>quest_runs</c>, <c>quest_run_steps</c> and the
/// <c>quest_run_audit</c> rows. Reads the open runs for a login; writes a <see cref="QuestBatch"/> inside the transaction of the
/// character save that carries it (<see cref="CharacterRepository.SaveAsync"/>), so run state, exp and delivered items commit
/// together with the version-checked row (rule 5). Only <c>Quests/RunStore</c> produces batches. Called from persistence workers
/// and session tasks, never from the tick thread (spec 1.6).
/// </summary>
public sealed class QuestRepository(NpgsqlDataSource realmDb)
{
    /// <summary>Unique keys of <c>quest_run_audit</c>: a violation means a run operation was already applied.</summary>
    internal const string OpKeyConstraint = "quest_run_audit_pkey";

    internal const string OneClaimConstraint = "quest_run_audit_one_claim";

    internal const string OneOpenRunConstraint = "quest_runs_one_open_per_character";

    /// <summary>The character's runs that are not claimed or abandoned, with their steps (oldest first).</summary>
    public async Task<IReadOnlyList<RunRecord>> LoadOpenAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        return await LoadAsync(connection, "WHERE owner_character_id = @characterId AND state IN (1, 2)", new { characterId }, ct);
    }

    /// <summary>Every run of a character, oldest first (tests and admin tools).</summary>
    public async Task<IReadOnlyList<RunRecord>> LoadAllAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        return await LoadAsync(connection, "WHERE owner_character_id = @characterId", new { characterId }, ct);
    }

    /// <summary>The audit rows of a character, oldest first (tests and admin tools).</summary>
    public async Task<IReadOnlyList<RunAuditEntry>> AuditAsync(long characterId, CancellationToken ct = default)
    {
        await using var connection = await realmDb.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<RunAuditEntry>(new CommandDefinition("""
            SELECT op_key AS OpKey, run_id AS RunId, action AS Action, step_index AS StepIndex, amount AS Amount, save_id AS SaveId
            FROM quest_run_audit WHERE character_id = @characterId ORDER BY created_at, op_key
            """, new { characterId }, cancellationToken: ct));
        return rows.ToList();
    }

    private static async Task<IReadOnlyList<RunRecord>> LoadAsync(DbConnection connection, string where, object parameters,
        CancellationToken ct)
    {
        var runs = (await connection.QueryAsync<RunRow>(new CommandDefinition($"""
            SELECT run_id AS RunId, owner_character_id AS OwnerCharacterId, outpost_marker_id AS OutpostMarkerId, state AS State,
                   board_epoch AS BoardEpoch, started_at AS StartedAt, bonus_percent AS BonusPercent,
                   penalty_percent AS PenaltyPercent, claimed_exp AS ClaimedExp
            FROM quest_runs {where} ORDER BY started_at, run_id
            """, parameters, cancellationToken: ct))).ToList();
        if (runs.Count == 0)
            return Array.Empty<RunRecord>();
        var steps = (await connection.QueryAsync<StepRow>(new CommandDefinition("""
            SELECT run_id AS RunId, step_index AS StepIndex, kind AS Kind, quest_def_id AS QuestDefId,
                   monster_def_id AS MonsterDefId, item_def_id AS ItemDefId, required AS Required, progress AS Progress,
                   reward_exp AS RewardExp, reward_percent AS RewardPercent, state AS State
            FROM quest_run_steps WHERE run_id = ANY(@ids) ORDER BY run_id, step_index
            """, new { ids = runs.Select(r => r.RunId).ToArray() }, cancellationToken: ct))).ToList();
        return runs.Select(r => new RunRecord(r.RunId, r.OwnerCharacterId, r.OutpostMarkerId, (RunState)r.State,
                DbTime.ToUtcMs(r.StartedAt), r.BoardEpoch, r.BonusPercent, r.PenaltyPercent, r.ClaimedExp,
                steps.Where(s => s.RunId == r.RunId)
                    .Select(s => new RunStepRecord(s.StepIndex, (QuestKind)s.Kind, s.QuestDefId, s.MonsterDefId, s.ItemDefId,
                        s.Required, s.Progress, s.RewardExp, s.RewardPercent, (RunStepState)s.State))
                    .ToList()))
            .ToList();
    }

    /// <summary>
    /// Writes <paramref name="batch"/> inside <paramref name="transaction"/>: the runs and steps as absolute rows (a new run is
    /// inserted, a known one updated), then the audit rows (a key or a second claim applied before throws a unique violation on
    /// <see cref="OpKeyConstraint"/> or <see cref="OneClaimConstraint"/>, and the caller rolls back).
    /// </summary>
    internal static async Task WriteAsync(DbConnection connection, DbTransaction transaction, long characterId, QuestBatch batch,
        Guid saveId, DateTime now, CancellationToken ct)
    {
        foreach (var run in batch.Runs)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO quest_runs (run_id, owner_character_id, outpost_marker_id, state, board_epoch, started_at, bonus_percent,
                    penalty_percent, claimed_exp, updated_at, save_id)
                VALUES (@RunId, @OwnerCharacterId, @OutpostMarkerId, @State, @BoardEpoch, @StartedAt, @BonusPercent, @PenaltyPercent,
                    @ClaimedExp, @now, @saveId)
                ON CONFLICT (run_id) DO UPDATE SET state = EXCLUDED.state, bonus_percent = EXCLUDED.bonus_percent,
                    penalty_percent = EXCLUDED.penalty_percent, claimed_exp = EXCLUDED.claimed_exp, updated_at = EXCLUDED.updated_at,
                    save_id = EXCLUDED.save_id
                WHERE quest_runs.owner_character_id = EXCLUDED.owner_character_id
                """, new
                {
                    run.RunId, run.OwnerCharacterId, run.OutpostMarkerId, State = (short)run.State, run.BoardEpoch,
                    StartedAt = DbTime.ToUtc(run.StartedAtUtcMs), run.BonusPercent, run.PenaltyPercent, run.ClaimedExp, now, saveId,
                }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO quest_run_steps (run_id, step_index, kind, quest_def_id, monster_def_id, item_def_id, required, progress,
                    reward_exp, reward_percent, state)
                VALUES (@RunId, @StepIndex, @Kind, @QuestDefId, @MonsterDefId, @ItemDefId, @Required, @Progress, @RewardExp,
                    @RewardPercent, @State)
                ON CONFLICT (run_id, step_index) DO UPDATE SET progress = EXCLUDED.progress, state = EXCLUDED.state
                """, run.Steps.Select(s => new
                {
                    run.RunId, StepIndex = (short)s.Index, Kind = (short)s.Kind, s.QuestDefId, s.MonsterDefId, s.ItemDefId,
                    s.Required, s.Progress, s.RewardExp, s.RewardPercent, State = (short)s.State,
                }), transaction, cancellationToken: ct));
        }

        if (batch.Audit.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO quest_run_audit (op_key, run_id, character_id, action, step_index, amount, save_id, created_at)
                VALUES (@OpKey, @RunId, @characterId, @Action, @StepIndex, @Amount, @saveId, @now)
                """, batch.Audit.Select(a => new
                {
                    a.OpKey, a.RunId, characterId, Action = (short)a.Action,
                    StepIndex = a.StepIndex < 0 ? (short?)null : (short)a.StepIndex, a.Amount, saveId, now,
                }), transaction, cancellationToken: ct));
        }
    }

    private sealed class RunRow
    {
        public Guid RunId { get; init; }
        public long OwnerCharacterId { get; init; }
        public string OutpostMarkerId { get; init; } = "";
        public short State { get; init; }
        public long BoardEpoch { get; init; }
        public DateTime StartedAt { get; init; }
        public int BonusPercent { get; init; }
        public int PenaltyPercent { get; init; }
        public long ClaimedExp { get; init; }
    }

    private sealed class StepRow
    {
        public Guid RunId { get; init; }
        public short StepIndex { get; init; }
        public short Kind { get; init; }
        public string QuestDefId { get; init; } = "";
        public string MonsterDefId { get; init; } = "";
        public string? ItemDefId { get; init; }
        public int Required { get; init; }
        public int Progress { get; init; }
        public long RewardExp { get; init; }
        public int RewardPercent { get; init; }
        public short State { get; init; }
    }
}

/// <summary>One <c>quest_run_audit</c> row.</summary>
public sealed class RunAuditEntry
{
    public string OpKey { get; init; } = "";
    public Guid RunId { get; init; }
    public short Action { get; init; }
    public short? StepIndex { get; init; }
    public long Amount { get; init; }
    public Guid SaveId { get; init; }
}
