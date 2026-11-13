using GameServer.Events;
using GameServer.Persistence;

namespace GameServer.Economy;

/// <summary>Which gold changes are faucets and which are sinks (R-4.2-01, R-4.2-04; spec 7.2 economy metrics).</summary>
public static class EconomyFlows
{
    /// <summary>
    /// The flow of a gold change: outpost sales create gold (the only faucet in M2), vendor purchases and fees (WP-M4-1)
    /// destroy it, trades and escrow move it (WP-M4-4); any other reason (admin tools) by its sign.
    /// </summary>
    public static GoldFlow Of(InventoryReason reason, long delta) => reason switch
    {
        InventoryReason.OutpostSale => GoldFlow.Faucet,
        InventoryReason.VendorPurchase => GoldFlow.Sink,
        InventoryReason.Fee => GoldFlow.Sink,
        // WP-M4-4: transfers between owners move gold; they never create or destroy it (R-4.5-02).
        InventoryReason.Trade or InventoryReason.EscrowHold or InventoryReason.EscrowRelease => GoldFlow.Move,
        // WP-M4-3: a mail claim hands over gold that left another owner (a sale's proceeds); an admin compensation claims
        // under Admin instead, a faucet by its sign.
        InventoryReason.MailClaim => GoldFlow.Move,
        // WP-M4-9: a death drop and a take from a loot bag move items only; listed so a gold change under them would be a move.
        InventoryReason.DeathDrop or InventoryReason.LootBagTake => GoldFlow.Move,
        _ => delta > 0 ? GoldFlow.Faucet : GoldFlow.Sink,
    };
}
