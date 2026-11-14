using System.Globalization;
using BotClient.Brain;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;

namespace BotClient.Loop.Steps;

/// <summary>
/// Shop stalls (spec 4.5, M4 decision 7; WP-M4-8). <c>stall:open:&lt;item&gt;[x&lt;n&gt;]:&lt;price&gt;</c> walks to the nearest free stall
/// place of the city and opens a stall there with <c>n</c> units (default: the whole first Normal-bag stack of the item) at the unit
/// price: the units go into escrow and the listing fee is paid through the listing model; a place taken meanwhile is skipped
/// (<c>StallPlaceTaken</c>) and the next is tried. On success it posts <c>stall</c> = the place ordinal on the pair board, so the
/// partner finds it (<c>buy:stall</c>). <c>stall:close</c> walks back to the own stall and closes it; the unsold items come back by
/// mail (claim them with <c>mail:claim</c>). Outcomes come only from the server's answers after the commit. Counters:
/// <c>stall.opened</c>, <c>stall.fee</c>, <c>stall.closed</c>, <c>stall.refused.&lt;reason&gt;</c>; failed with <c>stall.noItem</c>,
/// <c>stall.noPlace</c> (no free place left), <c>stall.none</c> (no own stall to close).
/// </summary>
public sealed class StallStep : IBotStep
{
    /// <summary>The pair board key of the own stall's place ordinal (read by the partner's <c>buy:stall</c>).</summary>
    public const string PlaceKey = "stall";

    private readonly string _argument;
    private readonly string _itemId = "";
    private readonly int _count;
    private readonly long _unitPrice;
    private readonly bool _close;
    private readonly string? _parseError;
    private readonly HashSet<int> _taken = new();
    private uint _pending;
    private int _place = -1;
    private uint _ownStall;
    private int _ownPlace = -1;

    public StallStep(string argument = "")
    {
        _argument = argument;
        if (argument == "close")
        {
            _close = true;
            return;
        }
        var parts = argument.Split(':');
        if (parts.Length != 3 || parts[0] != "open" || !ListStep.TryItem(parts[1], out _itemId, out _count)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _unitPrice) || _unitPrice <= 0)
            _parseError = "stall:open:<item>[x<n>]:<price> or stall:close";
    }

    public string Id => "stall";

    public void Begin(BotStepContext context)
    {
        _pending = 0;
        _place = -1;
        _taken.Clear();
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_parseError != null)
            return context.Failed($"bad argument '{_argument}': {_parseError}");
        if (context.Stalls is not { } stalls)
            return context.NotAvailable(Id, "no stall client in this context");
        if (stalls.Places.Count == 0)
            return context.NotAvailable(Id, "no stall places in this world");
        if (_pending != 0)
            return Answer(context, stalls);
        return _close ? Close(context, stalls, ref command) : Open(context, stalls, ref command);
    }

    private BotStepStatus Open(BotStepContext context, ClientStalls stalls, ref BotCommand command)
    {
        var item = context.Data.Rows<ItemDef>().FirstOrDefault(i => i.Id == _itemId);
        if (item == null)
            return context.NotAvailable(Id, $"item '{_itemId}' not in ItemDef yet");
        var slot = context.Inventory.Contents?.Slots.FirstOrDefault(s => s.ItemNetId == item.NetId && !s.Reserved);
        if (slot == null)
        {
            context.Count("stall.noItem");
            return context.Failed($"stall.noItem: no {_itemId} in the Normal bag");
        }
        if (_place < 0 || _taken.Contains(_place) || ClientStalls.StallOn(context.Entities, _place) != 0)
        {
            _place = NearestFree(context, stalls);
            if (_place < 0)
            {
                context.Count("stall.noPlace");
                return context.Failed("stall.noPlace: every stall place is taken");
            }
        }
        var at = stalls.Places[_place];
        if (context.ApproachPoint(FixVec2.FromCm(at.XCm, at.YCm), stalls.InteractRangeCm, ref command) != NpcApproach.InReach)
            return BotStepStatus.Running;
        int count = _count > 0 ? Math.Min(_count, slot.Count) : slot.Count;
        byte normal = (byte)(ContainerDef.Find(context.Data.Tables, ContainerDef.NormalBag)?.Code ?? 0);
        context.SendReliable(stalls.OpenRequest(_place, normal, slot.Slot, count, _unitPrice, out _pending));
        return BotStepStatus.Running;
    }

    private BotStepStatus Close(BotStepContext context, ClientStalls stalls, ref BotCommand command)
    {
        uint stall = OwnStall(context, stalls, out int place);
        if (stall == 0 && place < 0)
        {
            context.Count("stall.none");
            return context.Failed("stall.none: no own stall to close");
        }
        var at = stalls.Places[place];
        if (context.ApproachPoint(FixVec2.FromCm(at.XCm, at.YCm), stalls.InteractRangeCm, ref command) != NpcApproach.InReach)
            return BotStepStatus.Running;
        if (stall == 0)
            stall = ClientStalls.StallOn(context.Entities, place);
        if (stall == 0)
        {
            context.Count("stall.none");
            return context.Failed("stall.none: the own stall is gone (sold out or its time ran out)");
        }
        context.SendReliable(stalls.CloseRequest(stall, out _pending));
        return BotStepStatus.Running;
    }

    private BotStepStatus Answer(BotStepContext context, ClientStalls stalls)
    {
        if (stalls.ResultFor(_pending) is not { } result)
            return BotStepStatus.Running;
        _pending = 0;
        if (result.Reason == StallReason.StallPlaceTaken && !_close)
        {
            context.Count("stall.refused." + result.Reason);
            _taken.Add(_place); // another bot was faster: the next free place
            _place = -1;
            return BotStepStatus.Running;
        }
        if (result.Reason != StallReason.None)
        {
            string why = result.Reason == StallReason.StallListingRefused ? result.ListingReason.ToString() : result.Reason.ToString();
            context.Count("stall.refused." + why);
            return context.Failed("stall.refused." + why);
        }
        if (_close)
        {
            context.Count("stall.closed");
            return BotStepStatus.Done;
        }
        context.Count("stall.opened");
        context.Count("stall.fee", result.Fee);
        _ownStall = result.StallEntityId;
        _ownPlace = _place;
        context.Pair?.Post(PlaceKey, _place.ToString(CultureInfo.InvariantCulture));
        return BotStepStatus.Done;
    }

    /// <summary>The own stall: this step's open, or the place the stall client remembers (another <c>stall:open</c> entry, a relogin).</summary>
    private uint OwnStall(BotStepContext context, ClientStalls stalls, out int place)
    {
        place = _ownPlace >= 0 ? _ownPlace : stalls.OwnPlace;
        return _ownStall != 0 ? _ownStall : stalls.OwnStallId;
    }

    private int NearestFree(BotStepContext context, ClientStalls stalls)
    {
        int best = -1;
        double bestCm = double.MaxValue;
        for (int i = 0; i < stalls.Places.Count; i++)
        {
            if (_taken.Contains(i) || ClientStalls.StallOn(context.Entities, i) != 0)
                continue;
            double cm = GridPath.DistanceCm(context.View.Self, FixVec2.FromCm(stalls.Places[i].XCm, stalls.Places[i].YCm));
            if (cm < bestCm)
            {
                best = i;
                bestCm = cm;
            }
        }
        return best;
    }
}
