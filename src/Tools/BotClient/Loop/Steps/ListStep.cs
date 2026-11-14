using System.Globalization;
using BotClient.Brain;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The auction house (spec 4.5, M4 decision 6; WP-M4-7): <c>list:&lt;item&gt;[x&lt;n&gt;]:&lt;price&gt;[:&lt;duration&gt;]</c> walks to
/// the auctioneer and lists <c>n</c> units (default: the whole first Normal-bag stack of the item) at the unit price, for the
/// <c>ListingSettings.durationsMs</c> entry at the duration index (default 0); the units go into escrow and the listing fee is
/// paid through <c>FeeService</c>. <c>list:cancel:&lt;item&gt;</c> cancels the bot's own listing of the item; what is left comes
/// back by mail (claim it with <c>mail:claim</c>). The partner buys with <c>buy:listing:&lt;item&gt;</c>.
/// Outcomes come only from the server's answers after the commit. Counters: <c>list.created</c>, <c>list.units</c>,
/// <c>list.fee</c>, <c>list.cancelled</c>, <c>list.refused.&lt;reason&gt;</c>; failed with <c>list.noItem</c> (nothing to list),
/// <c>list.noNpc</c> (no auctioneer reached), <c>list.cancel.none</c> (no own listing of the item).
/// </summary>
public sealed class ListStep : IBotStep
{
    private const string CancelPrefix = "cancel:";

    private readonly string _argument;
    private readonly string _itemId = "";
    private readonly int _count;
    private readonly long _unitPrice;
    private readonly int _durationIndex;
    private readonly bool _cancel;
    private readonly string? _parseError;
    private uint _pending;
    private uint _minePending;
    private uint _npc;
    private int _mailBefore = -1;
    private long _mailWaitUntilMs;

    /// <summary>
    /// After a cancel, the returned units reach the mailbox a moment later (the release to mail commits, then the mailbox reloads
    /// off the tick): the step waits for the open mail count to rise, at most this long, so a following <c>mail:claim</c> finds it.
    /// </summary>
    private const int ReturnMailWaitMs = 10_000;

    public ListStep(string argument = "")
    {
        _argument = argument;
        if (argument.StartsWith(CancelPrefix, StringComparison.Ordinal))
        {
            _cancel = true;
            _itemId = argument[CancelPrefix.Length..];
            if (_itemId.Length == 0)
                _parseError = "list:cancel:<item>";
            return;
        }
        var parts = argument.Split(':');
        if (parts.Length is < 2 or > 3 || !TryItem(parts[0], out _itemId, out _count)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _unitPrice) || _unitPrice <= 0
            || (parts.Length == 3 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _durationIndex)))
            _parseError = "list:<item>[x<n>]:<price>[:<duration>] or list:cancel:<item>";
    }

    public string Id => "list";

    public void Begin(BotStepContext context)
    {
        _pending = 0;
        _minePending = 0;
        _npc = 0;
        _mailBefore = -1;
        _mailWaitUntilMs = 0;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_parseError != null)
            return context.Failed($"bad argument '{_argument}': {_parseError}");
        if (context.Listings is not { } listings)
            return context.NotAvailable(Id, "no auction client in this context");
        var item = context.Data.Rows<ItemDef>().FirstOrDefault(i => i.Id == _itemId);
        if (item == null)
            return context.NotAvailable(Id, $"item '{_itemId}' not in ItemDef yet");
        if (_pending != 0)
            return Answer(context, listings);

        if (_minePending == 0)
        {
            switch (context.ApproachNpc(n => n.Offers(NpcInteraction.Auction), ref command, out _npc, out _))
            {
                case NpcApproach.None:
                    context.Count("list.noNpc");
                    return context.Failed("list.noNpc: no auctioneer reached");
                case NpcApproach.Walking:
                    return BotStepStatus.Running;
            }
        }
        if (_cancel)
            return Cancel(context, listings, item);

        var slot = context.Inventory.Contents?.Slots.FirstOrDefault(s => s.ItemNetId == item.NetId && !s.Reserved);
        if (slot == null)
        {
            context.Count("list.noItem");
            return context.Failed($"list.noItem: no {_itemId} in the Normal bag");
        }
        int count = _count > 0 ? Math.Min(_count, slot.Count) : slot.Count;
        byte normal = (byte)(ContainerDef.Find(context.Data.Tables, ContainerDef.NormalBag)?.Code ?? 0);
        context.SendReliable(listings.CreateRequest(_npc, normal, slot.Slot, count, _unitPrice, _durationIndex, out _pending));
        return BotStepStatus.Running;
    }

    private BotStepStatus Cancel(BotStepContext context, ClientListings listings, ItemDef item)
    {
        if (_minePending == 0)
        {
            context.SendReliable(listings.MineRequest(_npc, out _minePending));
            return BotStepStatus.Running;
        }
        if (listings.MineFor(_minePending) is not { } mine)
            return BotStepStatus.Running;
        var own = mine.Rows.FirstOrDefault(r => r.Item.ItemNetId == item.NetId);
        if (mine.Reason != ListingReason.None || own == null)
        {
            context.Count("list.cancel.none");
            return context.Failed($"list.cancel.none: no own listing of {_itemId} ({mine.Reason})");
        }
        _mailBefore = context.Mail?.OpenCount ?? -1;
        context.SendReliable(listings.CancelRequest(_npc, own.ListingId, out _pending));
        return BotStepStatus.Running;
    }

    private BotStepStatus Answer(BotStepContext context, ClientListings listings)
    {
        if (listings.ResultFor(_pending) is not { } result)
            return BotStepStatus.Running;
        if (result.Reason != ListingReason.None)
        {
            context.Count("list.refused." + result.Reason);
            return context.Failed("list.refused." + result.Reason);
        }
        if (result.Action == ListingAction.Cancel)
        {
            if (_mailBefore >= 0 && context.Mail != null && context.Mail.OpenCount <= _mailBefore)
            {
                if (_mailWaitUntilMs == 0)
                    _mailWaitUntilMs = context.NowMs + ReturnMailWaitMs;
                if (context.NowMs < _mailWaitUntilMs)
                    return BotStepStatus.Running; // the return mail is on its way
                context.Count("list.cancel.mailLate");
            }
            context.Count("list.cancelled");
        }
        else
        {
            context.Count("list.created");
            context.Count("list.units", result.Count);
            context.Count("list.fee", result.Fee);
        }
        return BotStepStatus.Done;
    }

    /// <summary><c>item</c> or <c>item x n</c> (written <c>itemxN</c>, as the other pair steps write counts).</summary>
    internal static bool TryItem(string text, out string itemId, out int count)
    {
        itemId = text;
        count = 0;
        int x = text.LastIndexOf('x');
        if (x > 0 && x < text.Length - 1 && int.TryParse(text.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0)
        {
            itemId = text[..x];
            count = n;
        }
        return itemId.Length > 0;
    }
}
