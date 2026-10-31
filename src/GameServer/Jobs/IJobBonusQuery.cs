using Shared.Simulation.Jobs;

namespace GameServer.Jobs;

/// <summary>
/// The craft bonuses a character's job skill tree gives (spec 5.1 R-5.1-04: "skill-tree bonuses are computed when an
/// activity starts and passed into the quality/odds formulas"), read by harvesting, the forge, enchanting and gems. The job
/// trees (WP-M3-7) implement it through <see cref="JobService"/>; until then <see cref="NoJobBonuses"/> gives none. An
/// activity reads it once when it starts and keeps what it read. World thread only. (WP-M3-7's shape; whichever WP merges
/// second keeps one file.)
/// </summary>
public interface IJobBonusQuery
{
    /// <summary>The job tree's bonus of <paramref name="kind"/> for <paramref name="activity"/> of the job, capped; committed nodes only.</summary>
    int Get(long characterId, string jobId, JobBonusKind kind, JobActivity activity);

    /// <summary>Master Molds (<see cref="JobBonusKind.UnlockMoldTiers"/>): the job may make mold recipes of this tier (not use molds).</summary>
    bool MoldTierUnlocked(long characterId, string jobId, int tier);
}

/// <summary>No job tree bonuses (the stub until the job skill trees, WP-M3-7).</summary>
public sealed class NoJobBonuses : IJobBonusQuery
{
    public static readonly NoJobBonuses Instance = new();

    private NoJobBonuses()
    {
    }

    public int Get(long characterId, string jobId, JobBonusKind kind, JobActivity activity) => 0; // TODO(WP-M3-7): the learned nodes

    public bool MoldTierUnlocked(long characterId, string jobId, int tier) => false;
}
