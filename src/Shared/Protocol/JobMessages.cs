using System;
using MemoryPack;
using Shared.Data.Tables;
using Shared.Simulation.Jobs;

namespace Shared.Protocol
{
    /// <summary>
    /// Why the server refused a <see cref="JobRequest"/> (spec 5.1 M3 decisions, spec 9.7 reason codes; WP-M3-1). Append
    /// only; <see cref="ReliableMessages.LastJobReason"/> is the highest. <see cref="InCombat"/> and <see cref="BagFull"/>
    /// share their <c>ReasonCodeStrings</c> rows with the inventory reasons of the same name.
    /// </summary>
    public enum JobReason : byte
    {
        None = 0,
        /// <summary>The job tables or the character's jobs are not loaded on this world (yet).</summary>
        JobsUnavailable = 1,
        /// <summary>No <c>JobDef</c> has that net id, or the slot is not main or secondary.</summary>
        UnknownJob = 2,
        /// <summary>The job master is not visible, does not offer jobs, or is out of interaction range.</summary>
        JobMasterOutOfReach = 3,
        /// <summary>The character is below the job's <c>JobDef.minLevel</c>.</summary>
        JobLevelTooLow = 4,
        /// <summary>The slot already holds a job: a pick is permanent (user decision 2026-10-30).</summary>
        JobAlreadyChosen = 5,
        /// <summary>The job may not be a secondary job (<c>JobDef.allowedAsSecondary</c>).</summary>
        JobNotSecondary = 6,
        /// <summary>The secondary job can only be picked once the main job is (user decision 2026-10-30).</summary>
        JobMainFirst = 7,
        /// <summary>The secondary job would be the main job again.</summary>
        JobSameAsMain = 8,
        /// <summary>In combat or during an action (spec 5.1: the pick happens out of combat).</summary>
        InCombat = 9,
        /// <summary>The starter items do not all fit (user decision 2026-10-30: the pick is refused, nothing is written).</summary>
        BagFull = 10,
        /// <summary>A job tree request for a job the character does not hold (WP-M3-7, protocol 32).</summary>
        JobRequired = 11,
        /// <summary>No <c>JobTree</c> node has that id, or it is not in the named job's tree.</summary>
        UnknownJobNode = 12,
        /// <summary>The node's row opens at a higher job level (<c>JobTreeSettings.rowMinJobLevel</c>, decision 2).</summary>
        JobRowLocked = 13,
        /// <summary>A required node is not learned at its required level (decision 2).</summary>
        JobNodeLocked = 14,
        /// <summary>The node is at its max level.</summary>
        JobNodeMaxed = 15,
        /// <summary>Not enough unspent job points for the node's next level.</summary>
        NotEnoughJobPoints = 16,
        /// <summary>A respec of a tree with nothing learned.</summary>
        JobNothingToRespec = 17,
        /// <summary>A respec during a harvest or forge session (decision 6: a running session keeps its bonuses).</summary>
        JobActivityRunning = 18,
    }

    /// <summary>Why a <see cref="JobState"/> was sent. Append only.</summary>
    public enum JobCause : byte
    {
        /// <summary>Entering the world, or the state the job master shows when its window opens.</summary>
        Snapshot = 0,
        /// <summary>A committed pick.</summary>
        Picked = 1,
        /// <summary>Job XP, a job level-up or job points changed.</summary>
        Progress = 2,
        /// <summary>A committed job tree learn or respec (WP-M3-7).</summary>
        TreeChanged = 3,
    }

    /// <summary>What a <see cref="JobTreeRequest"/> asks for (WP-M3-7). On the wire: append only.</summary>
    public enum JobTreeOp : byte
    {
        None = 0,
        /// <summary>One more level of a node (the first level learns it).</summary>
        Learn = 1,
        /// <summary>Reset one job's tree: every point back at once, free (R-5.1-04, decisions 6 and 11).</summary>
        Respec = 2,
    }

    /// <summary>A learned job tree node and its level (WP-M3-7).</summary>
    [MemoryPackable]
    public sealed partial class JobNodeEntry
    {
        /// <summary>The <c>JobTree</c> id.</summary>
        public string NodeId { get; set; } = string.Empty;

        public byte Level { get; set; }
    }

    /// <summary>One job the character holds.</summary>
    [MemoryPackable]
    public sealed partial class JobEntry
    {
        /// <summary>The <c>JobDef.netId</c>.</summary>
        public byte JobNetId { get; set; }

        public JobSlot Slot { get; set; }

        public ushort Level { get; set; }

        /// <summary>XP into the current level.</summary>
        public uint Xp { get; set; }

        /// <summary>XP from this level to the next; 0 at the job level cap.</summary>
        public uint XpToNext { get; set; }

        /// <summary>Unspent job points (spent by the job skill tree, WP-M3-7), as the committed tree leaves them.</summary>
        public ushort Points { get; set; }

        /// <summary>The job tree's committed learned nodes (WP-M3-7, protocol 32), in node id order.</summary>
        public JobNodeEntry[] Nodes { get; set; } = Array.Empty<JobNodeEntry>();
    }

    /// <summary>
    /// Server to client, reliable (spec 5.1, 9.13; WP-M3-1, protocol 28): the observer's jobs. Sent at enter world, when the
    /// job master's window opens, after a committed pick (right after its <see cref="JobResult"/>) and when job XP changed.
    /// MemoryPack after the id byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class JobState
    {
        /// <summary>Counts per connection; a newer state replaces an older one.</summary>
        public uint Version { get; set; }

        public JobEntry[] Jobs { get; set; } = Array.Empty<JobEntry>();

        public JobCause Cause { get; set; }
    }

    /// <summary>
    /// Client to server, reliable (WP-M3-1, protocol 28): pick <see cref="JobNetId"/> for <see cref="Slot"/> at the job
    /// master <see cref="NpcEntityId"/>. Permanent; the window confirms first. <see cref="RequestId"/> counts per
    /// connection: a repeat gets the first answer and changes nothing. Capped by its <c>ReliableMessageCaps</c> row.
    /// </summary>
    [MemoryPackable]
    public sealed partial class JobRequest
    {
        public uint RequestId { get; set; }

        public uint NpcEntityId { get; set; }

        public byte JobNetId { get; set; }

        public JobSlot Slot { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M3-1, protocol 28): the answer to a <see cref="JobRequest"/>. A refusal comes at once;
    /// an accepted pick only once its save committed (rule 5), right before the <see cref="JobState"/> that shows it.
    /// </summary>
    [MemoryPackable]
    public sealed partial class JobResult
    {
        public uint RequestId { get; set; }

        public JobSlot Slot { get; set; }

        public byte JobNetId { get; set; }

        public JobReason Reason { get; set; }
    }

    /// <summary>
    /// Client to server, reliable (WP-M3-7, protocol 32): learn one level of <see cref="NodeId"/> in the tree of
    /// <see cref="JobNetId"/>, or respec that job's tree (<see cref="NodeId"/> empty; the window confirms first). Works
    /// anywhere (the Jobs window); a respec is refused in combat and during a harvest or forge session. <see cref="RequestId"/>
    /// counts per connection: a repeat gets the first answer and changes nothing. Capped by its <c>ReliableMessageCaps</c> row.
    /// </summary>
    [MemoryPackable]
    public sealed partial class JobTreeRequest
    {
        public uint RequestId { get; set; }

        public JobTreeOp Op { get; set; }

        public byte JobNetId { get; set; }

        public string NodeId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Server to client, reliable (WP-M3-7, protocol 32): the answer to a <see cref="JobTreeRequest"/>. A refusal comes at
    /// once; an applied learn or respec only once its save committed (rule 5), right before the <see cref="JobState"/> that
    /// shows it.
    /// </summary>
    [MemoryPackable]
    public sealed partial class JobTreeResult
    {
        public uint RequestId { get; set; }

        public JobTreeOp Op { get; set; }

        public byte JobNetId { get; set; }

        public string NodeId { get; set; } = string.Empty;

        public JobReason Reason { get; set; }

        /// <summary>The node's level after a learn; 0 for a respec or a refusal.</summary>
        public byte Level { get; set; }

        /// <summary>The job's unspent points after the operation; 0 for a refusal.</summary>
        public ushort Points { get; set; }
    }

    public static partial class ReliableMessages
    {
        /// <summary>Jobs a state carries at most (the eight jobs of R-5.1-02).</summary>
        public const int MaxJobEntries = 8;

        /// <summary>Learned nodes one job carries at most (a tree has about 9, decision 1).</summary>
        public const int MaxJobTreeNodes = 32;

        public const JobSlot LastJobSlot = JobSlot.Secondary;
        public const JobReason LastJobReason = JobReason.JobActivityRunning;
        public const JobCause LastJobCause = JobCause.TreeChanged;
        public const JobTreeOp LastJobTreeOp = JobTreeOp.Respec;

        public static bool TryRead(ReadOnlySpan<byte> message, out JobState state)
        {
            state = null!;
            if (!TryBody(message, ReliableMessageId.JobState, out JobState? value) || value == null || value.Cause > LastJobCause
                || value.Jobs == null || value.Jobs.Length > MaxJobEntries)
                return false;
            foreach (var job in value.Jobs)
            {
                if (job == null || job.JobNetId == 0 || job.Slot == JobSlot.None || job.Slot > LastJobSlot || job.Level == 0
                    || job.Nodes == null || job.Nodes.Length > MaxJobTreeNodes)
                    return false;
                foreach (var node in job.Nodes)
                {
                    if (node == null || node.Level == 0 || !SkillTree.IsAsciiId(node.NodeId ?? string.Empty))
                        return false;
                }
            }
            state = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out JobTreeRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.JobTreeRequest, out JobTreeRequest? value) || value == null || value.JobNetId == 0
                || value.Op == JobTreeOp.None || value.Op > LastJobTreeOp || value.NodeId == null
                || (value.NodeId.Length != 0 && !SkillTree.IsAsciiId(value.NodeId)))
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out JobTreeResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.JobTreeResult, out JobTreeResult? value) || value == null
                || value.Op > LastJobTreeOp || value.Reason > LastJobReason || value.NodeId == null)
                return false;
            result = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out JobRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.JobRequest, out JobRequest? value) || value == null || value.JobNetId == 0
                || value.Slot == JobSlot.None || value.Slot > LastJobSlot)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out JobResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.JobResult, out JobResult? value) || value == null || value.Slot > LastJobSlot
                || value.Reason > LastJobReason)
                return false;
            result = value;
            return true;
        }
    }
}
