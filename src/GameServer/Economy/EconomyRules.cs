using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Economy;

namespace GameServer.Economy;

/// <summary>
/// The economy tables in runtime form (spec 4.2, WP-M2-4): NPC definitions, vendor stock, base prices and rarity
/// multipliers, the demand curves and the safety nets. Built once from validated tables.
/// </summary>
public sealed class EconomyRules
{
    private readonly Dictionary<string, NpcDef> _npcs;
    private readonly Dictionary<(string NpcId, string ItemId), NpcVendorStock> _stock;
    private readonly Dictionary<string, int> _basePrice;
    private readonly Dictionary<int, int> _rarityPercentByTier;
    private readonly Dictionary<int, MonsterDef> _monstersByNetId;

    public EconomyRules(IReadOnlyList<NpcDef> npcs, IReadOnlyList<NpcVendorStock> stock, IReadOnlyList<PriceBase> prices,
        IReadOnlyDictionary<int, int> rarityPercentByTier, IReadOnlyList<MonsterDef> monsters, DemandCurve spawnCurve,
        DemandCurve rewardCurve, DemandCurve priceCurve, PriceFloorRecovery priceSettings, DemandSettings demand,
        EconomySettings settings)
    {
        _npcs = npcs.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _stock = stock.ToDictionary(s => (s.NpcId, s.ItemId));
        _basePrice = prices.ToDictionary(p => p.ItemId, p => p.BaseGold, StringComparer.Ordinal);
        _rarityPercentByTier = new Dictionary<int, int>(rarityPercentByTier);
        _monstersByNetId = monsters.ToDictionary(m => m.NetId);
        Npcs = npcs;
        Stock = stock;
        SpawnCurve = new DemandCurveShape(spawnCurve.RatioPermille, spawnCurve.Percent);
        RewardCurve = new DemandCurveShape(rewardCurve.RatioPermille, rewardCurve.Percent);
        PriceCurve = new DemandCurveShape(priceCurve.RatioPermille, priceCurve.Percent);
        Prices = priceSettings;
        Demand = demand;
        Settings = settings;
    }

    public IReadOnlyList<NpcDef> Npcs { get; }

    /// <summary>Every vendor's stock in table order (the vendor window lists it in this order).</summary>
    public IReadOnlyList<NpcVendorStock> Stock { get; }

    public DemandCurveShape SpawnCurve { get; }
    public DemandCurveShape RewardCurve { get; }
    public DemandCurveShape PriceCurve { get; }
    public PriceFloorRecovery Prices { get; }
    public DemandSettings Demand { get; }
    public EconomySettings Settings { get; }

    public bool TryGetNpc(string id, out NpcDef npc) => _npcs.TryGetValue(id, out npc!);

    public bool TryGetStock(string npcId, string itemId, out NpcVendorStock row) => _stock.TryGetValue((npcId, itemId), out row!);

    /// <summary>The base price of a sell-only item; false for an item outposts do not buy.</summary>
    public bool TryGetBasePrice(string itemId, out int baseGold) => _basePrice.TryGetValue(itemId, out baseGold);

    /// <summary>The rarity multiplier of a rarity tier; false for a tier with no <see cref="RarityMult"/> row.</summary>
    public bool TryGetRarityPercent(int tier, out int percent) => _rarityPercentByTier.TryGetValue(tier, out percent);

    /// <summary>A monster type by <c>MonsterDef.netId</c> (what <c>Killed.VictimDefId</c> carries).</summary>
    public bool TryGetMonster(int netId, out MonsterDef monster) => _monstersByNetId.TryGetValue(netId, out monster!);

    /// <summary>A monster type's net id by its stable id (restoring saved demand state).</summary>
    public bool TryGetMonsterNetId(string id, out int netId)
    {
        foreach (var monster in _monstersByNetId.Values)
        {
            if (monster.Id == id)
            {
                netId = monster.NetId;
                return true;
            }
        }
        netId = 0;
        return false;
    }

    /// <summary>From tables that passed validation; null when the economy tables are not loaded (worlds built without them).</summary>
    public static EconomyRules? From(ConfigSet set)
    {
        if (!set.TryGet(typeof(NpcDef), out _) || !set.TryGet(typeof(DemandSettings), out _))
            return null;
        var rarityTiers = Rows<RarityDef>(set).ToDictionary(r => r.Id, r => r.Tier, StringComparer.Ordinal);
        var rarityPercent = new Dictionary<int, int>();
        foreach (var mult in Rows<RarityMult>(set))
        {
            if (rarityTiers.TryGetValue(mult.RarityId, out var tier))
                rarityPercent[tier] = mult.PricePercent;
        }
        return new EconomyRules(Rows<NpcDef>(set), Rows<NpcVendorStock>(set), Rows<PriceBase>(set), rarityPercent,
            Rows<MonsterDef>(set), Curve(set, DemandCurve.SpawnDelay), Curve(set, DemandCurve.Reward),
            Curve(set, DemandCurve.Price), set.Single<PriceFloorRecovery>(), set.Single<DemandSettings>(),
            set.Single<EconomySettings>());
    }

    private static DemandCurve Curve(ConfigSet set, string id) =>
        DemandCurve.Find(set, id) ?? throw new InvalidOperationException($"DemandCurve has no '{id}' row (R-4.2-03).");

    private static List<T> Rows<T>(ConfigSet set) where T : class
    {
        var rows = new List<T>();
        if (set.TryGet(typeof(T), out var table))
        {
            foreach (var row in table.Rows)
                rows.Add((T)row);
        }
        return rows;
    }
}
