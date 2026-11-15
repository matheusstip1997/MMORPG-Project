using BotClient.Brain;
using Game.Gameplay.Entities;
using Shared.Simulation;

namespace BotClient.Loop.Steps;

/// <summary>
/// The pair's meeting (WP-M4-12, spec 11.5; contract in <c>Loop/Steps/README.md</c>, "Pairs"): <c>meet:&lt;point&gt;</c>
/// (point <c>meet</c> without an argument). When it begins it reserves its occurrence of the point (<c>PairLink.Open</c>: both
/// sides advance once per step entry, even when a step ends before it arrives); it waits for the partner's name on the pair
/// board; the second of the pair walks to within <c>BotBehaviour.pairReachCm</c> of the partner (its entity once in interest, else its
/// posted position) while the first holds where it stands (QA M4 wave 1: the first leads, as in the trade); it arrives, and is done once the partner arrived at the same occurrence (both bots of a loop meet again each round).
/// A monster that threatens the bot (<c>BotStepContext.FightBack</c>) is fought back first, after the partner-gone and give-up
/// checks (a fighting bot still sees its partner leave); every wait clock starts again once the fight is over (PR #254 review 3).
/// Counters: <c>meet.met</c>, <c>meet.noPartner</c>, <c>meet.gaveUp</c>, <c>meet.gone</c>, <c>meet.late</c>, <c>meet.unreachable</c>,
/// <c>meet.notStill</c>.
/// <para>
/// Failed without a pair (no <c>BotScenario.pairs</c>), when the partner gave the meeting up or left, after <c>pairWaitMs</c>
/// without its name, its position or its arrival, when the walk to it gets no closer for <c>pairWaitMs</c> (a level gate,
/// a blocked path, a stale position), and when the partner beside it never stood still for <see cref="StillMs"/> within
/// <c>pairWaitMs</c> (a leader fighting at a camp; PR #254 review 1). The occurrence it did not meet is given up by the loop
/// when the step ends.
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

    /// <summary>
    /// QA M4 wave 1 (the smoke's trade-item): the walking side arrives only once the partner's entity moved less than
    /// <see cref="StillCm"/> for <see cref="StillMs"/>. Beside the player still walking to the stall it had arrived mid-walk, the
    /// meeting counted met when the player stopped, and the trade invite there was refused TradeTooFar.
    /// </summary>
    public const int StillMs = 1_000, StillCm = 50;

    private FixVec2 _partnerAt;
    private long _partnerStillSinceMs = -1;

    /// <summary>When the wait for the partner to stand still began (-1: not waiting for it).</summary>
    private long _stillWaitFromMs = -1;

    /// <summary>The last tick fought (<see cref="BotStepContext.FightBack"/>): the next one restarts the wait clocks.</summary>
    private bool _fought;

    public void Begin(BotStepContext context)
    {
        _occurrence = context.Pair?.Open(_point) ?? 0;
        _arrived = false;
        _waitFromMs = context.NowMs;
        _closestCm = double.MaxValue;
        _partnerStillSinceMs = -1;
        _stillWaitFromMs = -1;
        _fought = false;
    }

    /// <summary>The first of the pair arrived at this occurrence of the point (its <c>meet.&lt;point&gt;</c> on the board).</summary>
    private bool LeaderArrived(PairLink pair) =>
        pair.Board.TryReadPartner("meet." + _point, out var text)
        && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)
        && n >= _occurrence;

    /// <summary>The partner's entity stood within <see cref="StillCm"/> of one spot for <see cref="StillMs"/> (a partner out of sight counts as still).</summary>
    private bool PartnerStill(BotStepContext context)
    {
        if (!context.TryPartnerEntity(out _, out var at))
            return true; // only its posted position is known: the spot it posted
        if (_partnerStillSinceMs < 0 || GridPath.DistanceCm(at, _partnerAt) > StillCm)
        {
            _partnerAt = at;
            _partnerStillSinceMs = context.NowMs;
        }
        return context.NowMs - _partnerStillSinceMs >= StillMs;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        var pair = context.Pair;
        if (pair == null)
        {
            context.Count("meet.noPartner");
            return context.Failed("no partner: meet needs BotScenario.pairs (or the QA partner's board)");
        }
        if (!_arrived)
        {
            // PR #254 review 3: gone and give-up first, so a bot fighting at the point still sees that its partner left.
            switch (pair.Check(_point, _occurrence))
            {
                case MeetState.PartnerGaveUp:
                    context.Count("meet.gaveUp");
                    return context.Failed($"partner gave up meeting '{_point}'");
                case MeetState.PartnerGone:
                    context.Count("meet.gone");
                    return context.Failed($"partner gone before '{_point}'");
            }
        }
        if (context.FightBack(ref command))
        {
            _fought = true;
            return BotStepStatus.Running; // a monster on it first (QA M4 wave 1): the wait goes on after the fight
        }
        if (_fought)
        {
            // PR #254 review 3: the fight is over; the waits start again from here (a fight longer than pairWaitMs is no late partner).
            _fought = false;
            _waitFromMs = context.NowMs;
            _closestCm = double.MaxValue;
            _partnerStillSinceMs = -1;
            if (_stillWaitFromMs >= 0)
                _stillWaitFromMs = context.NowMs;
        }
        long waited = context.NowMs - _waitFromMs;
        int waitMs = context.Behaviour.PairWaitMs;
        if (!_arrived)
        {
            // QA M4 wave 1 (the m4-exit run): the first of the pair holds where it stands and the second walks to it, as the first
            // leads a trade. When both walked to each other, one still fighting at a camp drew the meeting into the camp: the
            // trade there was refused TradeInCombat and a waiting bot was killed.
            // The second also waits until the first arrived at this occurrence (its meet.<point>, posted where it stands): the smoke's
            // player stood a few seconds at the auctioneer after the first trade, and the bot met it there for the stall.
            double distanceCm = 0;
            var approach = pair.IsFirst
                ? pair.PartnerName != null ? NpcApproach.InReach : NpcApproach.None
                : !LeaderArrived(pair) ? NpcApproach.None
                : context.ApproachPartner(context.Behaviour.PairReachCm, ref command, out distanceCm);
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
                return context.Failed(pair.PartnerName == null || pair.IsFirst
                    ? $"partner never posted its name and position within pairWaitMs {waitMs} ms"
                    : $"partner '{pair.PartnerName}' did not arrive at '{_point}' to be met there within pairWaitMs {waitMs} ms");
            }
            if (!pair.IsFirst && !PartnerStill(context))
            {
                // Beside a partner still walking: it follows, and arrives once the partner stopped. PR #254 review 1: a leader that
                // never stands still (fighting at a camp) fails the meeting after pairWaitMs instead of the step's own timeout.
                if (_stillWaitFromMs < 0)
                    _stillWaitFromMs = context.NowMs;
                else if (context.NowMs - _stillWaitFromMs > waitMs)
                {
                    context.Count("meet.notStill");
                    return context.Failed($"partner '{pair.PartnerName}' never stood still for StillMs {StillMs} ms at '{_point}' "
                        + $"within pairWaitMs {waitMs} ms");
                }
                return BotStepStatus.Running;
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
