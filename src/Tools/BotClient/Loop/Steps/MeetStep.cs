using BotClient.Brain;
using Game.Gameplay.Entities;

namespace BotClient.Loop.Steps;

/// <summary>
/// The pair's meeting (WP-M4-12, spec 11.5; contract in <c>Loop/Steps/README.md</c>, "Pairs"): <c>meet:&lt;point&gt;</c>
/// (point <c>meet</c> without an argument). It waits for the partner's name on the pair board, walks to within
/// <c>BotBehaviour.pairReachCm</c> of the partner (its entity once in interest, else its posted position), arrives at the
/// point and is done once the partner arrived at the same occurrence (both bots of a loop meet again each round).
/// Counters: <c>meet.met</c>, <c>meet.noPartner</c>, <c>meet.gaveUp</c>, <c>meet.gone</c>, <c>meet.late</c>.
/// <para>
/// Failed without a pair (no <c>BotScenario.pairs</c>), when the partner gave the meeting up or left, or after
/// <c>pairWaitMs</c> without its name, its position, or its arrival.
/// </para>
/// </summary>
public sealed class MeetStep : IBotStep
{
    private readonly string _point;
    private int _occurrence;
    private long _waitFromMs;

    public MeetStep(string argument = "")
    {
        _point = argument.Length > 0 ? argument : "meet";
    }

    public string Id => "meet";

    public void Begin(BotStepContext context)
    {
        _occurrence = 0;
        _waitFromMs = context.NowMs;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        var pair = context.Pair;
        if (pair == null)
        {
            context.Count("meet.noPartner");
            return context.Failed("no partner: meet needs BotScenario.pairs (or the QA partner's board)");
        }
        long waited = context.NowMs - _waitFromMs;
        if (_occurrence == 0)
        {
            if (pair.PartnerGone)
            {
                context.Count("meet.gone");
                return context.Failed($"partner gone before '{_point}'");
            }
            var approach = context.ApproachPartner(context.Behaviour.PairReachCm, ref command);
            if (approach == NpcApproach.Walking)
            {
                _waitFromMs = context.NowMs; // walking is progress: the step's own timeout bounds it
                return BotStepStatus.Running;
            }
            if (approach == NpcApproach.None)
            {
                // Hold until the partner says who and where it is.
                if (waited <= context.Behaviour.PairWaitMs)
                    return BotStepStatus.Running;
                context.Count("meet.late");
                return context.Failed($"partner never posted its name and position within pairWaitMs {context.Behaviour.PairWaitMs} ms");
            }
            _occurrence = pair.Arrive(_point);
            _waitFromMs = context.NowMs;
            waited = 0;
        }
        switch (pair.Check(_point, _occurrence))
        {
            case MeetState.Met:
                context.Count("meet.met");
                return BotStepStatus.Done;
            case MeetState.PartnerGaveUp:
                context.Count("meet.gaveUp");
                return context.Failed($"partner gave up meeting '{_point}'");
            case MeetState.PartnerGone:
                context.Count("meet.gone");
                return context.Failed($"partner gone before '{_point}'");
        }
        if (waited <= context.Behaviour.PairWaitMs)
            return BotStepStatus.Running;
        context.Count("meet.late");
        return context.Failed($"partner did not reach '{_point}' within pairWaitMs {context.Behaviour.PairWaitMs} ms");
    }
}
