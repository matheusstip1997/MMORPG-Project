using BotClient.Brain;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// Dying in the field (spec 4.3 R-4.3-03, M4 decision 8; WP-M4-9): <c>die[:&lt;camp&gt;]</c> walks to the camp's spawn marker (a
/// field zone ring, where the region's <c>deathDrop</c> is on) and stands there without fighting or dodging until its monsters
/// kill the bot (without a camp it stands where it is). Done once the server lists a new own loot bag (<c>OwnLootBags</c>, sent
/// after the death's save committed); the bag's spot is posted on the pair board (<c>bag.at</c>: <c>entity,x,y</c>, cm) for a partner
/// whose <c>recoverBag:partner</c> takes from it. Counters: <c>die.deaths</c>, <c>die.bags</c>. It fails when no monster killed
/// it within <c>BotBehaviour.dieWaitMs</c>, or when it died and no bag came within that time (an empty Normal bag, a region
/// without drops, the world's bag cap). <c>die:&lt;camp&gt;:dev</c> sends the dev console's <c>/die</c> once at the camp instead of
/// waiting for its monsters (a seeded bot the camp cannot kill; needs a server with the dev console, like <c>dev:...</c>).
/// </summary>
public sealed class DieStep : IBotStep
{
    private const int CampReachCm = 200;

    private readonly string _camp;
    private readonly bool _dev;
    private bool _devSent;
    private long _deathsAtBegin;
    private long _startedAtMs;
    private long _diedAtMs;
    private uint _newestBefore;

    public DieStep(string argument = "")
    {
        _dev = argument.EndsWith(":dev", StringComparison.Ordinal);
        _camp = _dev ? argument.Substring(0, argument.Length - 4) : argument;
    }

    public string Id => "die";

    public void Begin(BotStepContext context)
    {
        _deathsAtBegin = context.Counters.Deaths;
        _startedAtMs = context.NowMs;
        _diedAtMs = 0;
        _devSent = false;
        _newestBefore = context.LootBags?.NewestOwn?.BagEntityId ?? 0;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (context.LootBags is not { } bags)
            return context.NotAvailable(Id, "no loot bag client");
        if (_camp.Length > 0 && !context.Data.Markers.ContainsKey(_camp))
            return context.NotAvailable(Id, $"no spawn marker '{_camp}'");
        int waitMs = context.Behaviour.DieWaitMs;
        if (_diedAtMs == 0 && context.Counters.Deaths > _deathsAtBegin)
        {
            _diedAtMs = context.NowMs;
            context.Count(Id + ".deaths");
        }
        if (_diedAtMs != 0)
        {
            if (bags.NewestOwn is { } bag && bag.BagEntityId != _newestBefore)
            {
                context.Count(Id + ".bags");
                context.Pair?.Post("bag.at", $"{bag.BagEntityId},{bag.XCm},{bag.YCm}");
                context.Log($"die: own loot bag {bag.BagEntityId} with {bag.Units} units at ({bag.XCm}, {bag.YCm})");
                return BotStepStatus.Done;
            }
            return context.NowMs - _diedAtMs > waitMs ? context.Failed("died but no loot bag dropped") : BotStepStatus.Running;
        }
        if (context.NowMs - _startedAtMs > waitMs)
            return context.Failed($"not killed within {waitMs} ms");
        if (context.View.Dead)
            return BotStepStatus.Running; // the death counter follows
        if (_camp.Length > 0)
        {
            var camp = context.Data.Markers[_camp];
            if (BackOffLeg.DistanceCm(context.View.Self, camp) > CampReachCm)
            {
                command = context.Walk(camp, command);
                return BotStepStatus.Running;
            }
        }
        command = default; // stand: no attack, no dodge, the monsters do the rest
        if (_dev && !_devSent)
        {
            _devSent = context.SendReliable(ChatCodec.WriteSend(new ReliableMessageWriter(), new ChatSendMessage { Text = "/die" }).ToArray());
            if (_devSent)
                context.Log("die: > /die");
        }
        return BotStepStatus.Running;
    }
}
