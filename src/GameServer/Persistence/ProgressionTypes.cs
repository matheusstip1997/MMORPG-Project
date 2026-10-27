using Shared.Simulation.Stats;

namespace GameServer.Persistence;

/// <summary>A character's kill count of one monster type (<c>monster_kill_counts</c>, R-14.2-01): absolute, not a delta.</summary>
public readonly record struct MonsterKillCount(string MonsterDefId, long Count);

/// <summary>A rank-up to write with the save (R-3.6-04): its idempotency key, the step, and the counters it was checked against.</summary>
public sealed record RankUpRecord(string OpKey, Rank FromRank, Rank ToRank, string CountersJson);

/// <summary>
/// A character's progression changes since its last save (save class 2, R-7.1-01; WP-M2-3), beside the level, exp, rank and
/// skill points that every save writes in the row: the achievement counters (whole, when changed), the kill counts that
/// changed (absolute values), and the rank-ups with their audit rows. Written in the same transaction as the character row,
/// so a retried save writes the same thing and a crash loses all of it or nothing.
/// </summary>
public sealed record ProgressionBatch(
    string? AchievementCountersJson,
    IReadOnlyList<MonsterKillCount> KillCounts,
    IReadOnlyList<RankUpRecord> RankUps)
{
    public bool IsEmpty => AchievementCountersJson == null && KillCounts.Count == 0 && RankUps.Count == 0;
}

/// <summary>The progression state a login reads besides the character row: the achievement counters and the kill counts.</summary>
public sealed record ProgressionRecord(string AchievementCountersJson, IReadOnlyList<MonsterKillCount> KillCounts)
{
    public static readonly ProgressionRecord Empty = new("{\"v\":1}", Array.Empty<MonsterKillCount>());
}

/// <summary>
/// Hands a character's pending progression batch to the save that is being issued, and learns how that save ended.
/// Implemented by <c>ProgressionStore</c>; called on the world thread by <see cref="SaveScheduler"/>.
/// </summary>
public interface IProgressionBatchSource
{
    /// <summary>The changes not yet in a save, now part of save <paramref name="saveId"/>; null when there are none.</summary>
    ProgressionBatch? TakeBatch(long characterId, Guid saveId);

    /// <summary>Save <paramref name="saveId"/> committed: its batch is in the database.</summary>
    void Committed(long characterId, Guid saveId);
}
