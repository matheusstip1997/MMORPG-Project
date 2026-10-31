using Shared.Protocol;

namespace GameServer.Jobs;

/// <summary>
/// One player's job traffic (spec 5.1, WP-M3-1, WP-M3-7): whether its client needs a <see cref="JobState"/> and why, and the
/// answers to its pick and job tree requests (refusals at once, accepted operations once committed). World thread only.
/// </summary>
public sealed class JobAnswers
{
    /// <summary>Send a <see cref="JobState"/> in phase 10: a pick or tree operation committed, job XP changed, the job master opened, or the client is new.</summary>
    public bool Dirty { get; set; }

    /// <summary>The highest cause since the last send.</summary>
    public JobCause Cause { get; set; }

    /// <summary>States sent to this connection (the message's version).</summary>
    public uint Version { get; set; }

    internal List<JobResult> Results { get; } = new();

    internal List<JobTreeResult> TreeResults { get; } = new();

    /// <summary>Raises the cause of the next state.</summary>
    public void Mark(JobCause cause)
    {
        Dirty = true;
        if (cause > Cause)
            Cause = cause;
    }

    /// <summary>A new connection: the whole state again; answers for the old connection are dropped.</summary>
    public void Reset()
    {
        ClearAnswers();
        Dirty = true;
        Cause = JobCause.Snapshot;
        Version = 0;
    }

    public void ClearAnswers()
    {
        Results.Clear();
        TreeResults.Clear();
    }
}
