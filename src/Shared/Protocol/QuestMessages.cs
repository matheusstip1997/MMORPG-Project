using System;
using MemoryPack;
using Shared.Data.Tables;

namespace Shared.Protocol
{
    /// <summary>Where a run is in its life (spec 5.5 <c>Run.state</c>). Stored in the database by value: append only.</summary>
    public enum RunState : byte
    {
        /// <summary>No run (a <see cref="RunStatus"/> of a character without one).</summary>
        None = 0,
        /// <summary>At least one step is still active.</summary>
        Active = 1,
        /// <summary>Every step is done or forfeited and at least one is done: the run can be claimed.</summary>
        Completed = 2,
        /// <summary>The exp was paid (one claim, R-5.5-03).</summary>
        Claimed = 3,
        /// <summary>Every step was forfeited: nothing to claim.</summary>
        Abandoned = 4,
    }

    /// <summary>A step of a run. Stored in the database by value: append only.</summary>
    public enum RunStepState : byte
    {
        Active = 0,
        Done = 1,
        /// <summary>Forfeited (R-5.5-03): pays nothing and takes the forfeit penalty off the run.</summary>
        Forfeited = 2,
    }

    /// <summary>What a <see cref="QuestRunRequest"/> asks for. Stored in the audit rows by value: append only.</summary>
    public enum QuestRunAction : byte
    {
        None = 0,
        /// <summary>Build a run from offers of the current board (rewards locked, R-5.5-02).</summary>
        Accept = 1,
        /// <summary>Hand in the items of a deliver or return step (consumed: an item sink).</summary>
        Deliver = 2,
        /// <summary>Give up a step (R-5.5-03 penalty).</summary>
        Forfeit = 3,
        /// <summary>Take the run's exp (one claim).</summary>
        Claim = 4,
    }

    /// <summary>One offer of a <see cref="QuestBoard"/>; the rewards are what accepting it locks.</summary>
    [MemoryPackable]
    public sealed partial class QuestOffer
    {
        /// <summary>The offer's index on its board (what <see cref="QuestRunRequest.Slots"/> names).</summary>
        public byte Slot { get; set; }
        public QuestKind Kind { get; set; }
        /// <summary>The monster to kill, or whose drop to deliver (<c>MonsterDef.netId</c>).</summary>
        public ushort MonsterNetId { get; set; }
        /// <summary>The item to deliver or return (<c>ItemDef.netId</c>); 0 for kill X.</summary>
        public ushort ItemNetId { get; set; }
        public ushort Count { get; set; }
        /// <summary>The exp of this step if the run is accepted now (before the step bonus and any penalty).</summary>
        public long RewardExp { get; set; }
        /// <summary>The demand reward multiplier it was scaled by, whole percent (R-4.2-03).</summary>
        public ushort RewardPercent { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (R-5.5-01; WP-M2-6): an outpost's quest board, the cached snapshot of its current epoch, sent when
    /// the player opens it (<see cref="OutpostInteractRequest"/> kind quests) and after a <c>BoardExpired</c> refusal.
    /// </summary>
    [MemoryPackable]
    public sealed partial class QuestBoard
    {
        public uint NpcEntityId { get; set; }
        /// <summary>The board's epoch: an accept names it, and is refused once a newer board replaced it.</summary>
        public uint Epoch { get; set; }
        /// <summary>Time until the next board (world clock), ms.</summary>
        public int RefreshInMs { get; set; }
        public QuestOffer[] Offers { get; set; } = Array.Empty<QuestOffer>();
    }

    /// <summary>
    /// Client to server, reliable (WP-M2-6): a run request at an outpost NPC (the gate of interaction quests: NPC, range). Accept
    /// names the board's <see cref="Epoch"/> and the offer <see cref="Slots"/> in run order; deliver and forfeit name a
    /// <see cref="StepIndex"/>; claim names nothing more. MemoryPack after the id byte; append fields only.
    /// </summary>
    [MemoryPackable]
    public sealed partial class QuestRunRequest
    {
        public uint RequestId { get; set; }
        public uint NpcEntityId { get; set; }
        public QuestRunAction Action { get; set; }
        public uint Epoch { get; set; }
        public byte StepIndex { get; set; }
        public byte[] Slots { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Server to client, reliable: the answer to a <see cref="QuestRunRequest"/>. Accept, deliver and forfeit are answered at once
    /// (save class 2); a claim once its save committed (rule 5). <see cref="Count"/> is the units delivered,
    /// <see cref="Exp"/> the exp a claim paid.
    /// </summary>
    [MemoryPackable]
    public sealed partial class QuestRunResult
    {
        public uint RequestId { get; set; }
        public QuestRunAction Action { get; set; }
        public OutpostReason Reason { get; set; }
        public byte StepIndex { get; set; }
        public ushort Count { get; set; }
        public long Exp { get; set; }
    }

    /// <summary>One step of a <see cref="RunStatus"/>.</summary>
    [MemoryPackable]
    public sealed partial class RunStepEntry
    {
        public QuestKind Kind { get; set; }
        public ushort MonsterNetId { get; set; }
        public ushort ItemNetId { get; set; }
        public ushort Required { get; set; }
        public ushort Progress { get; set; }
        /// <summary>The step's locked reward (R-5.5-02).</summary>
        public long RewardExp { get; set; }
        public RunStepState State { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (spec 9.3 <c>run_tracker</c>): the character's run as the world holds it, sent when the player
    /// enters the world and whenever it changed. <see cref="State"/> None: no run. <see cref="ClaimExp"/> is what a claim would pay
    /// now (completed steps x the bonus x the penalty); <see cref="Revision"/> counts the messages of one connection.
    /// </summary>
    [MemoryPackable]
    public sealed partial class RunStatus
    {
        public uint Revision { get; set; }
        public RunState State { get; set; }
        public ushort BonusPercent { get; set; }
        public ushort PenaltyPercent { get; set; }
        public long ClaimExp { get; set; }
        public RunStepEntry[] Steps { get; set; } = Array.Empty<RunStepEntry>();
    }

    /// <summary>Bounds of the quest messages (the config tables stay within them).</summary>
    public static class QuestMessages
    {
        /// <summary>Most offers one board carries (<c>QuestBoardSettings.offersPerBoard</c>).</summary>
        public const int MaxOffers = 16;

        /// <summary>Most steps one run holds (<c>QuestCaps.maxStepsPerRun</c>).</summary>
        public const int MaxRunSteps = 8;

        /// <summary>The largest step count (<c>OutpostQuestDef.countMax</c>).</summary>
        public const int MaxCount = 9_999;

        public const QuestRunAction LastAction = QuestRunAction.Claim;
        public const RunState LastRunState = RunState.Abandoned;
    }

    public static partial class ReliableMessages
    {
        public static bool TryRead(ReadOnlySpan<byte> message, out QuestBoard board)
        {
            board = null!;
            if (!TryBody(message, ReliableMessageId.QuestBoard, out QuestBoard? value) || value?.Offers == null
                || value.Offers.Length > QuestMessages.MaxOffers || value.RefreshInMs < 0)
                return false;
            foreach (var offer in value.Offers)
            {
                if (offer == null || offer.Kind == QuestKind.None || offer.Kind > QuestKind.ReturnItem || offer.Count == 0
                    || offer.RewardExp < 0)
                    return false;
            }
            board = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out QuestRunRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.QuestRunRequest, out QuestRunRequest? value) || value?.Slots == null
                || value.Action == QuestRunAction.None || value.Action > QuestMessages.LastAction
                || value.Slots.Length > QuestMessages.MaxRunSteps)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out QuestRunResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.QuestRunResult, out QuestRunResult? value) || value == null
                || value.Action == QuestRunAction.None || value.Action > QuestMessages.LastAction
                || value.Reason > OutpostMessages.LastReason || value.Exp < 0)
                return false;
            result = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out RunStatus status)
        {
            status = null!;
            if (!TryBody(message, ReliableMessageId.RunStatus, out RunStatus? value) || value?.Steps == null
                || value.State > QuestMessages.LastRunState || value.Steps.Length > QuestMessages.MaxRunSteps
                || value.ClaimExp < 0 || value.PenaltyPercent > 100 || (value.State == RunState.None) != (value.Steps.Length == 0))
                return false;
            foreach (var step in value.Steps)
            {
                if (step == null || step.Kind == QuestKind.None || step.Kind > QuestKind.ReturnItem || step.Required == 0
                    || step.Progress > step.Required || step.State > RunStepState.Forfeited || step.RewardExp < 0)
                    return false;
            }
            status = value;
            return true;
        }
    }
}
