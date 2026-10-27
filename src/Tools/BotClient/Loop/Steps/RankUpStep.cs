using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's rank-up step (WP-M2-3, R-3.6-04): ranks are not automatic, and a bot that never ranks up falls into the band
/// dead zone above rank F's levels (R-3.6-02/03). The requirements are achievement counters only the server holds
/// (<c>ProgressionState</c> carries the rank, not the counters), so the bot asks as a player would: it walks to the
/// nearest outpost NPC that offers rankUp and sends <c>OutpostInteractRequest</c> RankUp. It asks once per rank, and again
/// only after a level-up since the last <c>RankRequirementsNotMet</c> at that rank (about one request per level, never per
/// round); after <c>RankMaxed</c> never again. Otherwise it is done at once.
/// Counters: <c>rankUp.ranks</c> (accepted; the new rank comes in the next <c>ProgressionState</c>), <c>rankUp.notMet</c>,
/// <c>rankUp.refused.&lt;reason&gt;</c>; done at once with <c>rankUp.waiting</c>, <c>rankUp.noNpc</c>.
/// </summary>
public sealed class RankUpStep : IBotStep
{
    private Rank? _askedRank;
    private int _askedLevel;
    private bool _maxed;
    private uint _pending;

    public string Id => "rankUp";

    public void Begin(BotStepContext context) => _pending = 0;

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_pending != 0)
            return Answer(context);
        var state = context.Progression.State;
        if (state == null || _maxed || (_askedRank == state.Rank && state.Level <= _askedLevel))
        {
            context.Count("rankUp.waiting");
            return BotStepStatus.Done;
        }
        switch (context.ApproachNpc(npc => npc.Offers(NpcInteraction.RankUp), ref command, out uint npcEntityId, out _))
        {
            case NpcApproach.None:
                context.Count("rankUp.noNpc");
                return BotStepStatus.Done;
            case NpcApproach.Walking:
                return BotStepStatus.Running;
        }
        _askedRank = state.Rank;
        _askedLevel = state.Level;
        context.SendReliable(context.Outposts.InteractRequest(npcEntityId, NpcInteraction.RankUp, out _pending));
        return BotStepStatus.Running;
    }

    private BotStepStatus Answer(BotStepContext context)
    {
        if (context.Outposts.LastInteract is not { } answer || answer.RequestId != _pending)
            return BotStepStatus.Running;
        _pending = 0;
        switch (answer.Reason)
        {
            case OutpostReason.None:
                context.Count("rankUp.ranks");
                context.Log($"ranked up from {_askedRank} at level {_askedLevel}");
                return BotStepStatus.Done;
            case OutpostReason.RankRequirementsNotMet:
                context.Count("rankUp.notMet");
                return BotStepStatus.Done;
            case OutpostReason.RankMaxed:
                _maxed = true;
                context.Count("rankUp.maxed");
                return BotStepStatus.Done;
            case OutpostReason.OutOfRange:
                _askedRank = null; // the server had it a step farther: ask again next round
                break;
        }
        context.Count("rankUp.refused." + answer.Reason);
        return BotStepStatus.Failed;
    }
}
