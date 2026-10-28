#nullable enable
using System.Collections.Generic;
using Shared.Protocol;

namespace Game.UI.ViewModels.Quests
{
    /// <summary>An accept the player confirmed: the board's epoch and the offer slots in run order.</summary>
    public readonly struct AcceptIntent
    {
        public AcceptIntent(uint epoch, byte[] slots)
        {
            Epoch = epoch;
            Slots = slots;
        }

        public uint Epoch { get; }
        public byte[] Slots { get; }
    }

    /// <summary>
    /// The quest board and run builder v1 at an outpost (spec 5.5, 9.13 M2; WP-M2-6): the outpost's offers as the server sent them,
    /// the offers the player picked for a run in pick order (at most <c>QuestCaps.maxStepsPerRun</c>), and the character's run with
    /// its steps (hand in, forfeit, claim). The client never holds the truth: rewards, progress and the claim come from the server;
    /// a refusal's reason code goes to the HUD toast.
    /// </summary>
    public sealed class QuestBoardViewModel : ViewModel
    {
        private readonly List<byte> _picked = new List<byte>();
        private uint _pendingAccept;

        public QuestBoardViewModel(int maxSteps)
        {
            MaxSteps = maxSteps;
        }

        public int MaxSteps { get; }

        public bool Open { get; private set; }

        public uint NpcEntityId { get; private set; }

        /// <summary>The outpost's board; null until it arrived.</summary>
        public QuestBoard? Board { get; private set; }

        /// <summary>The character's run (state None: none); null before the server sent it.</summary>
        public RunStatus? Run { get; private set; }

        /// <summary>Offer slots picked for the run, in run order.</summary>
        public IReadOnlyList<byte> Picked => _picked;

        public bool HasRun => Run != null && (Run.State == RunState.Active || Run.State == RunState.Completed);

        /// <summary>An accept is on its way to the server.</summary>
        public bool AcceptPending => _pendingAccept != 0;

        public void OpenFor(uint npcEntityId)
        {
            if (Open && NpcEntityId == npcEntityId)
                return;
            Open = true;
            NpcEntityId = npcEntityId;
            if (Board != null && Board.NpcEntityId != npcEntityId)
            {
                Board = null;
                _picked.Clear();
            }
            NotifyChanged();
        }

        public void Close()
        {
            if (!Open)
                return;
            Open = false;
            NotifyChanged();
        }

        /// <summary>A board arrived: a new epoch or outpost drops the picks (their offers are gone).</summary>
        public void SetBoard(QuestBoard board)
        {
            if (Board == null || Board.Epoch != board.Epoch || Board.NpcEntityId != board.NpcEntityId)
                _picked.Clear();
            Board = board;
            NotifyChanged();
        }

        public void SetRun(RunStatus run)
        {
            Run = run;
            if (HasRun)
                _picked.Clear();
            NotifyChanged();
        }

        /// <summary>Picks an offer for the run, or unpicks it; false when nothing changed (the run is full, or there is a run).</summary>
        public bool Toggle(byte slot)
        {
            if (Board == null || HasRun || slot >= Board.Offers.Length)
                return false;
            if (_picked.Remove(slot))
            {
                NotifyChanged();
                return true;
            }
            if (_picked.Count >= MaxSteps)
                return false;
            _picked.Add(slot);
            NotifyChanged();
            return true;
        }

        public void ClearPicks()
        {
            if (_picked.Count == 0)
                return;
            _picked.Clear();
            NotifyChanged();
        }

        /// <summary>The locked rewards of the picked offers, before the step bonus.</summary>
        public long PickedRewards()
        {
            long sum = 0;
            if (Board == null)
                return 0;
            foreach (byte slot in _picked)
            {
                if (slot < Board.Offers.Length)
                    sum += Board.Offers[slot].RewardExp;
            }
            return sum;
        }

        /// <summary>What an accept would send now; null without picks, a board, or with a run or an accept on its way.</summary>
        public AcceptIntent? Confirm() =>
            Board == null || _picked.Count == 0 || HasRun || AcceptPending ? (AcceptIntent?)null : new AcceptIntent(Board.Epoch, _picked.ToArray());

        public void AcceptSent(uint requestId)
        {
            _pendingAccept = requestId;
            NotifyChanged();
        }

        /// <summary>The answer of a run request; the accept's clears the pending flag (its run comes as a <see cref="RunStatus"/>).</summary>
        public void OnResult(QuestRunResult result)
        {
            if (result.Action != QuestRunAction.Accept || result.RequestId != _pendingAccept)
                return;
            _pendingAccept = 0;
            NotifyChanged();
        }

        /// <summary>The connection ended.</summary>
        public void Clear()
        {
            Open = false;
            NpcEntityId = 0;
            Board = null;
            Run = null;
            _picked.Clear();
            _pendingAccept = 0;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Spec 9.3 <c>run_tracker</c> (WP-M2-6): the character's run while it is active or completed: its steps with their progress,
    /// the step bonus and forfeit penalty, and the claim note. From <see cref="RunStatus"/> only.
    /// </summary>
    public sealed class RunTrackerViewModel : ViewModel
    {
        public RunStatus? Run { get; private set; }

        public bool Visible => Run != null && (Run.State == RunState.Active || Run.State == RunState.Completed);

        public int DoneSteps
        {
            get
            {
                int done = 0;
                if (Run != null)
                {
                    foreach (var step in Run.Steps)
                    {
                        if (step.State != RunStepState.Active)
                            done++;
                    }
                }
                return done;
            }
        }

        public void Set(RunStatus? run)
        {
            Run = run;
            NotifyChanged();
        }
    }
}
