namespace GameServer.Jobs;

/// <summary>
/// A job activity in progress that read the job tree's bonuses when it started (decision 10 of the job-tree design: it
/// keeps them), so a respec of the tree is refused while it runs (WP-M3-7, <c>JobActivityRunning</c>). Harvesting's
/// channel (WP-M3-5) and the forge's session register one through <c>GameWorld.JobActivityGates</c>. World thread only.
/// </summary>
public interface IJobActivityGate
{
    /// <summary>The character has an activity of this kind in progress (a harvest asked for or channelling, a forge session).</summary>
    bool InSession(long characterId);
}
