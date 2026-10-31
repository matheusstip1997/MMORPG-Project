using System;
using System.Collections.Generic;
using Shared.Data.Tables;

namespace Shared.Simulation.Jobs
{
    /// <summary>
    /// The job skill-tree bonuses as pure integer math (spec 5.1 R-5.1-04; <c>docs/design/job-trees.md</c> decision 4;
    /// WP-M3-7; architecture rules 2 and 6): a node's value at a level, the sum of a job's learned nodes of one bonus kind
    /// that feed one activity, capped by <c>JobBonusCap</c>, and the mold tiers a job's unlock nodes open. Shared by the
    /// server's <c>IJobBonusQuery</c>, the Jobs window and the bots, so every side shows the same number.
    /// </summary>
    public static class JobTreeMath
    {
        /// <summary>The bonus of <paramref name="node"/> at <paramref name="level"/> (0 when not learned; the max level's past it).</summary>
        public static int ValueAt(JobTree node, int level)
        {
            if (level <= 0 || node.ValuePerLevel.Length == 0)
                return 0;
            return node.ValuePerLevel[Math.Min(level, node.ValuePerLevel.Length) - 1];
        }

        /// <summary>The bonus at the next level; null when <paramref name="level"/> is the max.</summary>
        public static int? NextValue(JobTree node, int level) => level >= node.MaxLevel ? (int?)null : ValueAt(node, level + 1);

        /// <summary>The node's bonus feeds <paramref name="activity"/>.</summary>
        public static bool Feeds(JobTree node, JobActivity activity) => Array.IndexOf(node.Activities, activity) >= 0;

        /// <summary>
        /// The sum over <paramref name="nodes"/> (one job's tree) of the learned nodes of <paramref name="kind"/> that feed
        /// <paramref name="activity"/>, before the cap. <paramref name="learned"/> maps node ids to levels.
        /// </summary>
        public static int Sum(IReadOnlyList<JobTree> nodes, IReadOnlyDictionary<string, int> learned, JobBonusKind kind, JobActivity activity)
        {
            long sum = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.BonusKind != kind || !Feeds(node, activity) || !learned.TryGetValue(node.Id, out int level))
                    continue;
                sum += ValueAt(node, level);
            }
            return (int)Math.Min(int.MaxValue, sum);
        }

        /// <summary>The capped bonus: <see cref="Sum"/>, at most <paramref name="cap"/> (decision 4: a cap on the sum of its sources).</summary>
        public static int Capped(int sum, int cap) => Math.Max(0, Math.Min(sum, cap));

        /// <summary>The mold tiers the learned unlock nodes open, as bits (bit t set: tier t); 0 when none.</summary>
        public static uint MoldTiers(IReadOnlyList<JobTree> nodes, IReadOnlyDictionary<string, int> learned)
        {
            uint tiers = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.BonusKind != JobBonusKind.UnlockMoldTiers || !learned.TryGetValue(node.Id, out int level) || level <= 0)
                    continue;
                foreach (int tier in node.MoldTiers)
                {
                    if (tier >= 1 && tier <= JobTree.MaxMoldTier)
                        tiers |= 1u << tier;
                }
            }
            return tiers;
        }

        /// <summary><paramref name="tier"/> is in the set <see cref="MoldTiers"/> returned.</summary>
        public static bool HasTier(uint tiers, int tier) => tier >= 1 && tier <= JobTree.MaxMoldTier && (tiers & (1u << tier)) != 0;

        /// <summary>The job points one job's learned nodes cost at today's <c>CostPerLevel</c> (what a respec gives back).</summary>
        public static int PointsSpent(IReadOnlyList<JobTree> nodes, IReadOnlyDictionary<string, int> learned)
        {
            int spent = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (!learned.TryGetValue(node.Id, out int level))
                    continue;
                for (int l = 0; l < level && l < node.CostPerLevel.Length; l++)
                    spent += node.CostPerLevel[l];
            }
            return spent;
        }
    }
}
