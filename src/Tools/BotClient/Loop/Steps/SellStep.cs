using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's sell step (WP-M2-4; spec 4.2 flow "Selling a sell-only item at an outpost"). With sell-only stacks in the
/// Normal bag (not stolen, R-4.1-01, R-4.4-02; read from the kept <c>InventoryContents</c>) it walks to the nearest outpost
/// NPC that offers the sale, opens it (<c>OutpostInteractRequest</c> Sell, which counts the visit), then per stack asks for
/// a quote (<c>SellQuoteRequest</c>, units priced one after the other, R-4.2-02) and sells for the quoted total
/// (<c>SellRequest</c>, a class 1 operation answered once committed). A <c>QuoteExpired</c> (the price fell since the quote)
/// quotes again, <c>loopSellQuoteRetries</c> times at most, then the stack is skipped.
/// Counters: <c>sell.units</c>, <c>sell.gold</c>, <c>sell.quoteExpired</c>, <c>sell.skipped</c>, <c>sell.refused.&lt;reason&gt;</c>;
/// done at once with <c>sell.nothingToSell</c> (nothing to sell, no walk) or <c>sell.noNpc</c>.
/// </summary>
public sealed class SellStep : IBotStep
{
    private readonly List<InventorySlotEntry> _stacks = [];
    private Phase _phase;
    private int _stack;
    private int _retries;
    private uint _npc;
    private uint _pending;

    private enum Phase : byte
    {
        Approach,
        Opening,
        Quoting,
        Selling,
    }

    public string Id => "sell";

    public void Begin(BotStepContext context)
    {
        _stacks.Clear();
        foreach (var slot in context.Inventory.Contents?.Slots ?? [])
        {
            if (slot.Count > 0 && !slot.Stolen && context.Item(slot.ItemNetId) is { Category: ItemCategory.SellOnly })
                _stacks.Add(slot);
        }
        _phase = Phase.Approach;
        _stack = 0;
        _retries = 0;
        _npc = 0;
        _pending = 0;
    }

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        var outposts = context.Outposts;
        switch (_phase)
        {
            case Phase.Approach:
                if (_stacks.Count == 0)
                {
                    context.Count("sell.nothingToSell");
                    return BotStepStatus.Done;
                }
                switch (context.ApproachNpc(npc => npc.Offers(NpcInteraction.Sell), ref command, out _npc, out _))
                {
                    case NpcApproach.None:
                        context.Count("sell.noNpc");
                        return BotStepStatus.Done;
                    case NpcApproach.Walking:
                        return BotStepStatus.Running;
                }
                context.SendReliable(outposts.InteractRequest(_npc, NpcInteraction.Sell, out _pending));
                _phase = Phase.Opening;
                return BotStepStatus.Running;

            case Phase.Opening:
                if (outposts.LastInteract is not { } opened || opened.RequestId != _pending)
                    return BotStepStatus.Running;
                if (opened.Reason == OutpostReason.OutOfRange)
                {
                    Refused(context, opened.Reason);
                    _phase = Phase.Approach; // the server had it a step farther: walk on and ask again
                    return BotStepStatus.Running;
                }
                if (opened.Reason != OutpostReason.None)
                {
                    Refused(context, opened.Reason);
                    return BotStepStatus.Failed;
                }
                return Quote(context);

            case Phase.Quoting:
                if (outposts.LastQuote is not { } quote || quote.RequestId != _pending)
                    return BotStepStatus.Running;
                if (quote.Reason != OutpostReason.None || quote.Total <= 0)
                {
                    Refused(context, quote.Reason == OutpostReason.None ? OutpostReason.NotSellable : quote.Reason);
                    return Next(context);
                }
                var stack = _stacks[_stack];
                context.SendReliable(outposts.SellRequest(_npc, stack.Slot, stack.Count, quote.Total, out _pending));
                _phase = Phase.Selling;
                return BotStepStatus.Running;

            default: // Selling
                if (outposts.LastSale is not { } sale || sale.RequestId != _pending)
                    return BotStepStatus.Running;
                if (sale.Reason == OutpostReason.None)
                {
                    context.Count("sell.units", sale.Count);
                    context.Count("sell.gold", sale.Gold);
                    return Next(context);
                }
                if (sale.Reason == OutpostReason.QuoteExpired)
                {
                    context.Count("sell.quoteExpired");
                    if (_retries < context.Behaviour.LoopSellQuoteRetries)
                    {
                        _retries++;
                        return Quote(context);
                    }
                    context.Count("sell.skipped");
                    return Next(context);
                }
                Refused(context, sale.Reason);
                return Next(context);
        }
    }

    private BotStepStatus Quote(BotStepContext context)
    {
        var stack = _stacks[_stack];
        context.SendReliable(context.Outposts.QuoteRequest(_npc, stack.Slot, stack.Count, out _pending));
        _phase = Phase.Quoting;
        return BotStepStatus.Running;
    }

    private BotStepStatus Next(BotStepContext context)
    {
        _stack++;
        _retries = 0;
        return _stack < _stacks.Count ? Quote(context) : BotStepStatus.Done;
    }

    private static void Refused(BotStepContext context, OutpostReason reason) => context.Count("sell.refused." + reason);
}
