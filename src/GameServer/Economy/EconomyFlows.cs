using GameServer.Events;
using GameServer.Persistence;

namespace GameServer.Economy;

/// <summary>Which gold changes are faucets and which are sinks (R-4.2-01, R-4.2-04; spec 7.2 economy metrics).</summary>
public static class EconomyFlows
{
    /// <summary>
    /// The flow of a gold change: outpost sales create gold (the only faucet in M2), vendor purchases destroy it, trades and
    /// escrow move it (WP-M4-4); any other
    /// reason (admin tools) by its sign.
    /// </summary>
    public static GoldFlow Of(InventoryReason reason, long delta) => reason switch
    {
        InventoryReason.OutpostSale => GoldFlow.Faucet,
        InventoryReason.VendorPurchase => GoldFlow.Sink,
        // WP-M4-4: transfers between owners move gold; they never create or destroy it (R-4.5-02).
        InventoryReason.Trade or InventoryReason.EscrowHold or InventoryReason.EscrowRelease => GoldFlow.Move,
        _ => delta > 0 ? GoldFlow.Faucet : GoldFlow.Sink,
    };
}
