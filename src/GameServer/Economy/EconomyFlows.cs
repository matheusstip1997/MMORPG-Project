using GameServer.Events;
using GameServer.Persistence;

namespace GameServer.Economy;

/// <summary>Which gold changes are faucets and which are sinks (R-4.2-01, R-4.2-04; spec 7.2 economy metrics).</summary>
public static class EconomyFlows
{
    /// <summary>
    /// The flow of a gold change: outpost sales create gold (the only faucet in M2), vendor purchases destroy it; any other
    /// reason (admin tools) by its sign.
    /// </summary>
    public static GoldFlow Of(InventoryReason reason, long delta) => reason switch
    {
        InventoryReason.OutpostSale => GoldFlow.Faucet,
        InventoryReason.VendorPurchase => GoldFlow.Sink,
        _ => delta > 0 ? GoldFlow.Faucet : GoldFlow.Sink,
    };
}
