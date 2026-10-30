using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Net;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Outposts;

/// <summary>
/// Selling sell-only drops at an outpost (R-4.2-01, R-4.2-02; spec 4.2 flow "Selling a sell-only item at an outpost";
/// WP-M2-4), in phase 1 on the world thread. The only gold faucet in M2.
/// <list type="bullet">
/// <item>Quote: the price of <c>count</c> units from one bag slot, units priced one after the other
/// (<see cref="PriceService"/>), answered in the same tick.</item>
/// <item>Sale: a class 1 operation (R-7.1-01). The server checks the gate (NPC, range), that the stack is sell-only and not
/// stolen (R-4.4-02; the stolen flag is inert until M4, the check is here), recomputes the price, and refuses with
/// <c>QuoteExpired</c> when it is below the total the player confirmed (user decision 2026-10-27). Then, through
/// <see cref="InventoryService"/> (the only writer of items and gold, rule 4): remove the units (keys
/// <c>{op}:items</c>), add the gold (<c>{op}:gold</c>), register the sale with the demand loop, and ask for an
/// immediate save that carries the pending class 2 changes too (R-7.1-02). The player hears of it only once that save
/// committed (rule 5).</item>
/// <item>Operation key <c>sell:{runId}:{entityId}:{connection}:{requestId}</c>: unique for ever, and the same for a
/// repeated request on one connection, which gets the first answer and changes nothing (double submit).</item>
/// </list>
/// </summary>
public sealed class SaleHandler : IOutpostInteractionHandler
{
    private readonly InventoryService _inventory;
    private readonly PriceService _prices;
    private readonly OutpostInteractions _gate;
    private readonly string _runId;

    /// <param name="runId">Unique per world run, so operation keys never repeat across restarts.</param>
    public SaleHandler(InventoryService inventory, PriceService prices, OutpostInteractions gate, string runId)
    {
        _inventory = inventory;
        _prices = prices;
        _gate = gate;
        _runId = runId;
    }

    /// <summary>Opening the outpost's sale window: nothing to do but say yes (the gate already checked reach).</summary>
    public OutpostReason? Interact(Player player, Npc npc, uint requestId) =>
        _inventory.IsAttached(player.CharacterId) ? OutpostReason.None : OutpostReason.InventoryUnavailable;

    public string OpKey(Player player, uint requestId) => $"sell:{_runId}:{player.Id}:{player.Connection}:{requestId}";

    /// <summary>Phase 1: one <see cref="SellQuoteRequest"/>.</summary>
    internal void Quote(Player player, in ClientRequest request)
    {
        var answer = new SellQuoteResult { RequestId = request.RequestId, Slot = request.FromSlot, Count = request.Count };
        answer.Reason = Check(player, request, out var item, out byte rarity);
        if (answer.Reason == OutpostReason.None)
        {
            if (_prices.TryQuote(item!, rarity, request.Count, out var quote))
            {
                answer.Total = quote.Total;
                answer.FirstUnit = quote.FirstUnit;
                answer.LastUnit = quote.LastUnit;
            }
            else
            {
                answer.Reason = OutpostReason.NotSellable;
            }
        }
        player.Outposts.Quotes.Add(answer);
    }

    /// <summary>Phase 1: one <see cref="SellRequest"/>; a refusal is answered at once, a sale once it committed.</summary>
    internal void Sell(Player player, in ClientRequest request)
    {
        long characterId = player.CharacterId;
        string opKey = OpKey(player, request.RequestId);
        string itemsKey = opKey + ":items", goldKey = opKey + ":gold";
        if (_inventory.IsAttached(characterId) && _inventory.TryGetOpResult(characterId, itemsKey, out int soldBefore))
        {
            // Double submit: the first answer again, nothing changes.
            _inventory.TryGetGoldOp(characterId, goldKey, out long goldBefore);
            Confirm(player, goldKey, request.RequestId, soldBefore, goldBefore);
            return;
        }

        var reason = Check(player, request, out var item, out byte rarity, out var npc);
        if (reason != OutpostReason.None)
        {
            Refuse(player, request.RequestId, reason);
            return;
        }
        if (!_prices.TryQuote(item!, rarity, request.Count, out var quote))
        {
            Refuse(player, request.RequestId, OutpostReason.NotSellable);
            return;
        }
        if (quote.Total < request.QuotedTotal)
        {
            Refuse(player, request.RequestId, OutpostReason.QuoteExpired);
            return;
        }
        if (quote.Total <= 0 || !_inventory.GoldFits(characterId, quote.Total))
        {
            Refuse(player, request.RequestId, quote.Total <= 0 ? OutpostReason.NotSellable : OutpostReason.GoldCapReached);
            return;
        }

        string source = $"npc:{npc!.Def.Id}";
        // Checked above: the slot holds the units and the gold fits, so both halves apply (one world thread, no interleaving).
        if (!_inventory.TryRemove(characterId, request.FromSlot, request.Count, itemsKey, InventoryReason.OutpostSale, source))
            throw new InvalidOperationException($"sale {opKey}: the checked units could not be removed");
        var gold = _inventory.TryAddGold(characterId, quote.Total, goldKey, InventoryReason.OutpostSale, source,
            new SaleInfo((ushort)item!.NetId, rarity, request.Count, npc.Def.NetId));
        if (!gold.Applied)
            throw new InvalidOperationException($"sale {opKey}: the checked gold could not be added");
        _prices.RegisterSale(item, request.Count);
        _inventory.RequestClass1Save(characterId);
        Confirm(player, goldKey, request.RequestId, request.Count, quote.Total);
    }

    private OutpostReason Check(Player player, in ClientRequest request, out ItemDef? item, out byte rarity) =>
        Check(player, request, out item, out rarity, out _);

    /// <summary>The gate, then the stack: sell-only, not stolen, and at least <c>count</c> units in the slot.</summary>
    private OutpostReason Check(Player player, in ClientRequest request, out ItemDef? item, out byte rarity, out Npc? npc)
    {
        item = null;
        rarity = 0;
        var reason = _gate.CheckReach(player, request.DropEntityId, NpcInteraction.Sell, out npc);
        if (reason != OutpostReason.None)
            return reason;
        if (!_inventory.IsAttached(player.CharacterId))
            return OutpostReason.InventoryUnavailable;
        var bag = _inventory.Current(player.CharacterId);
        if (request.FromSlot >= bag.SlotCount || bag[request.FromSlot].IsEmpty || request.Count == 0
            || bag[request.FromSlot].Count < request.Count)
            return OutpostReason.NothingToSell;
        var stack = bag[request.FromSlot];
        if (!_inventory.Rules.TryGetItem(stack.ItemNetId, out var def) || def.Category != ItemCategory.SellOnly || stack.Stolen)
            return OutpostReason.NotSellable;
        var at = new Shared.Simulation.Items.InventoryLocation((byte)_inventory.Rules.NormalBag.Code, request.FromSlot);
        if (_inventory.Unreserved(player.CharacterId, at) < request.Count)
            return OutpostReason.ItemReserved; // a craft holds these units (spec 4.1 reservations, WP-M3-2)
        item = def;
        rarity = stack.Rarity;
        return OutpostReason.None;
    }

    private static void Refuse(Player player, uint requestId, OutpostReason reason) =>
        player.Outposts.SaleRefusals.Add(new SellResult { RequestId = requestId, Reason = reason });

    private static void Confirm(Player player, string goldKey, uint requestId, int count, long gold) =>
        player.Outposts.Pending.Add(new PendingClass1Answer(goldKey,
            new SellResult { RequestId = requestId, Reason = OutpostReason.None, Count = (ushort)count, Gold = gold }, null));
}
