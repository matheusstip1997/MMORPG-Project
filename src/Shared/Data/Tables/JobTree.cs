using System;
using System.Collections.Generic;
using Shared.Simulation.Jobs;

namespace Shared.Data.Tables
{
    /// <summary>
    /// One node of a job's skill tree (spec 5.1 R-5.1-04; catalog 8.5; WP-M3-7). Hand-written from the design file
    /// <c>docs/design/job-trees.json</c> (user design talk 2026-10-30), which stays the source: <c>JobTreeDesignTests</c>
    /// fails when the two differ. Each job has its own nodes (decision 3: no shared trunk); a row opens at the job level of
    /// <see cref="JobTreeSettings.RowMinJobLevel"/> (decision 2); a link is a prerequisite: <see cref="Requires"/> learned to
    /// <see cref="RequiresLevels"/> first. Every number is a placeholder (balance is deferred).
    /// </summary>
    [Table("JobTree", 1)]
    public sealed class JobTree : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Longest node id in bytes: it travels as a string in <c>JobTreeRequest</c> and <c>JobState</c>.</summary>
        public const int MaxIdBytes = 48;

        public const int MaxLevelCap = 20;

        /// <summary>The node id, <c>job.nodeName</c> (the design file's id), stored in <c>job_nodes.node_id</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>The job whose tree holds the node.</summary>
        [Ref(typeof(JobDef))] public string JobId { get; set; } = string.Empty;

        [Ref(typeof(UiStrings))] public string NameId { get; set; } = string.Empty;

        /// <summary>One line on what the node does (the tab's details).</summary>
        [Ref(typeof(UiStrings))] public string TextId { get; set; } = string.Empty;

        /// <summary>The row, opened at <see cref="JobTreeSettings.RowMinJobLevel"/> of this index.</summary>
        [Range(0, 15)] public int Row { get; set; }

        /// <summary>Column in the tab's layout only.</summary>
        [Range(0, 15)] public int Col { get; set; }

        /// <summary>Nodes of the same tree to learn first, each at <see cref="RequiresLevels"/> of the same index.</summary>
        [Ref(typeof(JobTree))] public string[] Requires { get; set; } = Array.Empty<string>();

        public int[] RequiresLevels { get; set; } = Array.Empty<int>();

        [Range(1, MaxLevelCap)] public int MaxLevel { get; set; }

        /// <summary>Job points each level costs, index level - 1 (decision 1: 1 each).</summary>
        public int[] CostPerLevel { get; set; } = Array.Empty<int>();

        public JobBonusKind BonusKind { get; set; }

        /// <summary>The activities the bonus feeds (<c>JobXpWeights</c> activity ids).</summary>
        public JobActivity[] Activities { get; set; } = Array.Empty<JobActivity>();

        /// <summary>The bonus at each level, index level - 1 (the total, not the step); empty for <see cref="JobBonusKind.UnlockMoldTiers"/>.</summary>
        public int[] ValuePerLevel { get; set; } = Array.Empty<int>();

        /// <summary>For <see cref="JobBonusKind.UnlockMoldTiers"/>: the mold recipe tiers the node unlocks making (decision 5).</summary>
        public int[] MoldTiers { get; set; } = Array.Empty<int>();

        /// <summary>Highest mold tier a node may name (bits of a 32-bit set).</summary>
        public const int MaxMoldTier = 30;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            else if (!SkillTree.IsAsciiId(Id))
                errors.Add(nameof(Id), $"must be ASCII and at most {MaxIdBytes} bytes");
            if (CostPerLevel.Length != MaxLevel)
                errors.Add(nameof(CostPerLevel), $"has {CostPerLevel.Length} entries for {MaxLevel} levels");
            foreach (int cost in CostPerLevel)
            {
                if (cost < 1 || cost > 100)
                    errors.Add(nameof(CostPerLevel), $"{cost} is outside 1..100");
            }
            if (Requires.Length != RequiresLevels.Length)
                errors.Add(nameof(RequiresLevels), $"has {RequiresLevels.Length} levels for {Requires.Length} required nodes");
            for (int i = 0; i < RequiresLevels.Length; i++)
            {
                if (RequiresLevels[i] < 1)
                    errors.Add(nameof(RequiresLevels), $"level {RequiresLevels[i]} for '{(i < Requires.Length ? Requires[i] : "?")}' is below 1");
            }
            for (int i = 0; i < Requires.Length; i++)
            {
                if (Requires[i] == Id)
                    errors.Add(nameof(Requires), "a node cannot require itself");
                else if (Array.IndexOf(Requires, Requires[i]) != i)
                    errors.Add(nameof(Requires), $"lists '{Requires[i]}' twice");
            }
            if (BonusKind == JobBonusKind.None || !Enum.IsDefined(typeof(JobBonusKind), BonusKind))
                errors.Add(nameof(BonusKind), $"{(int)BonusKind} is not a job bonus kind");
            if (Activities.Length == 0)
                errors.Add(nameof(Activities), "must list at least one activity");
            for (int i = 0; i < Activities.Length; i++)
            {
                if (Activities[i] == JobActivity.None || !Enum.IsDefined(typeof(JobActivity), Activities[i]))
                    errors.Add(nameof(Activities), $"{(int)Activities[i]} is not a job activity");
                else if (Array.IndexOf(Activities, Activities[i]) != i)
                    errors.Add(nameof(Activities), $"lists {Activities[i]} twice");
            }
            if (BonusKind == JobBonusKind.UnlockMoldTiers)
            {
                // Decision 5: an unlock is one level that names the tiers it opens.
                if (MaxLevel != 1)
                    errors.Add(nameof(MaxLevel), "an unlockMoldTiers node has max level 1");
                if (MoldTiers.Length == 0)
                    errors.Add(nameof(MoldTiers), "an unlockMoldTiers node names the tiers it unlocks");
                if (ValuePerLevel.Length != 0)
                    errors.Add(nameof(ValuePerLevel), "an unlockMoldTiers node has no values (its tiers are MoldTiers)");
                foreach (int tier in MoldTiers)
                {
                    if (tier < 1 || tier > MaxMoldTier)
                        errors.Add(nameof(MoldTiers), $"tier {tier} is outside 1..{MaxMoldTier}");
                }
            }
            else
            {
                if (MoldTiers.Length != 0)
                    errors.Add(nameof(MoldTiers), $"only an unlockMoldTiers node names tiers, not a {BonusKind} node");
                if (ValuePerLevel.Length != MaxLevel)
                    errors.Add(nameof(ValuePerLevel), $"has {ValuePerLevel.Length} entries for {MaxLevel} levels");
                for (int i = 0; i < ValuePerLevel.Length; i++)
                {
                    if (ValuePerLevel[i] < 0 || ValuePerLevel[i] > 10_000)
                        errors.Add(nameof(ValuePerLevel), $"{ValuePerLevel[i]} is outside 0..10000");
                    else if (i > 0 && ValuePerLevel[i] < ValuePerLevel[i - 1])
                        errors.Add(nameof(ValuePerLevel), $"level {i + 1} gives less than level {i}");
                }
            }
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var settings = JobTreeSettings.Find(set);
            if (settings != null && Row >= settings.RowMinJobLevel.Length)
                errors.Add(nameof(Row), $"row {Row} has no JobTreeSettings.rowMinJobLevel entry ({settings.RowMinJobLevel.Length} rows)");

            var rows = Rows(set);
            int index = rows.IndexOf(this);
            for (int i = 0; i < rows.Count; i++)
            {
                var other = rows[i];
                if (i < index && other.JobId == JobId && other.Row == Row && other.Col == Col)
                    errors.Add(nameof(Col), $"'{other.Id}' already sits at row {Row}, column {Col} of the {JobId} tree");
            }

            // Decision 2: a prerequisite is a link inside one tree, in an earlier or the same row, at a level it has.
            for (int i = 0; i < Requires.Length && i < RequiresLevels.Length; i++)
            {
                var required = Find(set, Requires[i]);
                if (required == null)
                    continue;
                if (required.JobId != JobId)
                    errors.Add(nameof(Requires), $"'{required.Id}' is in the {required.JobId} tree, not the {JobId} tree (decision 3)");
                if (required.Row > Row)
                    errors.Add(nameof(Requires), $"'{required.Id}' is on row {required.Row}, after this node's row {Row}");
                if (RequiresLevels[i] > required.MaxLevel)
                    errors.Add(nameof(RequiresLevels), $"'{required.Id}' at level {RequiresLevels[i]} is above its max level {required.MaxLevel}");
            }
            if (RequiresCycle(set, this))
                errors.Add(nameof(Requires), $"'{Id}' requires itself through its prerequisites");

            // Decision 4: every bonus kind is capped on the sum of its sources (unlocks are a tier set, not a number).
            if (BonusKind != JobBonusKind.UnlockMoldTiers && BonusKind != JobBonusKind.None && set.TryGet(typeof(JobBonusCap), out _)
                && JobBonusCap.Find(set, BonusKind) == null)
                errors.Add(nameof(BonusKind), $"{BonusKind} has no JobBonusCap row");
        }

        public static JobTree? Find(ConfigSet set, string id) => NetIds.Find<JobTree>(set, id, row => row.Id);

        public static List<JobTree> Rows(ConfigSet set) => NetIds.Rows<JobTree>(set);

        private static bool RequiresCycle(ConfigSet set, JobTree start)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<JobTree>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                foreach (var id in node.Requires)
                {
                    if (id == start.Id)
                        return true;
                    var next = Find(set, id);
                    if (next != null && seen.Add(id))
                        stack.Push(next);
                }
            }
            return false;
        }
    }
}
