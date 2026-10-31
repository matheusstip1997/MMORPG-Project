using Shared.Simulation.Jobs;

namespace GameServer.Jobs;

/// <summary>
/// The craft bonuses a character's job skill tree gives (spec 5.1 R-5.1-04: "skill-tree bonuses are computed when an
/// activity starts and passed into the quality/odds formulas"), read by harvesting (WP-M3-5) and the forge, enchanting and
/// gems. The job trees (WP-M3-7) implement it through <see cref="JobService"/>, the only writer of job progress; until then
/// <see cref="NoJobBonuses"/> gives none. An activity reads it once when it starts and keeps what it read (decision 10: a
/// respec is refused during a session, <see cref="IJobActivityGate"/>). World thread only. Exposed as
/// <c>GameWorld.JobBonusQuery</c>.
/// </summary>
public interface IJobBonusQuery
{
    /// <summary>
    /// The job tree's bonus of <paramref name="kind"/> for <paramref name="activity"/> of <paramref name="jobId"/> (a
    /// <c>JobDef</c> id), from committed nodes only and capped by the tree's cap, in percent or percentage points as the kind
    /// says; 0 when none or when the character does not hold the job.
    /// </summary>
    int Get(long characterId, string jobId, JobBonusKind kind, JobActivity activity);

    /// <summary>Master Molds (<see cref="JobBonusKind.UnlockMoldTiers"/>): the job may make mold recipes of <paramref name="tier"/>.</summary>
    bool MoldTierUnlocked(long characterId, string jobId, int tier);
}

/// <summary>No job tree bonuses (the stub until the job skill trees, WP-M3-7).</summary>
public sealed class NoJobBonuses : IJobBonusQuery
{
    public static readonly NoJobBonuses Instance = new();

    private NoJobBonuses()
    {
    }

    public int Get(long characterId, string jobId, JobBonusKind kind, JobActivity activity) => 0; // TODO(WP-M3-7): the tree

    public bool MoldTierUnlocked(long characterId, string jobId, int tier) => false; // TODO(WP-M3-7): the tree
}
