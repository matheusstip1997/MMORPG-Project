using Shared.Simulation.Jobs;

namespace GameServer.Persistence;

/// <summary>A learned job tree node and its level (<c>job_nodes</c>, WP-M3-7, R-5.1-04).</summary>
public sealed record JobNodeRow(string NodeId, int Level);

/// <summary>
/// One job a character holds (<c>job_progress</c>, spec 5.1): its slot, level, XP into the level, unspent job points and the
/// diminishing-returns window as JSON (<c>{"v":1,"recent":[[activity,subject],...]}</c>, oldest first), with the job tree's
/// learned nodes (<c>job_nodes</c>, WP-M3-7) in node id order.
/// </summary>
public sealed record JobProgressRow(string JobId, JobSlot Slot, int Level, long Xp, int Points, string RecentJson)
{
    public IReadOnlyList<JobNodeRow> Nodes { get; init; } = Array.Empty<JobNodeRow>();
}

/// <summary>
/// One committed job operation (<c>job_ops</c>): the audit row and, with <see cref="OpKey"/>, the idempotency key (rule 5).
/// The permanent pick (<see cref="JobOp.Pick"/>, with the starter items it granted), and the job tree's learn and respec
/// (WP-M3-7: <see cref="NodeId"/>, the node's levels before and after, or the levels a respec cleared, and the job's points
/// around it). <see cref="LevelAtPick"/> is the character level at the operation.
/// </summary>
public sealed record JobOpRecord(string OpKey, JobOp Op, string JobId, JobSlot Slot, int LevelAtPick, IReadOnlyList<string> StarterItems)
{
    public string NodeId { get; init; } = string.Empty;

    public int FromLevel { get; init; }

    public int ToLevel { get; init; }

    public int PointsBefore { get; init; }

    public int PointsAfter { get; init; }
}

/// <summary>What a <see cref="JobOpRecord"/> did. Stored as a smallint: append only.</summary>
public enum JobOp : short
{
    Pick = 1,
    /// <summary>One level of a job tree node (WP-M3-7).</summary>
    Learn = 2,
    /// <summary>A job's whole tree reset, every point back (WP-M3-7, R-5.1-04).</summary>
    Respec = 3,
}

/// <summary>
/// A character's job changes since its last save (WP-M3-1, WP-M3-7): every job record as it is now, its learned nodes
/// included (absolute, so a retried save writes the same), and the operations with their audit rows. Written in the same
/// transaction as the character row and the inventory batch, so a pick and its starter items commit together.
/// </summary>
public sealed record JobBatch(IReadOnlyList<JobProgressRow> Jobs, IReadOnlyList<JobOpRecord> Ops);

/// <summary>The jobs a login reads besides the character row.</summary>
public sealed record JobRecord(IReadOnlyList<JobProgressRow> Jobs)
{
    public static readonly JobRecord Empty = new(Array.Empty<JobProgressRow>());
}

/// <summary>
/// Hands a character's pending job batch to the save being issued and learns how that save ended. Implemented by
/// <c>Jobs/JobService</c>; called on the world thread by <see cref="SaveScheduler"/>.
/// </summary>
public interface IJobBatchSource
{
    /// <summary>The changes not yet in a save, now part of save <paramref name="saveId"/>; null when there are none.</summary>
    JobBatch? TakeBatch(long characterId, Guid saveId);

    /// <summary>Save <paramref name="saveId"/> committed: its batch is in the database.</summary>
    void Committed(long characterId, Guid saveId);
}
