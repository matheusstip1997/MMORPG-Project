using Shared.Data.Tables;
using Shared.Simulation.Economy;

namespace GameServer.Economy;

/// <summary>
/// Outpost sell prices (R-4.2-02, spec 4.2 <c>PriceService</c>; WP-M2-4): base (<c>PriceBase</c>) x rarity multiplier
/// (<c>RarityMult</c>) x the dynamic multiplier, which follows the item's decayed sales counter in
/// <see cref="DemandService"/> (user decision 2026-10-27: sales only) and is applied per unit sequentially
/// (<see cref="PriceQuote"/>, shared with the economy simulator). The sale itself commits through <c>InventoryService</c>
/// and registers here (<see cref="RegisterSale"/>). Simulation thread only.
/// </summary>
public sealed class PriceService
{
    private readonly EconomyRules _rules;
    private readonly DemandService _demand;

    public PriceService(EconomyRules rules, DemandService demand)
    {
        _rules = rules;
        _demand = demand;
    }

    /// <summary>What a unit of <paramref name="item"/> at <paramref name="rarity"/> costs now; false when outposts do not buy it.</summary>
    public bool TryGetInputs(ItemDef item, byte rarity, out PriceInputs inputs)
    {
        inputs = default;
        if (item.Category != ItemCategory.SellOnly || !_rules.TryGetBasePrice(item.Id, out int baseGold)
            || !_rules.TryGetRarityPercent(rarity, out int rarityPercent))
            return false;
        var prices = _rules.Prices;
        inputs = new PriceInputs(baseGold, rarityPercent, _rules.PriceCurve, prices.FloorPercent, prices.MaxPercent,
            _demand.SalesMilli(item.NetId), _demand.ExpectedSalesMilli());
        return true;
    }

    /// <summary>The quote for <paramref name="count"/> units (the per-unit sequence goes into <paramref name="units"/>).</summary>
    public bool TryQuote(ItemDef item, byte rarity, int count, out PriceQuoteResult quote, Span<int> units = default)
    {
        quote = default;
        if (!TryGetInputs(item, rarity, out var inputs))
            return false;
        quote = PriceQuote.Quote(inputs, count, units);
        return true;
    }

    /// <summary>A sale was applied: the next units of this item price lower (R-4.2-02).</summary>
    public void RegisterSale(ItemDef item, int count) => _demand.RegisterSale(item, count);
}
