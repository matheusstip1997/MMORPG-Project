#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Jobs;

namespace Game.UI.ViewModels.Jobs
{
    /// <summary>A job that reached a higher job level in the latest state (for the news toast).</summary>
    public readonly struct JobNews
    {
        public JobNews(byte jobNetId, int level)
        {
            JobNetId = jobNetId;
            Level = level;
        }

        public byte JobNetId { get; }
        public int Level { get; }
    }

    /// <summary>
    /// The Jobs window (spec 5.1, 9.13; WP-M3-1): the last <see cref="JobState"/> the server sent (the held jobs with level, XP
    /// and points), the character level, and, while it is open at a job master (<see cref="NpcEntityId"/>), the pick section:
    /// which picks the rules allow (<see cref="JobRules.CheckPick"/>, the server's own check), the pick waiting for the
    /// player's confirmation (a pick is permanent, user decision 2026-10-30) and the one waiting for its answer. Display only:
    /// the server decides every pick; a refusal comes back as its reason code.
    /// <para>
    /// The job tree tabs (WP-M3-7, decision 8): one per held job, its tree as a <see cref="JobTreeView"/>, the selected node,
    /// the one learn or respec waiting for its answer, and the respec confirmation. Learning works anywhere (decision 10).
    /// </para>
    /// </summary>
    public sealed class JobsWindowViewModel : ViewModel
    {
        private readonly JobRules? _rules;
        private readonly List<JobNews> _news = new List<JobNews>();

        public JobsWindowViewModel(JobRules? rules)
        {
            _rules = rules;
        }

        public bool Open { get; private set; }

        /// <summary>The job master the window was opened at (its pick section shows); 0 when opened by the key.</summary>
        public uint NpcEntityId { get; private set; }

        public bool AtJobMaster => NpcEntityId != 0;

        public JobState? State { get; private set; }

        /// <summary>The character level (the pick's <c>JobDef.minLevel</c>); 1 before the first progression state.</summary>
        public int Level { get; private set; } = 1;

        /// <summary>The jobs the tables have, in table order (the pick section lists them).</summary>
        public IReadOnlyList<JobDef> Jobs => _rules?.Jobs ?? (IReadOnlyList<JobDef>)Array.Empty<JobDef>();

        public int MaxLevel => _rules?.MaxLevel ?? 0;

        /// <summary>The pick waiting for the player's confirmation; null when none.</summary>
        public (byte JobNetId, JobSlot Slot)? Confirming { get; private set; }

        /// <summary>The pick waiting for its answer; 0 for none (one at a time, so the view never runs ahead of the server).</summary>
        public uint Pending { get; private set; }

        // ---- the job tree tabs (WP-M3-7; decision 8)

        /// <summary>The tab shown: 0 the jobs list, else the <c>JobDef.netId</c> of a held job whose tree shows.</summary>
        public byte Tab { get; private set; }

        /// <summary>The node selected in the tree tab (its details and Learn); null when none.</summary>
        public string? SelectedNode { get; private set; }

        /// <summary>The tree request waiting for its answer; 0 for none (one at a time).</summary>
        public uint TreePending { get; private set; }

        /// <summary>The tab's Respec waits for the player's confirmation (decision 6).</summary>
        public bool ConfirmingRespec { get; private set; }

        /// <summary>The held jobs in slot order (one tree tab each).</summary>
        public IReadOnlyList<JobEntry> HeldJobs
        {
            get
            {
                var held = new List<JobEntry>();
                if (InSlot(JobSlot.Main) is { } main)
                    held.Add(main);
                if (InSlot(JobSlot.Secondary) is { } secondary)
                    held.Add(secondary);
                return held;
            }
        }

        /// <summary>The tree of the tab's job; null on the jobs list or without the tree tables.</summary>
        public JobTreeView? Tree => Tab != 0 && _rules?.Tree is { } tree && _rules.FindByNetId(Tab) is { } job && Held(Tab) is { } held
            ? new JobTreeView(tree, job, held)
            : null;

        /// <summary>Shows the jobs list (0) or a held job's tree; a selection and a respec confirmation go.</summary>
        public void SelectTab(byte jobNetId)
        {
            if (jobNetId != 0 && Held(jobNetId) == null)
                return;
            Tab = jobNetId;
            SelectedNode = null;
            ConfirmingRespec = false;
            NotifyChanged();
        }

        /// <summary>Selects a node of the tab's tree.</summary>
        public void SelectNode(string nodeId)
        {
            if (Tree is not { } tree || _rules?.Tree?.Node(nodeId) is not { } node || node.JobId != tree.Job.Id)
                return;
            SelectedNode = nodeId;
            NotifyChanged();
        }

        /// <summary>The selected node can take a level now (the server's own check) and nothing is pending.</summary>
        public bool CanLearn => TreePending == 0 && SelectedNode != null && Tree is { } tree && tree.Check(SelectedNode) == JobTreeCheck.Ok;

        /// <summary>The tab's tree has something to reset and nothing is pending (combat and sessions are the server's checks).</summary>
        public bool CanRespec => TreePending == 0 && !ConfirmingRespec && Tree is { AnythingLearned: true };

        /// <summary>The first step of a respec: the window asks the player to confirm.</summary>
        public bool AskRespec()
        {
            if (!CanRespec)
                return false;
            ConfirmingRespec = true;
            NotifyChanged();
            return true;
        }

        public void CancelRespec()
        {
            if (!ConfirmingRespec)
                return;
            ConfirmingRespec = false;
            NotifyChanged();
        }

        /// <summary>A learn or a confirmed respec was sent as <paramref name="requestId"/>.</summary>
        public void SetTreePending(uint requestId)
        {
            TreePending = requestId;
            ConfirmingRespec = false;
            NotifyChanged();
        }

        /// <summary>The answer to a tree request: the pending one is done. Returns the reason code of a refusal, else null.</summary>
        public string? OnTreeResult(JobTreeResult result)
        {
            if (result.RequestId != TreePending || TreePending == 0)
                return null;
            TreePending = 0;
            NotifyChanged();
            return result.Reason == JobReason.None ? null : result.Reason.ToString();
        }

        /// <summary>The held job with <paramref name="jobNetId"/>; null when not held.</summary>
        public JobEntry? Held(byte jobNetId) => State is { } state ? Find(state, jobNetId) : null;

        /// <summary>Opens the jobs list, or closes the window (and with it the pick section).</summary>
        public void SetOpen(bool open)
        {
            if (Open == open)
                return;
            Open = open;
            if (!open)
            {
                NpcEntityId = 0;
                Confirming = null;
            }
            NotifyChanged();
        }

        /// <summary>The key or the JB button: opens the jobs list (without the pick section) or closes the window.</summary>
        public void Toggle() => SetOpen(!Open);

        /// <summary>The job master answered yes (spec 5.1): the window opens with its pick section.</summary>
        public void OpenAt(uint npcEntityId)
        {
            NpcEntityId = npcEntityId;
            Open = true;
            Confirming = null;
            NotifyChanged();
        }

        /// <summary>The player left the job master's reach: the pick section and any confirmation go, the list stays.</summary>
        public void LeaveJobMaster()
        {
            if (NpcEntityId == 0 && Confirming == null)
                return;
            NpcEntityId = 0;
            Confirming = null;
            NotifyChanged();
        }

        /// <summary>Takes a new state and returns the jobs that levelled up since the previous one (none for the first state).</summary>
        public IReadOnlyList<JobNews> SetState(JobState state)
        {
            _news.Clear();
            if (State != null)
            {
                foreach (var job in state.Jobs)
                {
                    var before = Find(State, job.JobNetId);
                    if (before != null && job.Level > before.Level)
                        _news.Add(new JobNews(job.JobNetId, job.Level));
                }
            }
            State = state;
            if (Tab != 0 && Find(state, Tab) == null)
            {
                Tab = 0;
                SelectedNode = null;
                ConfirmingRespec = false;
            }
            NotifyChanged();
            return _news;
        }

        public void SetLevel(int level)
        {
            if (Level == level)
                return;
            Level = level;
            NotifyChanged();
        }

        /// <summary>The held job in <paramref name="slot"/>; null when empty.</summary>
        public JobEntry? InSlot(JobSlot slot)
        {
            if (State == null)
                return null;
            foreach (var job in State.Jobs)
            {
                if (job.Slot == slot)
                    return job;
            }
            return null;
        }

        /// <summary>
        /// What the rules say of picking <paramref name="job"/> for <paramref name="slot"/> now (the server checks combat, reach
        /// and bag room too). Before the first state nothing can be picked.
        /// </summary>
        public JobPickCheck Check(JobDef job, JobSlot slot)
        {
            if (State == null || _rules == null)
                return JobPickCheck.AlreadyChosen;
            return JobRules.CheckPick(job, slot, Level, IdIn(JobSlot.Main), IdIn(JobSlot.Secondary));
        }

        /// <summary>A pick can be asked for: at the job master, the rules allow it, nothing pending or being confirmed.</summary>
        public bool CanPick(JobDef job, JobSlot slot) => AtJobMaster && Pending == 0 && Confirming == null && Check(job, slot) == JobPickCheck.Ok;

        /// <summary>The first step of a pick: the window asks the player to confirm (spec 5.1 M3 decisions).</summary>
        public bool AskConfirm(JobDef job, JobSlot slot)
        {
            if (!CanPick(job, slot))
                return false;
            Confirming = ((byte)job.NetId, slot);
            NotifyChanged();
            return true;
        }

        public void CancelConfirm()
        {
            if (Confirming == null)
                return;
            Confirming = null;
            NotifyChanged();
        }

        /// <summary>The confirmed pick was sent as <paramref name="requestId"/>.</summary>
        public void SetPending(uint requestId)
        {
            Pending = requestId;
            Confirming = null;
            NotifyChanged();
        }

        /// <summary>The answer to a pick: the pending one is done. Returns the reason code of a refusal, else null.</summary>
        public string? OnResult(JobResult result)
        {
            if (result.RequestId != Pending || Pending == 0)
                return null;
            Pending = 0;
            NotifyChanged();
            return result.Reason == JobReason.None ? null : result.Reason.ToString();
        }

        /// <summary>The connection ended: nothing is known until the next state.</summary>
        public void Clear()
        {
            State = null;
            Pending = 0;
            Confirming = null;
            NpcEntityId = 0;
            Open = false;
            Tab = 0;
            SelectedNode = null;
            TreePending = 0;
            ConfirmingRespec = false;
            NotifyChanged();
        }

        private string? IdIn(JobSlot slot) => InSlot(slot) is { } held ? _rules?.FindByNetId(held.JobNetId)?.Id ?? held.JobNetId.ToString() : null;

        private static JobEntry? Find(JobState state, byte jobNetId)
        {
            foreach (var job in state.Jobs)
            {
                if (job.JobNetId == jobNetId)
                    return job;
            }
            return null;
        }
    }
}
