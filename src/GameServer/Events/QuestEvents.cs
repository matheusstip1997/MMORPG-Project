using Shared.Data.Tables;

namespace GameServer.Events;

// Outpost quest and run events (spec 5.5, 8.1; WP-M2-6), published by the quest system on the world thread when the change is
// made (save class 2: the run state rides the next character save). The envelope's actor is the run's owner. RunId is the
// run's database id.

/// <summary>A step of a run reached its count (R-5.5-03: steps are tracked independently).</summary>
public readonly record struct QuestStepDone(long CharacterId, Guid RunId, int StepIndex, QuestKind Kind, int Required) : IGameEvent;

/// <summary>A step was forfeited (R-5.5-03): it pays nothing and takes the forfeit penalty off the run.</summary>
public readonly record struct QuestForfeited(long CharacterId, Guid RunId, int StepIndex, int PenaltyPercent) : IGameEvent;

/// <summary>
/// A run's exp was paid in its one claim (R-5.5-03): <see cref="Exp"/> through <c>ExpService.Grant</c>, after the step bonus and
/// the forfeit penalty (whole percents).
/// </summary>
public readonly record struct RunClaimed(long CharacterId, Guid RunId, int CompletedSteps, int ForfeitedSteps, int BonusPercent,
    int PenaltyPercent, long Exp) : IGameEvent;

/// <summary>
/// Items were handed in for a deliver or return step at an outpost and destroyed (R-5.5-02: an item sink). Published by the
/// delivery itself, which advances the step in the same call so the removal and the progress ride one save.
/// </summary>
public readonly record struct ItemDelivered(long CharacterId, Guid RunId, int StepIndex, ushort ItemNetId, int Count) : IGameEvent;
