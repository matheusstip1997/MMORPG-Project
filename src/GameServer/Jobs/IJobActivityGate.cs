namespace GameServer.Jobs;

/// <summary>
/// A job activity that keeps the job tree bonuses it read at its start (the job-tree design, decision 10): while any gate says
/// a character is in a session, the job tree refuses a respec (<c>JobActivityRunning</c>, WP-M3-7). Registered with
/// <c>GameWorld.JobActivityGates</c>; the forge (WP-M3-6) and harvesting (WP-M3-5) implement it. World thread only.
/// (WP-M3-7's shape; whichever WP merges second keeps one file.)
/// </summary>
public interface IJobActivityGate
{
    bool InSession(long characterId);
}
