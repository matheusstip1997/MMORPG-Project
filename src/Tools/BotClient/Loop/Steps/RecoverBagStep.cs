using BotClient.Brain;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;

namespace BotClient.Loop.Steps;

/// <summary>
/// Death loot bags (spec 4.3 R-4.3-03, 4.4 R-4.4-01, M4 decision 8; WP-M4-9), through ClientCore's <see cref="ClientLootBags"/>:
/// <list type="bullet">
/// <item><c>recoverBag</c>: after the respawn, walks back to the newest own bag (<c>OwnLootBags</c>, at any distance) and takes
/// everything (<c>LootBagRequest</c> take all; the owner's take puts the units back in the Normal bag and mails what does not fit).
/// Done once the answer came after the commit and the bag is gone; counters <c>recoverBag.units</c>, <c>recoverBag.mailed</c>,
/// <c>recoverBag.inWindow</c> when the owner window was still running at the take.</item>
/// <item><c>recoverBag:expire</c>: leaves the newest own bag alone and is done once the server no longer lists it (it expired and
/// its items were destroyed, a sink): <c>recoverBag.expired</c>. The script's step timeout must outlast <c>BagExpiry.expiryMs</c>.</item>
/// <item><c>recoverBag:partner</c> (a pair leg after its own <c>meet:&lt;point&gt;</c>): walks to the spot the partner's <c>die</c>
/// posted (<c>bag.at</c>, a bag newer than the step), opens it and waits out the owner window (inside it a party member would
/// collect for the owner, and anyone else is <c>BagLocked</c>), then takes everything with the stolen mark;
/// done when the committed answer says <c>Stolen</c> (<c>recoverBag.stolen</c>, <c>recoverBag.units</c>), failed when it does not.</item>
/// </list>
/// Any other refusal fails with <c>recoverBag.refused.&lt;reason&gt;</c>.
/// </summary>
public sealed class RecoverBagStep : IBotStep
{
    private const int RetryMs = 1_000;
    private const int OpenWaitMs = 5_000;

    private readonly Mode _mode;
    private uint _target;
    private uint _pending;
    private long _retryAtMs;
    private bool _inWindow;
    private int _units;
    private FixVec2? _partnerSpot;
    private uint _staleBag;
    private long _openAskedAtMs;

    private enum Mode : byte
    {
        Own,
        Expire,
        Partner,
    }

    public RecoverBagStep(string argument = "")
    {
        _mode = argument switch
        {
            "" => Mode.Own,
            "expire" => Mode.Expire,
            "partner" => Mode.Partner,
            _ => throw new ArgumentException($"recoverBag step: '{argument}' is not expire or partner"),
        };
    }

    public string Id => "recoverBag";

    public void Begin(BotStepContext context)
    {
        _target = 0;
        _pending = 0;
        _retryAtMs = 0;
        _inWindow = false;
        _units = 0;
        _partnerSpot = null;
        _openAskedAtMs = long.MinValue / 2;
        _staleBag = context.Pair != null && context.Pair.TryRead("bag.at", out var posted) && TryParseSpot(posted, out uint stale, out _) ? stale : 0;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (context.LootBags is not { } bags)
            return context.NotAvailable(Id, "no loot bag client");
        if (_pending != 0)
            return bags.ResultFor(_pending) is { } answer ? OnAnswer(context, bags, answer) : BotStepStatus.Running;
        if (context.View.Dead)
            return BotStepStatus.Running; // the respawn comes first
        return _mode switch
        {
            Mode.Expire => Expire(context, bags),
            Mode.Partner => Partner(context, bags, ref command),
            _ => Own(context, bags, ref command),
        };
    }

    private BotStepStatus Own(BotStepContext context, ClientLootBags bags, ref BotCommand command)
    {
        if (bags.Own == null)
            return BotStepStatus.Running; // the own bags come at enter world
        if (_target == 0)
        {
            if (bags.NewestOwn is not { } newest)
                return context.Failed("no own loot bag to recover");
            _target = newest.BagEntityId;
            bags.MarkUsed(_target); // this step deals with it (the die step looks for newer ones)
        }
        if (bags.OwnBag(_target) is not { } bag)
            return _units > 0 ? BotStepStatus.Done : context.Failed("the own loot bag is gone before it was recovered");
        if (!Reach(context, FixVec2.FromCm(bag.XCm, bag.YCm), ref command) || !context.Entities.Entities.ContainsKey(_target))
            return BotStepStatus.Running;
        _inWindow = bag.OwnerWindowLeftMs > 0;
        context.SendReliable(bags.TakeAllRequest(_target, out _pending));
        return BotStepStatus.Running;
    }

    private BotStepStatus Expire(BotStepContext context, ClientLootBags bags)
    {
        if (bags.Own == null)
            return BotStepStatus.Running;
        if (_target == 0)
        {
            if (bags.NewestOwn is not { } newest)
                return context.Failed("no own loot bag to leave to expire");
            _target = newest.BagEntityId;
            bags.MarkUsed(_target);
            context.Log($"recoverBag: leaving bag {_target} ({newest.Units} units) to expire in {newest.ExpiresInMs} ms");
        }
        if (bags.OwnBag(_target) != null)
            return BotStepStatus.Running; // stand by: nothing to do until the server drops it
        context.Count(Id + ".expired");
        return BotStepStatus.Done;
    }

    private BotStepStatus Partner(BotStepContext context, ClientLootBags bags, ref BotCommand command)
    {
        if (context.Pair == null)
            return context.NotAvailable(Id, "partner needed");
        if (_partnerSpot == null)
        {
            // The partner's die step posts "entity,x,y" once its bag lies; a bag posted before this step began is an older one.
            if (!context.Pair.TryRead("bag.at", out var posted) || !TryParseSpot(posted, out uint entity, out var spot)
                || entity == _staleBag)
                return BotStepStatus.Running;
            _target = entity;
            _partnerSpot = spot;
        }
        if (!Reach(context, _partnerSpot.Value, ref command) || !context.Entities.Entities.ContainsKey(_target))
            return BotStepStatus.Running;
        if (context.NowMs < _retryAtMs)
            return BotStepStatus.Running;
        if (bags.Open is not { } open || open.BagEntityId != _target)
        {
            if (context.NowMs >= _openAskedAtMs + OpenWaitMs)
            {
                _openAskedAtMs = context.NowMs; // its contents follow the answer; asked again if they never came
                context.SendReliable(bags.OpenRequest(_target, out _pending));
            }
            return BotStepStatus.Running;
        }
        if (open.Access == LootBagAccess.TakeStolen)
        {
            context.SendReliable(bags.TakeAllRequest(_target, out _pending));
            return BotStepStatus.Running;
        }
        // Inside the owner window: a party member would collect for the owner (by mail, no mark). Wait it out, then look again.
        context.Count(Id + ".locked");
        _retryAtMs = context.NowMs + open.OwnerWindowLeftMs + RetryMs;
        _openAskedAtMs = long.MinValue / 2;
        context.SendReliable(bags.CloseRequest(_target, out _));
        return BotStepStatus.Running;
    }

    private static bool TryParseSpot(string posted, out uint entity, out FixVec2 spot)
    {
        entity = 0;
        spot = default;
        var parts = posted.Split(',');
        if (parts.Length != 3 || !uint.TryParse(parts[0], out entity) || !int.TryParse(parts[1], out int xCm)
            || !int.TryParse(parts[2], out int yCm))
            return false;
        spot = FixVec2.FromCm(xCm, yCm);
        return true;
    }

    private BotStepStatus OnAnswer(BotStepContext context, ClientLootBags bags, LootBagResult answer)
    {
        _pending = 0;
        if (answer.Op == LootBagOp.Open && answer.Reason == LootBagReason.None)
            return BotStepStatus.Running; // the contents follow
        switch (answer.Reason)
        {
            case LootBagReason.None:
                break;
            case LootBagReason.OutOfRange:
                return BotStepStatus.Running; // the server had it a step farther: walk on and ask again
            case LootBagReason.BagLocked when _mode == Mode.Partner:
                context.Count(Id + ".locked");
                _retryAtMs = context.NowMs + RetryMs; // the owner window: ask again once it ended
                return BotStepStatus.Running;
            case LootBagReason.BagEmpty when _units > 0:
                return BotStepStatus.Done;
            default:
                context.Count(Id + ".refused." + answer.Reason);
                return context.Failed($"loot bag take refused: {answer.Reason}");
        }
        _units += answer.Taken + answer.Mailed;
        context.Count(Id + ".units", answer.Taken);
        context.Count(Id + ".mailed", answer.Mailed);
        context.Log($"recoverBag: took {answer.Taken} units, {answer.Mailed} by mail{(answer.Stolen ? ", stolen" : "")}");
        if (_mode == Mode.Partner)
        {
            if (!answer.Stolen)
                return context.Failed("took the partner's bag without the stolen mark");
            context.Count(Id + ".stolen");
            return BotStepStatus.Done;
        }
        if (_inWindow)
            context.Count(Id + ".inWindow");
        return bags.OwnBag(_target) is { Units: > 0 } ? BotStepStatus.Running : BotStepStatus.Done;
    }

    /// <summary>Walks to <paramref name="spot"/> until within reach of a take (the pickup range, a margin kept); true once there.</summary>
    private static bool Reach(BotStepContext context, FixVec2 spot, ref BotCommand command)
    {
        int reach = context.Data.Rows<LootSettings>().Select(s => s.PickupRangeCm).FirstOrDefault() * 3 / 4;
        if (BackOffLeg.DistanceCm(context.View.Self, spot) <= reach && BackOffLeg.DistanceCm(context.View.Confirmed, spot) <= reach)
            return true;
        command = context.Walk(spot, command);
        return false;
    }

}
