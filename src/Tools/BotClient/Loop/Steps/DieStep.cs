using BotClient.Brain;
using Shared.Protocol;
using Shared.Simulation;

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
/// <para>
/// A bot also dies outside this step (a fight, a wait at a meeting point). An own bag no step dealt with yet is taken up: lying
/// at the camp, it is this step's death (<c>die.adopted</c>); lying elsewhere, it is taken back first (as <c>recoverBag</c>,
/// <c>die.recovered</c>), so the Normal bag has something to drop here (the m4-exit run 1: a death at a meeting point emptied
/// the bag before the step began).
/// </para>
/// </summary>
public sealed class DieStep : IBotStep
{
    private const int CampReachCm = 200;

    /// <summary>A bag this close to the camp counts as a death at the camp.</summary>
    private const int AdoptCm = 3_000;

    private readonly string _camp;
    private readonly bool _dev;
    private readonly RecoverBagStep _recover = new();
    private bool _recovering;
    private bool _devSent;
    private long _deathsAtBegin;
    private long _startedAtMs;
    private long _diedAtMs;

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
        _recovering = false;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (context.LootBags is not { } bags)
            return context.NotAvailable(Id, "no loot bag client");
        if (_camp.Length > 0 && !context.Data.Markers.ContainsKey(_camp))
            return context.NotAvailable(Id, $"no spawn marker '{_camp}'");
        if (bags.Own == null)
            return BotStepStatus.Running; // the own bags come at enter world
        int waitMs = context.Behaviour.DieWaitMs;
        if (_recovering)
        {
            var status = _recover.Tick(context, ref command);
            if (status == BotStepStatus.Running)
                return status;
            if (status != BotStepStatus.Done)
                return context.Failed("an earlier own bag could not be taken back: " + context.LastReason);
            _recovering = false;
            context.Count(Id + ".recovered");
            _deathsAtBegin = context.Counters.Deaths; // the deaths on the way back are not this step's
            _startedAtMs = context.NowMs;
        }
        if (_diedAtMs == 0 && context.Counters.Deaths > _deathsAtBegin)
        {
            _diedAtMs = context.NowMs;
            context.Count(Id + ".deaths");
        }
        var fresh = bags.NewestOwn is { } newest && !bags.IsUsed(newest.BagEntityId) ? newest : null;
        if (_diedAtMs != 0)
        {
            if (fresh != null)
                return Report(context, bags, fresh, adopted: false);
            return context.NowMs - _diedAtMs > waitMs ? context.Failed("died but no loot bag dropped") : BotStepStatus.Running;
        }
        if (fresh != null && !context.View.Dead)
        {
            if (_camp.Length == 0 || BackOffLeg.DistanceCm(FixVec2.FromCm(fresh.XCm, fresh.YCm), context.Data.Markers[_camp]) <= AdoptCm)
                return Report(context, bags, fresh, adopted: true);
            _recovering = true; // lying elsewhere: take it back first, then die here
            _recover.Begin(context);
            return BotStepStatus.Running;
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

    private BotStepStatus Report(BotStepContext context, Game.Gameplay.Entities.ClientLootBags bags, OwnLootBagEntry bag, bool adopted)
    {
        bags.MarkUsed(bag.BagEntityId);
        context.Count(Id + ".bags");
        if (adopted)
            context.Count(Id + ".adopted");
        context.Pair?.Post("bag.at", $"{bag.BagEntityId},{bag.XCm},{bag.YCm}");
        context.Log($"die: own loot bag {bag.BagEntityId} with {bag.Units} units at ({bag.XCm}, {bag.YCm}){(adopted ? ", dropped before the step" : "")}");
        return BotStepStatus.Done;
    }
}
