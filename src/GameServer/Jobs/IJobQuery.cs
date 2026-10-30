using Shared.Simulation.Jobs;

namespace GameServer.Jobs;

/// <summary>
/// What other systems may read of a character's jobs (spec 5.1; WP-M3-1): harvesting (R-5.2-01 "only players with the job"),
/// the forge, enchanting and gems. The committed view: a pick counts once its save committed. World thread only. Writes
/// go through <see cref="JobService"/> alone (architecture rule 4).
/// </summary>
public interface IJobQuery
{
    /// <summary>The character holds <paramref name="jobId"/> (a <c>JobDef</c> id) in either slot.</summary>
    bool HasJob(long characterId, string jobId);

    /// <summary>The job level of <paramref name="jobId"/>; 0 when the character does not hold it (or is not on this world).</summary>
    int JobLevel(long characterId, string jobId);

    /// <summary>The job in <paramref name="slot"/>; null when the slot is empty.</summary>
    string? JobIn(long characterId, JobSlot slot);
}
