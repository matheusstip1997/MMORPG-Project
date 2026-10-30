using BotClient.Brain;
using Shared.Data.Tables;
using Shared.Protocol;

namespace BotClient.Loop.Steps;

/// <summary>
/// The loop's buy step (WP-M2-4; spec 4.2, R-4.2-01: vendors sell at fixed prices and the gold is destroyed). It tops the
/// behaviour's <c>loopBuyItemId</c> (a potion) in the Normal bag up to <c>loopBuyKeep</c> units, as far as the committed gold
/// (<c>GoldBalance</c>, kept by the client) pays: it walks to the nearest vendor whose <c>NpcVendorStock</c> has the item
/// and sends one <c>VendorBuyRequest</c> for the fixed price times the count.
/// Counters: <c>buy.units</c>, <c>buy.gold</c>, <c>buy.refused.&lt;reason&gt;</c>; done at once with <c>buy.stocked</c>
/// (enough held), <c>buy.noGold</c> (the gold does not pay one unit, no walk), <c>buy.noNpc</c>, or <c>buy.off</c> (no item set).
/// <para>
/// With an argument (<c>buy:m3.pickaxeT1</c>, WP-M3-12) it makes sure the bot holds at least one of that item in any
/// container (a tool lands in the Safe bag equipment area): done when held (<c>buy.stocked</c>) or bought; <b>failed</b>
/// when the gold does not pay (<c>buy.noGold</c>) or no vendor is reached (<c>buy.noNpc</c>), since the entry asks for it;
/// not yet while the item or a vendor stocking it is not in the tables.
/// </para>
/// </summary>
public sealed class BuyStep : IBotStep
{
    private readonly string _argument;
    private uint _pending;

    public BuyStep(string argument = "")
    {
        _argument = argument;
    }

    public string Id => "buy";

    public void Begin(BotStepContext context) => _pending = 0;

    public BotStepStatus Tick(BotStepContext context, ref BotCommand command)
    {
        if (_pending != 0)
            return Answer(context);

        bool required = _argument.Length > 0;
        string itemId = required ? _argument : context.Behaviour.LoopBuyItemId;
        var item = itemId.Length == 0 ? null : context.Data.Rows<ItemDef>().FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            if (required)
                return context.NotAvailable(Id, $"item '{itemId}' not in ItemDef yet");
            context.Count("buy.off");
            return BotStepStatus.Done;
        }
        int held = 0;
        var contents = context.Inventory.Contents;
        IEnumerable<InventorySlotEntry> slots = contents?.Slots ?? [];
        if (required && contents != null)
            slots = slots.Concat(contents.Containers.SelectMany(c => c.Slots));
        foreach (var slot in slots)
        {
            if (slot.ItemNetId == item.NetId)
                held += slot.Count;
        }
        int missing = (required ? 1 : context.Behaviour.LoopBuyKeep) - held;
        if (missing <= 0)
        {
            context.Count("buy.stocked");
            return BotStepStatus.Done;
        }

        var stock = context.Data.Rows<NpcVendorStock>().Where(s => s.ItemId == itemId).ToList();
        if (required && stock.Count == 0)
            return context.NotAvailable(Id, $"no NpcVendorStock row sells '{itemId}' yet");
        BotStepStatus NotDone(string why) => required ? context.Failed($"{why} ({itemId})") : BotStepStatus.Done;
        long gold = context.Outposts.Gold ?? 0;
        if (stock.Count > 0 && gold < stock.Min(s => s.PriceGold))
        {
            context.Count("buy.noGold");
            return NotDone($"buy.noGold: {gold} gold, {stock.Min(s => s.PriceGold)} needed");
        }
        switch (context.ApproachNpc(npc => npc.Offers(NpcInteraction.Buy) && stock.Any(s => s.NpcId == npc.Id), ref command,
                    out uint npcEntityId, out var vendor))
        {
            case NpcApproach.None:
                context.Count("buy.noNpc");
                return NotDone("buy.noNpc: no vendor of it reached");
            case NpcApproach.Walking:
                return BotStepStatus.Running;
        }
        int price = stock.First(s => s.NpcId == vendor!.Id).PriceGold;
        int count = (int)Math.Min(Math.Min(missing, gold / price), item.MaxStack);
        if (count <= 0)
        {
            context.Count("buy.noGold");
            return NotDone($"buy.noGold: {gold} gold, {price} needed");
        }
        context.SendReliable(context.Outposts.BuyRequest(npcEntityId, (ushort)item.NetId, (ushort)count, (long)price * count, out _pending));
        return BotStepStatus.Running;
    }

    private BotStepStatus Answer(BotStepContext context)
    {
        if (context.Outposts.LastPurchase is not { } purchase || purchase.RequestId != _pending)
            return BotStepStatus.Running;
        if (purchase.Reason != OutpostReason.None)
        {
            context.Count("buy.refused." + purchase.Reason);
            return context.Failed("buy.refused." + purchase.Reason);
        }
        context.Count("buy.units", purchase.Count);
        context.Count("buy.gold", purchase.GoldSpent);
        return BotStepStatus.Done;
    }
}
