using System.Globalization;
using BotClient.Brain;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;

namespace BotClient.Loop.Steps;

/// <summary>
/// The pair's trade step (WP-M4-4; spec 4.5 "M4 trade window", M4 decision 5): <c>trade:&lt;give&gt;:&lt;for&gt;</c>, each side
/// <c>gold*&lt;n&gt;</c>, <c>&lt;ItemDef id&gt;*&lt;n&gt;</c> (the count defaults to 1) or <c>-</c> (nothing). Both bots of a pair
/// (<see cref="BotPairs"/>) run it with mirrored arguments: <c>trade:m1.demonAsh*4:gold*50</c> and <c>trade:gold*50:m1.demonAsh*4</c>.
/// The leading bot walks within reach of its partner and invites; the other accepts. Each offers its <c>give</c>, locks once the
/// partner's offer holds its <c>for</c>, and confirms once both locked; the confirm is answered after both saves committed.
/// Counters: <c>trade.done</c>, <c>trade.gold</c>, <c>trade.units</c>, <c>trade.refused.&lt;reason&gt;</c> (failed),
/// <c>trade.ended.&lt;reason&gt;</c> (failed), <c>trade.retry.&lt;reason&gt;</c> (a busy partner or the cooldown: asked again).
/// Not available while the partner has not shown up (<c>partner needed</c>: WP-M4-12 runs the pairs).
/// </summary>
public sealed class TradeStep : IBotStep
{
    private readonly Side _give;
    private readonly Side _for;
    private uint _pending;
    private uint _tradeId;
    private long _retryAtMs;
    private long _startedAtMs;
    private bool _opened;

    public TradeStep(string argument)
    {
        int colon = argument.IndexOf(':');
        if (colon <= 0 || colon == argument.Length - 1)
            throw new ArgumentException($"trade:{argument}: expected trade:<give>:<for> (gold*<n>, <item>*<n> or -)", nameof(argument));
        _give = Side.Parse(argument[..colon]);
        _for = Side.Parse(argument[(colon + 1)..]);
    }

    public string Id => "trade";

    public void Begin(BotStepContext context)
    {
        _pending = 0;
        _tradeId = 0;
        _retryAtMs = 0;
        _opened = false;
        _startedAtMs = context.NowMs;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        var trade = context.Trade;
        if (context.Entities.OwnEntityId is uint self)
            BotPairs.Publish(context.BotIndex, self);
        int partnerIndex = BotPairs.PartnerOf(context.BotIndex);
        uint partner = BotPairs.EntityOf(partnerIndex);

        if (_pending != 0)
        {
            if (trade.ResultFor(_pending) is not { } answer)
                return BotStepStatus.Running;
            _pending = 0;
            switch (answer.Reason)
            {
                case TradeReason.None:
                    break;
                case TradeReason.TradeChanged: // the partner changed its offer meanwhile: look again
                    context.Count("trade.retry." + answer.Reason);
                    break;
                case TradeReason.TradeBusy or TradeReason.TradeCooldown or TradeReason.TradeTooFar:
                    context.Count("trade.retry." + answer.Reason);
                    _retryAtMs = context.NowMs + RetryMs(context);
                    break;
                default:
                    context.Count("trade.refused." + answer.Reason);
                    return context.Failed($"trade {answer.Action} refused: {answer.Reason}");
            }
        }

        var state = trade.State;
        if (state != null && _tradeId != 0 && state.TradeId == _tradeId)
        {
            if (state.Phase == TradePhase.Done)
            {
                context.Count("trade.done");
                context.Count("trade.gold", state.PartnerGold - state.OwnGold);
                context.Count("trade.units", state.PartnerOffer.Sum(o => o.Count) - state.OwnOffer.Sum(o => o.Count));
                return BotStepStatus.Done;
            }
            if (state.Phase == TradePhase.Cancelled)
            {
                context.Count("trade.ended." + state.Reason);
                if (_opened) // the window was open: a real end of the trade
                    return context.Failed($"trade ended: {state.Reason}");
                _tradeId = 0; // an invite that lapsed or was declined (the partner was busy with another step): ask again
                _retryAtMs = context.NowMs + RetryMs(context);
            }
        }
        if (context.NowMs < _retryAtMs)
            return BotStepStatus.Running;

        switch (state?.Phase)
        {
            case TradePhase.Invited:
                _tradeId = state.TradeId;
                return Send(context, trade.Accept(out _pending));
            case TradePhase.Inviting:
                _tradeId = state.TradeId;
                return BotStepStatus.Running;
            case TradePhase.Open:
                _opened = true;
                _tradeId = state.TradeId;
                return Offer(context, trade, state);
            case TradePhase.Settling:
                return BotStepStatus.Running;
        }

        // No trade under way: the leading bot walks to its partner and invites; the other waits for the invite.
        if (partner == 0 || !context.Entities.Entities.TryGetValue(partner, out var other) || !other.Samples.TryNewest(out var sample))
        {
            if (context.NowMs - _startedAtMs > PartnerWaitMs(context))
                return context.NotAvailable(Id, "partner needed: no pair partner in sight (WP-M4-12 runs the bots in pairs)");
            return BotStepStatus.Running;
        }
        if (!BotPairs.Leads(context.BotIndex))
            return BotStepStatus.Running;
        var target = FixVec2.FromCm(sample.XCm, sample.ZCm); // snapshot axes: Z is ground Y
        int reach = (context.Data.Tables.TryGet(typeof(TradeSettings), out var table) && table.Rows.Count == 1
            ? ((TradeSettings)table.Rows[0]).RangeCm : 0) / 2;
        long dx = (long)sample.XCm - FixPoint(context.View.Confirmed.X), dy = (long)sample.ZCm - FixPoint(context.View.Confirmed.Y);
        if (dx * dx + dy * dy > (long)reach * reach)
        {
            command = context.Walk(target, command);
            return BotStepStatus.Running;
        }
        return Send(context, trade.Invite(partner, out _pending));
    }

    /// <summary>Offers the own side, then locks once the partner's offer holds what this side wants, then confirms.</summary>
    private BotStepStatus Offer(BotStepContext context, ClientTrade trade, TradeState state)
    {
        if (_give.IsGold && state.OwnGold != _give.Count)
            return Send(context, trade.SetGold(_give.Count, out _pending));
        if (_give.ItemId != null && Offered(context, state.OwnOffer, _give) < _give.Count)
        {
            if (FindUnits(context, _give.ItemId, _give.Count - Offered(context, state.OwnOffer, _give)) is not { } at)
            {
                context.Count("trade.noItem");
                return context.Failed($"trade: {_give.ItemId} x{_give.Count} not held");
            }
            return Send(context, trade.Add(at.Container, at.Slot, at.Count, out _pending));
        }
        bool partnerReady = _for.IsGold ? state.PartnerGold >= _for.Count
            : _for.ItemId == null || Offered(context, state.PartnerOffer, _for) >= _for.Count;
        if (!partnerReady)
            return BotStepStatus.Running;
        if (!state.OwnLocked)
            return Send(context, trade.Lock(out _pending));
        if (state.PartnerLocked && !state.OwnConfirmed)
            return Send(context, trade.Confirm(out _pending)); // answered after both saves committed (rule 5)
        return BotStepStatus.Running;
    }

    private static BotStepStatus Send(BotStepContext context, byte[] message)
    {
        context.SendReliable(message);
        return BotStepStatus.Running;
    }

    private static int Offered(BotStepContext context, TradeOffer[] offers, Side side) =>
        Item(context, side.ItemId!) is { } item
            ? ClientTrade.Units(offers, (ushort)item.NetId)
            : 0;

    private static ItemDef? Item(BotStepContext context, string id) =>
        context.Data.Rows<ItemDef>().FirstOrDefault(row => row.Id == id);

    /// <summary>The first committed, unreserved stack or instance of the item with units left to offer (Normal bag first).</summary>
    private static (byte Container, ushort Slot, ushort Count)? FindUnits(BotStepContext context, string itemId, int wanted)
    {
        if (context.Inventory.Contents is not { } contents || Item(context, itemId) is not { } item)
            return null;
        byte normal = (byte)(ContainerDef.Find(context.Data.Tables, ContainerDef.NormalBag)?.Code ?? 0);
        foreach (var entry in contents.Slots)
        {
            if (entry.ItemNetId == item.NetId && entry.Count > 0 && !entry.Reserved && !entry.Stolen)
                return (normal, entry.Slot, (ushort)Math.Min(wanted, entry.Count));
        }
        foreach (var container in contents.Containers)
        {
            foreach (var entry in container.Slots)
            {
                if (entry.ItemNetId == item.NetId && entry.Count > 0 && !entry.Reserved && !entry.Stolen && !entry.NonTradable)
                    return (container.Code, entry.Slot, (ushort)Math.Min(wanted, entry.Count));
            }
        }
        return null;
    }

    private static int FixPoint(int raw) => FixedPoint.ToCmRound(raw);

    /// <summary>A busy partner or the cooldown: ask again after the trade's cooldown (<c>TradeSettings.cancelCooldownMs</c>).</summary>
    private static int RetryMs(BotStepContext context) =>
        context.Data.Tables.TryGet(typeof(TradeSettings), out var table) && table.Rows.Count == 1
            ? Math.Max(((TradeSettings)table.Rows[0]).CancelCooldownMs, 500)
            : 5_000;

    /// <summary>How long to look for the partner before "not available": the invite timeout of the table.</summary>
    private static int PartnerWaitMs(BotStepContext context) =>
        context.Data.Tables.TryGet(typeof(TradeSettings), out var table) && table.Rows.Count == 1
            ? ((TradeSettings)table.Rows[0]).InviteTimeoutMs
            : 30_000;

    /// <summary>One side of the argument: gold, an item, or nothing.</summary>
    private readonly record struct Side(string? ItemId, bool IsGold, int Count)
    {
        public static Side Parse(string text)
        {
            text = text.Trim();
            if (text is "-" or "")
                return new Side(null, false, 0);
            int star = text.LastIndexOf('*');
            string what = star < 0 ? text : text[..star];
            int count = star < 0 ? 1 : int.Parse(text[(star + 1)..], NumberStyles.None, CultureInfo.InvariantCulture);
            if (count <= 0)
                throw new ArgumentException($"trade side '{text}': the count must be positive");
            return what == "gold" ? new Side(null, true, count) : new Side(what, false, count);
        }
    }
}
