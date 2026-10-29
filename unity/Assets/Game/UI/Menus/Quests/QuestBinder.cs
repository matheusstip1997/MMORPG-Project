#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Quests;
using Shared.Data.Tables;
using Shared.Protocol;
using UnityEngine.InputSystem;

namespace Game.UI.Menus.Quests
{
    /// <summary>What the player does on the quest board; the binder turns it into run requests (WP-M2-6).</summary>
    public interface IQuestBoardActions
    {
        void ToggleOffer(byte slot);
        void ClearPicks();
        void AcceptRun();
        void HandIn(byte step);
        void Forfeit(byte step);
        void Claim();
        void CloseBoard();
    }

    /// <summary>
    /// Connects the quest board, the run builder and the run tracker (spec 5.5, 9.3, 9.13 M2; WP-M2-6) to the world client. The
    /// board opens when the server says yes to the quests interaction (the outpost window's Quests button) and its offers arrive;
    /// picks build a run of up to <c>QuestCaps.maxStepsPerRun</c> offers, accept sends them with the board's epoch, and the run's
    /// steps can be handed in, forfeited and claimed. Every answer comes from the server: refusals show their
    /// <c>ReasonCodeStrings</c> text in the HUD toast, a committed claim the <c>hud.toast.runClaimed</c> news. The board closes when
    /// its NPC is no longer in reach, or on its close button. Display time is the caller's clock in ms.
    /// </summary>
    public sealed class QuestBinder : IQuestBoardActions, IDisposable
    {
        private readonly QuestBoardViewModel _board;
        private readonly RunTrackerViewModel _tracker;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private readonly InputAction? _journalKey;
        private WorldClientFeed? _feed;

        /// <param name="journalBinding">The <c>Hotkeys</c> row <c>toggleJournal</c> (WP-M2-11); empty for none (tests).</param>
        public QuestBinder(QuestBoardViewModel board, RunTrackerViewModel tracker, ToastViewModel toast, int toastMs, Func<double> nowMs,
            string journalBinding = "")
        {
            _board = board;
            _tracker = tracker;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            if (journalBinding.Length > 0)
            {
                _journalKey = new InputAction("ToggleJournal", InputActionType.Button, journalBinding);
                _journalKey.Enable();
            }
        }

        public WorldClientFeed? Feed => _feed;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.OutpostInteracted += OnInteracted;
            feed.QuestBoardReceived += OnBoard;
            feed.QuestRunAnswered += OnAnswered;
            feed.RunReceived += OnRun;
            feed.Disconnected += OnDisconnected;
            if (feed.Run != null)
                OnRun(feed.Run);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.OutpostInteracted -= OnInteracted;
            _feed.QuestBoardReceived -= OnBoard;
            _feed.QuestRunAnswered -= OnAnswered;
            _feed.RunReceived -= OnRun;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose()
        {
            Unbind();
            _journalKey?.Dispose();
        }

        /// <summary>
        /// Once per frame: the journal key (ignored while the chat input has focus), and the board closes when its NPC left
        /// the reach (the journal has no NPC and stays).
        /// </summary>
        public void Frame(double nowMs)
        {
            if (_journalKey != null && _journalKey.WasPressedThisFrame() && !(_feed?.TextInputActive ?? false))
                _board.ToggleJournal();
            if (_feed != null && _board.Open && !_board.Journal && _board.NpcEntityId != _feed.NpcTarget)
                _board.Close();
        }

        public void ToggleOffer(byte slot) => _board.Toggle(slot);

        public void ClearPicks() => _board.ClearPicks();

        public void AcceptRun()
        {
            var intent = _board.Confirm();
            if (intent == null || _feed?.QuestRequests == null)
                return;
            uint requestId = _feed.QuestRequests.RequestQuestRun(_board.NpcEntityId, QuestRunAction.Accept, intent.Value.Epoch, 0,
                intent.Value.Slots);
            if (requestId != 0)
                _board.AcceptSent(requestId);
        }

        public void HandIn(byte step) => Send(QuestRunAction.Deliver, step);

        public void Forfeit(byte step) => Send(QuestRunAction.Forfeit, step);

        public void Claim() => Send(QuestRunAction.Claim, 0);

        public void CloseBoard() => _board.Close();

        private void Send(QuestRunAction action, byte step)
        {
            if (_feed?.QuestRequests == null || !_board.Open || _board.Journal)
                return;
            _feed.QuestRequests.RequestQuestRun(_board.NpcEntityId, action, 0, step, Array.Empty<byte>());
        }

        /// <summary>The server opened the board (the outpost window's request; its refusals are that window's toasts).</summary>
        private void OnInteracted(OutpostInteractResult result)
        {
            if (result.Kind == NpcInteraction.Quests && result.Reason == OutpostReason.None)
                _board.OpenFor(result.NpcEntityId);
        }

        private void OnBoard(QuestBoard board)
        {
            if (_board.Open && board.NpcEntityId == _board.NpcEntityId)
                _board.SetBoard(board);
        }

        private void OnAnswered(QuestRunResult result)
        {
            _board.OnResult(result);
            if (result.Reason != OutpostReason.None)
                _toast.Show(result.Reason.ToString(), false, _now(), _toastMs);
            else if (result.Action == QuestRunAction.Claim)
                _toast.ShowText(QuestTextIds.ToastClaimed, "exp", QuestConfig.Number(result.Exp), _now(), _toastMs);
        }

        private void OnRun(RunStatus run)
        {
            _board.SetRun(run);
            _tracker.Set(run);
        }

        private void OnDisconnected(string reasonCode)
        {
            _board.Clear();
            _tracker.Set(null);
        }
    }
}
