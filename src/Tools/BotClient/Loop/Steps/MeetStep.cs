using BotClient.Brain;
using Game.Gameplay.Entities;

namespace BotClient.Loop.Steps;

/// <summary>
/// The pair's meeting (WP-M4-12, spec 11.5; contract in <c>Loop/Steps/README.md</c>, "Pairs"): <c>meet:&lt;point&gt;</c>
/// (point <c>meet</c> without an argument). When it begins it reserves its occurrence of the point (<c>PairLink.Open</c>: both
/// sides advance once per step entry, even when a step ends before it arrives); it waits for the partner's name on the pair
/// board, walks to within <c>BotBehaviour.pairReachCm</c> of the partner (its entity once in interest, else its posted
/// position), arrives, and is done once the partner arrived at the same occurrence (both bots of a loop meet again each round).
/// Counters: <c>meet.met</c>, <c>meet.noPartner</c>, <c>meet.gaveUp</c>, <c>meet.gone</c>, <c>meet.late</c>, <c>meet.unreachable</c>.
/// <para>
/// Failed without a pair (no <c>BotScenario.pairs</c>), when the partner gave the meeting up or left, after <c>pairWaitMs</c>
/// without its name, its position or its arrival, and when the walk to it gets no closer for <c>pairWaitMs</c> (a level gate,
/// a blocked path, a stale position). The occurrence it did not meet is given up by the loop when the step ends.
/// </para>
/// </summary>
public sealed class MeetStep : IBotStep
{
    private readonly string _point;
    private int _occurrence;
    private bool _arrived;
    private long _waitFromMs;
    private double _closestCm;

    public MeetStep(string argument = "")
    {
        _point = argument.Length > 0 ? argument : "meet";
    }

    public string Id => "meet";

    public void Begin(BotStepContext context)
    {
        _occurrence = context.Pair?.Open(_point) ?? 0;
        _arrived = false;
        _waitFromMs = context.NowMs;
        _closestCm = double.MaxValue;
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
        int waitMs = context.Behaviour.PairWaitMs;
        if (!_arrived)
        {
            if (pair.PartnerGone)
            {
                context.Count("meet.gone");
                return context.Failed($"partner gone before '{_point}'");
            }
            var approach = context.ApproachPartner(context.Behaviour.PairReachCm, ref command, out double distanceCm);
            if (approach == NpcApproach.Walking)
            {
                // Progress is getting closer by a quarter of the reach; a walk that does not for pairWaitMs is unreachable.
                if (distanceCm < _closestCm - context.Behaviour.PairReachCm / 4.0)
                {
                    _closestCm = distanceCm;
                    _waitFromMs = context.NowMs;
                }
                else if (waited > waitMs)
                {
                    context.Count("meet.unreachable");
                    return context.Failed($"partner '{pair.PartnerName}' not reached for '{_point}': no closer than {_closestCm:0} cm "
                        + $"for pairWaitMs {waitMs} ms");
                }
                return BotStepStatus.Running;
            }
            if (approach == NpcApproach.None)
            {
                // Hold until the partner says who and where it is.
                if (waited <= waitMs)
                    return BotStepStatus.Running;
                context.Count("meet.late");
                return context.Failed($"partner never posted its name and position within pairWaitMs {waitMs} ms");
            }
            pair.Arrive(_point, _occurrence);
            _arrived = true;
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
        if (waited <= waitMs)
            return BotStepStatus.Running;
        context.Count("meet.late");
        return context.Failed($"partner did not reach '{_point}' within pairWaitMs {waitMs} ms");
    }
}
