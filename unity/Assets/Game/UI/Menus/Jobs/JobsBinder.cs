#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Jobs;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Jobs;
using UnityEngine.InputSystem;

namespace Game.UI.Menus.Jobs
{
    /// <summary>What the player does in the Jobs window; the binder turns it into requests (WP-M3-1).</summary>
    public interface IJobsWindowActions
    {
        /// <summary>The first step of a pick: job row <paramref name="row"/> for <paramref name="slot"/> asks for confirmation.</summary>
        void Pick(int row, JobSlot slot);

        void ConfirmPick();

        void CancelPick();

        void CloseJobs();

        /// <summary>Shows the jobs list (0) or the tree of a held job (its <c>JobDef.netId</c>; WP-M3-7).</summary>
        void SelectTab(byte jobNetId);

        void SelectNode(string nodeId);

        /// <summary>One more level of the selected node.</summary>
        void Learn();

        /// <summary>The first step of a respec of the tab's tree: the window asks for confirmation.</summary>
        void Respec();

        void ConfirmRespec();

        void CancelRespec();
    }

    /// <summary>
    /// Connects the Jobs window (spec 5.1, 9.13; WP-M3-1) to the world client: the job states and pick answers of the
    /// <see cref="WorldClientFeed"/> go into the <see cref="JobsWindowViewModel"/> with the character level; the job master's
    /// yes (<see cref="NpcInteraction.Jobs"/>) opens the window with its pick section, which closes when the player leaves its
    /// reach; a confirmed pick goes out through <see cref="WorldClientFeed.JobRequests"/>, one at a time. Refusals show their
    /// <c>ReasonCodeStrings</c> text in the HUD toast, a committed pick and a job level-up a news toast. The toggle key is the
    /// <c>Hotkeys</c> row <c>toggleJobs</c>. Display time is the caller's clock in ms.
    /// </summary>
    public sealed class JobsBinder : IJobsWindowActions, IDisposable
    {
        private readonly JobsConfig _config;
        private readonly UiText _text;
        private readonly JobsWindowViewModel _window;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private readonly InputAction? _toggle;
        private WorldClientFeed? _feed;
        private byte _pendingJob;
        private (JobTreeOp Op, byte Job, string Node) _pendingTree;

        /// <param name="readToggleKey">False in tests: no Input System action is made for the toggle key.</param>
        public JobsBinder(JobsConfig config, UiText text, JobsWindowViewModel window, ToastViewModel toast, int toastMs, Func<double> nowMs,
            bool readToggleKey = true)
        {
            _config = config;
            _text = text;
            _window = window;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            if (readToggleKey && config.ToggleBinding.Length > 0)
            {
                _toggle = new InputAction("ToggleJobs", InputActionType.Button, config.ToggleBinding);
                _toggle.Enable();
            }
        }

        public WorldClientFeed? Feed => _feed;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.JobsReceived += OnJobs;
            feed.JobAnswered += OnAnswered;
            feed.JobTreeAnswered += OnTreeAnswered;
            feed.ProgressionReceived += OnProgression;
            feed.OutpostInteracted += OnInteracted;
            feed.Disconnected += OnDisconnected;
            if (feed.Progression != null) // they may have come before the HUD bound
                OnProgression(feed.Progression);
            if (feed.JobsState != null)
                OnJobs(feed.JobsState);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.JobsReceived -= OnJobs;
            _feed.JobAnswered -= OnAnswered;
            _feed.JobTreeAnswered -= OnTreeAnswered;
            _feed.ProgressionReceived -= OnProgression;
            _feed.OutpostInteracted -= OnInteracted;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose()
        {
            Unbind();
            _toggle?.Dispose();
        }

        /// <summary>
        /// Once per frame: the toggle key (ignored while the chat input has focus: an "o" typed in a line is text), and the pick
        /// section closes once the job master is no longer the NPC in reach.
        /// </summary>
        public void Frame(double nowMs)
        {
            if (_toggle != null && _toggle.WasPressedThisFrame() && !(_feed?.TextInputActive ?? false))
                _window.Toggle();
            if (_feed != null && _window.AtJobMaster && _window.NpcEntityId != _feed.NpcTarget)
                _window.LeaveJobMaster();
        }

        public void Pick(int row, JobSlot slot)
        {
            if (row >= 0 && row < _window.Jobs.Count)
                _window.AskConfirm(_window.Jobs[row], slot);
        }

        public void ConfirmPick()
        {
            if (_window.Confirming is not { } pick || _feed?.JobRequests == null)
                return;
            uint requestId = _feed.JobRequests.RequestJob(_window.NpcEntityId, pick.JobNetId, pick.Slot);
            if (requestId == 0)
                return;
            _pendingJob = pick.JobNetId;
            _window.SetPending(requestId);
        }

        public void CancelPick() => _window.CancelConfirm();

        public void CloseJobs() => _window.SetOpen(false);

        public void SelectTab(byte jobNetId) => _window.SelectTab(jobNetId);

        public void SelectNode(string nodeId) => _window.SelectNode(nodeId);

        public void Learn()
        {
            if (!_window.CanLearn || _window.SelectedNode is not { } node || _feed?.JobRequests == null)
                return;
            uint requestId = _feed.JobRequests.RequestJobTree(JobTreeOp.Learn, _window.Tab, node);
            if (requestId == 0)
                return;
            _pendingTree = (JobTreeOp.Learn, _window.Tab, node);
            _window.SetTreePending(requestId);
        }

        public void Respec() => _window.AskRespec();

        public void ConfirmRespec()
        {
            if (!_window.ConfirmingRespec || _feed?.JobRequests == null)
                return;
            uint requestId = _feed.JobRequests.RequestJobTree(JobTreeOp.Respec, _window.Tab, string.Empty);
            if (requestId == 0)
                return;
            _pendingTree = (JobTreeOp.Respec, _window.Tab, string.Empty);
            _window.SetTreePending(requestId);
        }

        public void CancelRespec() => _window.CancelRespec();

        /// <summary>The job master said yes (its refusals are the NPC window's toasts, like every interaction).</summary>
        private void OnInteracted(OutpostInteractResult result)
        {
            if (result.Kind == NpcInteraction.Jobs && result.Reason == OutpostReason.None)
                _window.OpenAt(result.NpcEntityId);
        }

        private void OnJobs(JobState state)
        {
            foreach (var news in _window.SetState(state))
                _toast.ShowText(JobsTextIds.ToastLevelUp, "name", Name(news.JobNetId), _now(), _toastMs);
        }

        private void OnAnswered(JobResult result)
        {
            bool mine = result.RequestId == _window.Pending && _window.Pending != 0;
            string? refused = _window.OnResult(result);
            if (refused != null)
                _toast.Show(refused, false, _now(), _toastMs);
            else if (mine)
                _toast.ShowText(JobsTextIds.ToastPicked, "name", Name(_pendingJob), _now(), _toastMs);
        }

        /// <summary>A tree answer: a refusal's reason toast, or the learned node or the reset tree (WP-M3-7).</summary>
        private void OnTreeAnswered(JobTreeResult result)
        {
            bool mine = result.RequestId == _window.TreePending && _window.TreePending != 0;
            string? refused = _window.OnTreeResult(result);
            if (refused != null)
                _toast.Show(refused, false, _now(), _toastMs);
            else if (mine && _pendingTree.Op == JobTreeOp.Learn)
                _toast.ShowText(JobsTextIds.ToastLearned, "name", NodeName(_pendingTree.Node), _now(), _toastMs);
            else if (mine)
                _toast.ShowText(JobsTextIds.ToastRespecced, "name", Name(_pendingTree.Job), _now(), _toastMs);
        }

        private string NodeName(string nodeId) => _config.Rules?.Tree?.Node(nodeId) is { } node ? _text.Get(node.NameId) : nodeId;

        private void OnProgression(ProgressionState state) => _window.SetLevel(state.Level);

        private void OnDisconnected(string reasonCode) => _window.Clear();

        private string Name(int jobNetId) => _config.Job(jobNetId) is { } job ? _text.Get(job.NameId) : jobNetId.ToString();
    }
}
