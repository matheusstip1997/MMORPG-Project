using BotClient.Brain;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's run step (WP-M2-6; spec 5.5, 11.5). It reads the character's run from the server's <c>RunStatus</c> (the world
/// client's kept <see cref="ClientQuests"/>, <c>context.Quests</c>; the first comes at enter world) and:
/// <list type="bullet">
/// <item>without a run, walks to the nearest outpost that offers quests, opens its board (<c>OutpostInteractRequest</c> Quests) and
/// accepts a run of <c>BotBehaviour.loopRunSteps</c> kill offers (at most <c>QuestCaps.maxStepsPerRun</c>), the ones with the fewest
/// kills, in board order; the fight step's kills then complete them (one kill counts for one step);</item>
/// <item>with a completed run, walks to the outpost and claims it (answered once the claim's save committed);</item>
/// <item>with a run still active, is done at once (the next rounds' fights advance it).</item>
/// </list>
/// Counters: <c>run.accepted</c>, <c>run.steps</c>, <c>run.claimed</c>, <c>run.exp</c>, <c>run.active</c>, <c>run.noKillOffers</c>,
/// <c>run.noNpc</c>, <c>run.refused.&lt;reason&gt;</c>. Without a <c>RunStatus</c> from the server (a world without quests) it is
/// not available.
/// </summary>
public sealed class RunStep : IBotStep
{
    private Phase _phase;
    private uint _npc;
    private uint _pending;
    private QuestBoard? _boardBefore;

    private enum Phase : byte
    {
        Decide,
        Approach,
        Opening,
        Board,
        Accepting,
        Claiming,
    }

    public string Id => "run";

    public void Begin(BotStepContext context)
    {
        _phase = Phase.Decide;
        _npc = 0;
        _pending = 0;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        switch (_phase)
        {
            case Phase.Decide:
                if (context.Quests.Run is not { } run)
                    return context.NotAvailable(Id, "no RunStatus from the server (a world without outpost quests)");
                if (run.State == RunState.Active)
                {
                    context.Count("run.active");
                    return BotStepStatus.Done;
                }
                _phase = Phase.Approach;
                return BotStepStatus.Running;

            case Phase.Approach:
                switch (context.ApproachNpc(npc => npc.Offers(NpcInteraction.Quests), ref command, out _npc, out _))
                {
                    case NpcApproach.None:
                        context.Count("run.noNpc");
                        return BotStepStatus.Done;
                    case NpcApproach.Walking:
                        return BotStepStatus.Running;
                }
                if (context.Quests.Run?.State == RunState.Completed)
                {
                    context.SendReliable(context.Quests.RunRequest(_npc, QuestRunAction.Claim, 0, 0, Array.Empty<byte>(), out _pending));
                    _phase = Phase.Claiming;
                    return BotStepStatus.Running;
                }
                _boardBefore = context.Quests.Board;
                context.SendReliable(context.Outposts.InteractRequest(_npc, NpcInteraction.Quests, out _pending));
                _phase = Phase.Opening;
                return BotStepStatus.Running;

            case Phase.Opening:
                if (context.Outposts.LastInteract is not { } opened || opened.RequestId != _pending)
                    return BotStepStatus.Running;
                if (opened.Reason == OutpostReason.OutOfRange)
                {
                    Refused(context, opened.Reason);
                    _phase = Phase.Approach; // the server had it a step farther: walk on and ask again
                    return BotStepStatus.Running;
                }
                if (opened.Reason != OutpostReason.None)
                {
                    Refused(context, opened.Reason);
                    return BotStepStatus.Failed;
                }
                _phase = Phase.Board;
                return BotStepStatus.Running;

            case Phase.Board:
                if (context.Quests.Board is not { } board || ReferenceEquals(board, _boardBefore) || board.NpcEntityId != _npc)
                    return BotStepStatus.Running; // the board follows the interaction's yes
                return Accept(context, board);

            case Phase.Accepting:
                if (context.Quests.LastResult is not { } accepted || accepted.RequestId != _pending)
                    return BotStepStatus.Running;
                if (accepted.Reason == OutpostReason.BoardExpired)
                {
                    Refused(context, accepted.Reason);
                    // The new board came just before the refusal (boards are sent before run answers).
                    return context.Quests.Board is { } fresh && fresh.NpcEntityId == _npc ? Accept(context, fresh) : BotStepStatus.Failed;
                }
                if (accepted.Reason != OutpostReason.None)
                {
                    Refused(context, accepted.Reason);
                    return BotStepStatus.Failed;
                }
                context.Count("run.accepted");
                return BotStepStatus.Done;

            default: // Claiming: answered once the claim's save committed
                if (context.Quests.LastResult is not { } claimed || claimed.RequestId != _pending)
                    return BotStepStatus.Running;
                if (claimed.Reason == OutpostReason.OutOfRange)
                {
                    Refused(context, claimed.Reason);
                    _phase = Phase.Approach;
                    return BotStepStatus.Running;
                }
                if (claimed.Reason != OutpostReason.None)
                {
                    Refused(context, claimed.Reason);
                    return BotStepStatus.Failed;
                }
                context.Count("run.claimed");
                context.Count("run.exp", claimed.Exp);
                return BotStepStatus.Done;
        }
    }

    /// <summary>The kill offers with the fewest kills (ties in board order), as many as the behaviour and the run cap allow.</summary>
    private BotStepStatus Accept(BotStepContext context, QuestBoard board)
    {
        int cap = context.Data.Tables.TryGet(typeof(QuestCaps), out var caps) && caps.Rows.Count == 1
            ? ((QuestCaps)caps.Rows[0]).MaxStepsPerRun
            : 1;
        var picks = board.Offers.Where(o => o.Kind == QuestKind.Kill).OrderBy(o => o.Count).ThenBy(o => o.Slot)
            .Take(Math.Min(context.Behaviour.LoopRunSteps, cap)).Select(o => o.Slot).ToArray();
        if (picks.Length == 0)
        {
            context.Count("run.noKillOffers");
            return BotStepStatus.Done;
        }
        context.SendReliable(context.Quests.RunRequest(_npc, QuestRunAction.Accept, board.Epoch, 0, picks, out _pending));
        context.Count("run.steps", picks.Length);
        _phase = Phase.Accepting;
        return BotStepStatus.Running;
    }

    private static void Refused(BotStepContext context, OutpostReason reason) => context.Count("run.refused." + reason);
}
