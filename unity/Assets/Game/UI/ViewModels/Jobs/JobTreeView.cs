#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Jobs;

namespace Game.UI.ViewModels.Jobs
{
    /// <summary>How a job tree node shows in the tab (WP-M3-7).</summary>
    public enum JobNodeState
    {
        /// <summary>Its row opens at a higher job level (decision 2).</summary>
        RowLocked,
        /// <summary>A prerequisite is not learned to its level yet.</summary>
        Locked,
        /// <summary>Open and not learned.</summary>
        Open,
        /// <summary>Learned, below its max level.</summary>
        Learned,
        Maxed,
    }

    /// <summary>A link of the tab: <see cref="From"/> to <see cref="Level"/> is needed before <see cref="To"/>.</summary>
    public readonly struct JobTreeLink
    {
        public JobTreeLink(JobTree from, JobTree to, int level)
        {
            From = from;
            To = to;
            Level = level;
        }

        public JobTree From { get; }
        public JobTree To { get; }
        public int Level { get; }
    }

    /// <summary>
    /// One line of the active bonuses summary (decision 8): a bonus kind's capped value for the activities that get it (the
    /// value the server's <c>IJobBonusQuery</c> returns), or the mold tiers an unlock opens.
    /// </summary>
    public readonly struct JobBonusLine
    {
        public JobBonusLine(JobBonusKind kind, int value, IReadOnlyList<JobActivity> activities, int cap, uint tiers)
        {
            Kind = kind;
            Value = value;
            Activities = activities;
            Cap = cap;
            Tiers = tiers;
        }

        public JobBonusKind Kind { get; }
        public int Value { get; }
        public IReadOnlyList<JobActivity> Activities { get; }
        public int Cap { get; }

        /// <summary>For <see cref="JobBonusKind.UnlockMoldTiers"/>: the tiers as bits (<see cref="JobTreeMath.HasTier"/>).</summary>
        public uint Tiers { get; }
    }

    /// <summary>
    /// One held job's tree as the tab shows it (spec 5.1, 9.13; WP-M3-7; decision 8), from the committed state the server sent
    /// (<see cref="JobEntry.Nodes"/> and <see cref="JobEntry.Points"/>) and the shared <see cref="JobTreeRules"/>, so the tab
    /// and the server agree: the rows with their gates, the links, each node's state and level, what a learn would do, and the
    /// summary of the active bonuses with their caps. Display only; the server decides every learn and respec.
    /// </summary>
    public sealed class JobTreeView
    {
        private readonly JobTreeRules _rules;

        public JobTreeView(JobTreeRules rules, JobDef job, JobEntry held)
        {
            _rules = rules;
            Job = job;
            Held = held;
            var learned = new Dictionary<string, int>(held.Nodes.Length, StringComparer.Ordinal);
            foreach (var node in held.Nodes)
                learned[node.NodeId] = node.Level;
            Learned = learned;
            Nodes = rules.Nodes(job.Id);
            RowCount = rules.RowCount(job.Id);
            var columns = new SortedSet<int>();
            foreach (var node in Nodes)
                columns.Add(node.Col);
            Columns = new List<int>(columns);
        }

        public JobDef Job { get; }
        public JobEntry Held { get; }
        public int Level => Held.Level;
        public int Points => Held.Points;
        public IReadOnlyDictionary<string, int> Learned { get; }
        public IReadOnlyList<JobTree> Nodes { get; }
        public int RowCount { get; }

        /// <summary>The columns the tree uses, in order (the tab drops empty ones).</summary>
        public IReadOnlyList<int> Columns { get; }

        public bool AnythingLearned => Learned.Count > 0;

        public int LevelOf(string nodeId) => Learned.TryGetValue(nodeId, out int level) ? level : 0;

        /// <summary>The job level that opens <paramref name="row"/>.</summary>
        public int RowGate(int row) => _rules.MinJobLevel(row);

        public bool RowOpen(int row) => _rules.RowOpen(row, Level);

        /// <summary>The server's own check of one more level of <paramref name="nodeId"/> now.</summary>
        public JobTreeCheck Check(string nodeId) => _rules.CheckLearn(Job.Id, true, Level, Learned, nodeId, Points, out _);

        /// <summary>The reason code of <see cref="Check"/>; null when it can be learned.</summary>
        public string? ReasonCode(string nodeId)
        {
            var check = Check(nodeId);
            return check == JobTreeCheck.Ok ? null : JobTreeRules.ReasonOf(check).ToString();
        }

        public JobNodeState StateOf(JobTree node)
        {
            int level = LevelOf(node.Id);
            if (level >= node.MaxLevel)
                return JobNodeState.Maxed;
            if (level > 0)
                return JobNodeState.Learned;
            if (!RowOpen(node.Row))
                return JobNodeState.RowLocked;
            for (int i = 0; i < node.Requires.Length && i < node.RequiresLevels.Length; i++)
            {
                if (LevelOf(node.Requires[i]) < node.RequiresLevels[i])
                    return JobNodeState.Locked;
            }
            return JobNodeState.Open;
        }

        /// <summary>The next level's cost; 0 at the max.</summary>
        public int CostOf(JobTree node)
        {
            int level = LevelOf(node.Id);
            return level < node.MaxLevel && level < node.CostPerLevel.Length ? node.CostPerLevel[level] : 0;
        }

        /// <summary>Every prerequisite link of the tree.</summary>
        public IReadOnlyList<JobTreeLink> Links()
        {
            var links = new List<JobTreeLink>();
            foreach (var node in Nodes)
            {
                for (int i = 0; i < node.Requires.Length && i < node.RequiresLevels.Length; i++)
                {
                    if (_rules.Node(node.Requires[i]) is { } from)
                        links.Add(new JobTreeLink(from, node, node.RequiresLevels[i]));
                }
            }
            return links;
        }

        /// <summary>
        /// The job's active bonuses (decision 8): per bonus kind in tree order, the capped value per activity, activities with
        /// the same value on one line; then the mold tiers an unlock opens. Kinds with nothing learned are left out.
        /// </summary>
        public IReadOnlyList<JobBonusLine> Summary()
        {
            var lines = new List<JobBonusLine>();
            var kinds = new List<JobBonusKind>();
            foreach (var node in Nodes)
            {
                if (!kinds.Contains(node.BonusKind))
                    kinds.Add(node.BonusKind);
            }
            foreach (var kind in kinds)
            {
                if (kind == JobBonusKind.UnlockMoldTiers)
                {
                    uint tiers = _rules.MoldTiers(Job.Id, Learned);
                    if (tiers != 0)
                        lines.Add(new JobBonusLine(kind, 0, Array.Empty<JobActivity>(), 0, tiers));
                    continue;
                }
                var values = new List<int>();
                var groups = new List<List<JobActivity>>();
                foreach (var activity in ActivitiesOf(kind))
                {
                    int value = _rules.Bonus(Job.Id, Learned, kind, activity);
                    if (value <= 0)
                        continue;
                    int index = values.IndexOf(value);
                    if (index < 0)
                    {
                        values.Add(value);
                        groups.Add(new List<JobActivity> { activity });
                    }
                    else
                    {
                        groups[index].Add(activity);
                    }
                }
                for (int i = 0; i < values.Count; i++)
                    lines.Add(new JobBonusLine(kind, values[i], groups[i], _rules.Cap(kind), 0));
            }
            return lines;
        }

        private List<JobActivity> ActivitiesOf(JobBonusKind kind)
        {
            var activities = new List<JobActivity>();
            foreach (var node in Nodes)
            {
                if (node.BonusKind != kind)
                    continue;
                foreach (var activity in node.Activities)
                {
                    if (!activities.Contains(activity))
                        activities.Add(activity);
                }
            }
            return activities;
        }
    }
}
