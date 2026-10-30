using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Net;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Outposts;

/// <summary>
/// Buying from an NPC vendor (R-4.2-01: potions, food, starter molds and basic tools at fixed prices, a gold sink; WP-M2-4,
/// molds and tools WP-M3-2), in phase 1 on the world thread. A class 1 operation (R-7.1-01): the gate (NPC, range), the
/// stock row (<c>NotInStock</c>), room in the container that receives the item's category (<c>BagFull</c>: the Normal bag
/// for consumables, the Safe bag equipment area for molds and tools), the price the player saw (<c>QuoteExpired</c> when
/// the table changed), the gold (<c>NotEnoughGold</c>); then, through <see cref="InventoryService"/>: remove the gold
/// (destroyed, keys <c>{op}:gold</c>), add the items (<c>{op}:items</c>, the lowest rarity tier; an instance category makes
/// one instance with a new id, count 1), and an immediate save; the player hears of it once that save committed.
/// Operation key <c>buy:{runId}:{entityId}:{connection}:{requestId}</c>, repeated requests get the first answer.
/// </summary>
public sealed class VendorHandler : IOutpostInteractionHandler
{
    /// <summary>Vendor goods have no rarity roll: the first tier (<c>common</c>).</summary>
    public const byte VendorRarity = 0;

    private readonly InventoryService _inventory;
    private readonly EconomyRules _rules;
    private readonly OutpostInteractions _gate;
    private readonly string _runId;

    public VendorHandler(InventoryService inventory, EconomyRules rules, OutpostInteractions gate, string runId)
    {
        _inventory = inventory;
        _rules = rules;
        _gate = gate;
        _runId = runId;
    }

    /// <summary>Opening the vendor window (its stock comes from the client's own tables).</summary>
    public OutpostReason? Interact(Player player, Npc npc, uint requestId) =>
        _inventory.IsAttached(player.CharacterId) ? OutpostReason.None : OutpostReason.InventoryUnavailable;

    public string OpKey(Player player, uint requestId) => $"buy:{_runId}:{player.Id}:{player.Connection}:{requestId}";

    /// <summary>Phase 1: one <see cref="VendorBuyRequest"/>.</summary>
    internal void Buy(Player player, in ClientRequest request)
    {
        long characterId = player.CharacterId;
        string opKey = OpKey(player, request.RequestId);
        string itemsKey = opKey + ":items", goldKey = opKey + ":gold";
        if (_inventory.IsAttached(characterId) && _inventory.TryGetGoldOp(characterId, goldKey, out long spentBefore))
        {
            _inventory.TryGetOpResult(characterId, itemsKey, out int addedBefore);
            Confirm(player, goldKey, request, addedBefore, spentBefore);
            return;
        }

        var reason = _gate.CheckReach(player, request.DropEntityId, NpcInteraction.Buy, out var npc);
        if (reason == OutpostReason.None && !_inventory.IsAttached(characterId))
            reason = OutpostReason.InventoryUnavailable;
        ItemDef? item = null;
        NpcVendorStock? stock = null;
        if (reason == OutpostReason.None
            && (!_inventory.Rules.TryGetItem(request.ItemNetId, out item) || !_rules.TryGetStock(npc!.Def.Id, item.Id, out stock)
                || request.Count == 0 || request.Count > item.MaxStack))
            reason = OutpostReason.NotInStock; // not a purchase this vendor offers
        long total = reason == OutpostReason.None ? (long)stock!.PriceGold * request.Count : 0;
        if (reason == OutpostReason.None && _inventory.SpaceFor(characterId, item!, VendorRarity) < request.Count)
            reason = OutpostReason.BagFull;
        else if (reason == OutpostReason.None && total != request.QuotedTotal)
            reason = OutpostReason.QuoteExpired;
        else if (reason == OutpostReason.None && _inventory.Gold(characterId) < total)
            reason = OutpostReason.NotEnoughGold;
        if (reason != OutpostReason.None)
        {
            player.Outposts.PurchaseRefusals.Add(new VendorBuyResult
            {
                RequestId = request.RequestId, Reason = reason, ItemNetId = request.ItemNetId,
            });
            return;
        }

        string source = $"npc:{npc!.Def.Id}";
        // Checked above: the gold is there and the units fit, so both halves apply.
        if (!_inventory.TryRemoveGold(characterId, total, goldKey, InventoryReason.VendorPurchase, source).Applied)
            throw new InvalidOperationException($"purchase {opKey}: the checked gold could not be removed");
        var added = _inventory.TryAdd(characterId, item!, VendorRarity, request.Count, itemsKey, InventoryReason.VendorPurchase, source);
        if (added.Added != request.Count)
            throw new InvalidOperationException($"purchase {opKey}: the checked units did not fit");
        _inventory.RequestClass1Save(characterId);
        Confirm(player, goldKey, request, request.Count, total);
    }

    private static void Confirm(Player player, string goldKey, in ClientRequest request, int count, long gold) =>
        player.Outposts.Pending.Add(new PendingClass1Answer(goldKey, null, new VendorBuyResult
        {
            RequestId = request.RequestId,
            Reason = OutpostReason.None,
            ItemNetId = request.ItemNetId,
            Count = (ushort)count,
            GoldSpent = gold,
        }));
}
