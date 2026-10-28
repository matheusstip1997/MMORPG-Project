using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Persistence;

/// <summary>One step of a run as stored in <c>quest_run_steps</c> (spec 5.5 <c>RunStep</c>); ids are the tables' string ids.</summary>
public sealed record RunStepRecord(int Index, QuestKind Kind, string QuestDefId, string MonsterDefId, string? ItemDefId,
    int Required, int Progress, long RewardExp, int RewardPercent, RunStepState State);

/// <summary>
/// A run as stored in <c>quest_runs</c> (spec 5.5 <c>Run</c>): its owner (a character; <c>owner_party_id</c> stays null until
/// party runs, M4), the outpost it was built at, its state and, once claimed, the exp it paid.
/// </summary>
public sealed record RunRecord(Guid RunId, long OwnerCharacterId, string OutpostMarkerId, RunState State, long StartedAtUtcMs,
    long BoardEpoch, int BonusPercent, int PenaltyPercent, long ClaimedExp, IReadOnlyList<RunStepRecord> Steps);

/// <summary>
/// One run operation for <c>quest_run_audit</c> (rule 5): its idempotency key, the action, the step (-1 for none) and the amount
/// (units delivered, or the exp a claim paid). A key the table already holds fails the save (<see cref="SaveStatus.DuplicateOperation"/>).
/// </summary>
public sealed record RunAuditRecord(string OpKey, Guid RunId, QuestRunAction Action, int StepIndex, long Amount);

/// <summary>
/// A character's run changes since its last save (save class 2, R-7.1-01; WP-M2-6): the runs that changed, whole (absolute, so
/// a retried save writes the same thing), and the audit rows of accepts, deliveries, forfeits and claims. Written in the same
/// transaction as the character row, its exp and the inventory batch that holds the delivered items' removal, so a crash
/// loses all of it or nothing.
/// </summary>
public sealed record QuestBatch(IReadOnlyList<RunRecord> Runs, IReadOnlyList<RunAuditRecord> Audit)
{
    public bool IsEmpty => Runs.Count == 0 && Audit.Count == 0;
}

/// <summary>
/// Hands a character's pending run batch to the save that is being issued, and learns how that save ended. Implemented by
/// <c>Quests/RunStore</c>; called on the world thread by <see cref="SaveScheduler"/>.
/// </summary>
public interface IQuestBatchSource
{
    /// <summary>The changes not yet in a save, now part of save <paramref name="saveId"/>; null when there are none.</summary>
    QuestBatch? TakeBatch(long characterId, Guid saveId);

    /// <summary>Save <paramref name="saveId"/> committed: its batch is in the database.</summary>
    void Committed(long characterId, Guid saveId);
}
