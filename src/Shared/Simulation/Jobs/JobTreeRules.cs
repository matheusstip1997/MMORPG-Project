using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Shared.Simulation.Jobs
{
    /// <summary>
    /// The job skill-tree tables as runtime data (spec 5.1 R-5.1-04; <c>docs/design/job-trees.md</c>; WP-M3-7): the nodes by
    /// id and by job in table order, the row gates and the caps, built once from a validated config set. Read-only; shared
    /// by the server (<c>JobService</c>), the Jobs window and the bots, so every side checks a learn alike.
    /// </summary>
    public sealed class JobTreeRules
    {
        private static readonly IReadOnlyList<JobTree> NoNodes = Array.Empty<JobTree>();

        private readonly Dictionary<string, JobTree> _nodes = new Dictionary<string, JobTree>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<JobTree>> _byJob = new Dictionary<string, List<JobTree>>(StringComparer.Ordinal);
        private readonly int[] _caps = new int[byte.MaxValue + 1];

        private JobTreeRules(JobTreeSettings settings) => Settings = settings;

        public JobTreeSettings Settings { get; }

        /// <summary>Builds the rules from a validated config set; null when the job tree tables are not loaded.</summary>
        public static JobTreeRules? From(ConfigSet set)
        {
            var settings = JobTreeSettings.Find(set);
            if (settings == null || !set.TryGet(typeof(JobTree), out _))
                return null;
            var rules = new JobTreeRules(settings);
            foreach (var node in JobTree.Rows(set))
            {
                rules._nodes[node.Id] = node;
                if (!rules._byJob.TryGetValue(node.JobId, out var list))
                    rules._byJob[node.JobId] = list = new List<JobTree>();
                list.Add(node);
            }
            foreach (var cap in JobBonusCap.Rows(set))
                rules._caps[(byte)cap.Kind] = cap.Cap;
            return rules;
        }

        public JobTree? Node(string id) => _nodes.TryGetValue(id, out var node) ? node : null;

        /// <summary>The nodes of <paramref name="jobId"/>'s tree in table order (empty for a job without a tree).</summary>
        public IReadOnlyList<JobTree> Nodes(string jobId) => _byJob.TryGetValue(jobId, out var list) ? list : NoNodes;

        /// <summary>The rows <paramref name="jobId"/>'s tree has (the highest row + 1).</summary>
        public int RowCount(string jobId)
        {
            int rows = 0;
            foreach (var node in Nodes(jobId))
                rows = Math.Max(rows, node.Row + 1);
            return rows;
        }

        /// <summary>The job level that opens <paramref name="row"/> (decision 2).</summary>
        public int MinJobLevel(int row) => Settings.MinJobLevel(row);

        public bool RowOpen(int row, int jobLevel) => jobLevel >= MinJobLevel(row);

        /// <summary>The cap of <paramref name="kind"/> (<c>JobBonusCap</c>; 0 for a kind without a row).</summary>
        public int Cap(JobBonusKind kind) => _caps[(byte)kind];

        /// <summary>The capped bonus of <paramref name="jobId"/>'s learned nodes for one activity (decision 4).</summary>
        public int Bonus(string jobId, IReadOnlyDictionary<string, int> learned, JobBonusKind kind, JobActivity activity) =>
            kind == JobBonusKind.UnlockMoldTiers ? 0 : JobTreeMath.Capped(JobTreeMath.Sum(Nodes(jobId), learned, kind, activity), Cap(kind));

        /// <summary>The mold tiers <paramref name="jobId"/>'s learned unlock nodes open for making (decision 5).</summary>
        public uint MoldTiers(string jobId, IReadOnlyDictionary<string, int> learned) => JobTreeMath.MoldTiers(Nodes(jobId), learned);

        /// <summary>
        /// Why one more level of <paramref name="nodeId"/> cannot be learned in <paramref name="jobId"/>'s tree, or
        /// <see cref="JobTreeCheck.Ok"/> with its <paramref name="cost"/>. <paramref name="jobHeld"/>: the character holds the
        /// job (committed); <paramref name="jobLevel"/>, <paramref name="learned"/> and <paramref name="points"/> are that
        /// job's. Order: the node, the job, the row gate, the prerequisites, the max level, the points. Learning works any
        /// time (decision 10): combat and sessions are no refusal.
        /// </summary>
        public JobTreeCheck CheckLearn(string jobId, bool jobHeld, int jobLevel, IReadOnlyDictionary<string, int> learned, string nodeId,
            int points, out int cost)
        {
            cost = 0;
            var node = Node(nodeId);
            if (node == null || !string.Equals(node.JobId, jobId, StringComparison.Ordinal))
                return JobTreeCheck.UnknownNode;
            if (!jobHeld)
                return JobTreeCheck.JobRequired;
            if (!RowOpen(node.Row, jobLevel))
                return JobTreeCheck.RowLocked;
            for (int i = 0; i < node.Requires.Length && i < node.RequiresLevels.Length; i++)
            {
                if (!learned.TryGetValue(node.Requires[i], out int have) || have < node.RequiresLevels[i])
                    return JobTreeCheck.NodeLocked;
            }
            learned.TryGetValue(nodeId, out int current);
            if (current >= node.MaxLevel)
                return JobTreeCheck.Maxed;
            cost = current < node.CostPerLevel.Length ? node.CostPerLevel[current] : 1;
            return points < cost ? JobTreeCheck.NotEnoughPoints : JobTreeCheck.Ok;
        }

        /// <summary>
        /// A free respec of one job's tree (R-5.1-04, decisions 6 and 11): <paramref name="refund"/> is every point its
        /// learned nodes cost. Combat and sessions are the server's checks.
        /// </summary>
        public JobTreeCheck CheckRespec(string jobId, bool jobHeld, IReadOnlyDictionary<string, int> learned, out int refund)
        {
            refund = 0;
            if (!jobHeld)
                return JobTreeCheck.JobRequired;
            if (learned.Count == 0)
                return JobTreeCheck.NothingToRespec;
            refund = JobTreeMath.PointsSpent(Nodes(jobId), learned);
            return JobTreeCheck.Ok;
        }

        /// <summary>The reason code of a check (spec 9.7; the text comes from <c>ReasonCodeStrings</c>).</summary>
        public static JobReason ReasonOf(JobTreeCheck check) => check switch
        {
            JobTreeCheck.Ok => JobReason.None,
            JobTreeCheck.UnknownNode => JobReason.UnknownJobNode,
            JobTreeCheck.JobRequired => JobReason.JobRequired,
            JobTreeCheck.RowLocked => JobReason.JobRowLocked,
            JobTreeCheck.NodeLocked => JobReason.JobNodeLocked,
            JobTreeCheck.Maxed => JobReason.JobNodeMaxed,
            JobTreeCheck.NotEnoughPoints => JobReason.NotEnoughJobPoints,
            JobTreeCheck.NothingToRespec => JobReason.JobNothingToRespec,
            _ => JobReason.UnknownJobNode,
        };
    }

    /// <summary>The outcome of <see cref="JobTreeRules.CheckLearn"/> and <see cref="JobTreeRules.CheckRespec"/>; the server and the window map it to <c>JobReason</c>.</summary>
    public enum JobTreeCheck
    {
        Ok = 0,
        UnknownNode,
        JobRequired,
        RowLocked,
        NodeLocked,
        Maxed,
        NotEnoughPoints,
        NothingToRespec,
    }
}
