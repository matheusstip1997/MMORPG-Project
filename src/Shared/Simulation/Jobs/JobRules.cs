using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Simulation.Jobs
{
    /// <summary>
    /// The job tables as runtime data (spec 5.1; WP-M3-1): jobs by id and net id, the job level curve, the XP weights by
    /// activity and the pick rules, built once from a validated config set. Read-only; shared by the server, the Jobs window
    /// and the bots, so every side checks a pick alike.
    /// </summary>
    public sealed class JobRules
    {
        private readonly Dictionary<string, JobDef> _jobs = new Dictionary<string, JobDef>(StringComparer.Ordinal);
        private readonly Dictionary<int, JobDef> _byNetId = new Dictionary<int, JobDef>();
        private readonly JobXpWeights?[] _weights = new JobXpWeights?[byte.MaxValue + 1];
        private readonly List<JobDef> _ordered = new List<JobDef>();
        private readonly int[] _xpToNext;

        private readonly int[] _secondaryXpToNext;

        private JobRules(JobSettings settings, int[] xpToNext)
        {
            Settings = settings;
            _xpToNext = xpToNext;
            _secondaryXpToNext = JobMath.ScaleCurve(xpToNext, settings.SecondaryXpPercent);
        }

        public JobSettings Settings { get; }

        public int MaxLevel => Settings.MaxLevel;

        /// <summary>Every job in table order (the Jobs window lists them so).</summary>
        public IReadOnlyList<JobDef> Jobs => _ordered;

        /// <summary>The size of each job's saved recent window: the largest <c>JobXpWeights.repeatWindow</c> (at least 1).</summary>
        public int WindowCapacity { get; private set; } = 1;

        /// <summary>The job level curve, index level - 1 (levels 1 to <see cref="MaxLevel"/> - 1).</summary>
        public int[] XpToNextByLevel => _xpToNext;

        /// <summary>The job skill trees (WP-M3-7); null when their tables are not loaded.</summary>
        public JobTreeRules? Tree { get; private set; }

        /// <summary>Builds the rules from a validated config set; null when the job tables are not loaded or the curve does not fit the cap.</summary>
        public static JobRules? From(ConfigSet set)
        {
            var settings = JobSettings.Find(set);
            if (settings == null || !set.TryGet(typeof(JobDef), out var jobs) || !set.TryGet(typeof(JobLevelCurve), out var curve)
                || curve.Rows.Count != settings.MaxLevel - 1)
                return null;
            var xpToNext = new int[curve.Rows.Count];
            for (int i = 0; i < xpToNext.Length; i++)
                xpToNext[i] = ((JobLevelCurve)curve.Rows[i]).XpToNext;
            var rules = new JobRules(settings, xpToNext) { Tree = JobTreeRules.From(set) };
            foreach (var row in jobs.Rows)
            {
                var job = (JobDef)row;
                rules._jobs[job.Id] = job;
                rules._byNetId[job.NetId] = job;
                rules._ordered.Add(job);
            }
            if (set.TryGet(typeof(JobXpWeights), out var weights))
            {
                foreach (var row in weights.Rows)
                {
                    var weight = (JobXpWeights)row;
                    rules._weights[(byte)weight.Activity] = weight;
                    rules.WindowCapacity = Math.Max(rules.WindowCapacity, weight.RepeatWindow);
                }
            }
            return rules;
        }

        public JobDef? Find(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

        public JobDef? FindByNetId(int netId) => _byNetId.TryGetValue(netId, out var job) ? job : null;

        /// <summary>The XP weights of <paramref name="activity"/>; null when it pays no job XP.</summary>
        public JobXpWeights? WeightsOf(JobActivity activity) => _weights[(byte)activity];

        /// <summary>XP from job level <paramref name="level"/> to the next; 0 at the cap.</summary>
        public int XpToNext(int level) => level >= 1 && level < MaxLevel ? _xpToNext[level - 1] : 0;

        /// <summary>The secondary job's curve: <see cref="XpToNextByLevel"/> times <c>secondaryXpPercent</c> (decision 7a).</summary>
        public int[] SecondaryXpToNextByLevel => _secondaryXpToNext;

        /// <summary>XP from job level <paramref name="level"/> to the next in <paramref name="slot"/>; 0 at the cap.</summary>
        public int XpToNext(int level, JobSlot slot) =>
            level >= 1 && level < MaxLevel ? (slot == JobSlot.Secondary ? _secondaryXpToNext : _xpToNext)[level - 1] : 0;

        /// <summary>
        /// Why <paramref name="job"/> cannot fill <paramref name="slot"/> for a character at <paramref name="characterLevel"/>
        /// whose slots hold <paramref name="mainJobId"/> and <paramref name="secondaryJobId"/> (null when empty), or
        /// <see cref="JobPickCheck.Ok"/>. Spec 5.1 M3 decisions: each slot is written once, the main comes first (user
        /// decision 2026-10-30), the two jobs differ, the secondary needs <c>allowedAsSecondary</c>, and the level comes last.
        /// Combat, reach and bag room are the server's checks.
        /// </summary>
        public static JobPickCheck CheckPick(JobDef job, JobSlot slot, int characterLevel, string? mainJobId, string? secondaryJobId)
        {
            if (slot == JobSlot.Main && mainJobId != null)
                return JobPickCheck.AlreadyChosen;
            if (slot == JobSlot.Secondary)
            {
                if (secondaryJobId != null)
                    return JobPickCheck.AlreadyChosen;
                if (mainJobId == null)
                    return JobPickCheck.MainFirst;
                if (string.Equals(mainJobId, job.Id, StringComparison.Ordinal))
                    return JobPickCheck.SameAsMain;
                if (!job.AllowedAsSecondary)
                    return JobPickCheck.NotSecondary;
            }
            if (characterLevel < job.MinLevel)
                return JobPickCheck.LevelTooLow;
            return JobPickCheck.Ok;
        }
    }

    /// <summary>The outcome of <see cref="JobRules.CheckPick"/>; the server and the window map it to their reason codes.</summary>
    public enum JobPickCheck
    {
        Ok = 0,
        AlreadyChosen,
        MainFirst,
        SameAsMain,
        NotSecondary,
        LevelTooLow,
    }
}
