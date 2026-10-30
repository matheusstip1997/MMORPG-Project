using System.Text.Json;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Economy;
using Shared.Simulation.Items;
using Shared.World;

namespace EconomySimulator;

/// <summary>
/// The spawn points of one monster type in one region (the key of a demand kill counter, R-4.2-03): how many monsters live
/// there, their base respawn delay and their loot table.
/// </summary>
public sealed record SimPool(int MonsterNetId, string MonsterId, byte RegionIndex, string RegionId, bool Boss, int Points,
    int RespawnMs, IReadOnlyList<LootChoice> Loot, int Rolls, int NothingWeight)
{
    /// <summary>The demand counter key, as <c>DemandService</c> forms it.</summary>
    public int Key => DemandModel.Key(MonsterNetId, RegionIndex);

    public string Name => MonsterId + "@" + RegionId;
}

/// <summary>A sell-only item outposts buy (<c>PriceBase</c>).</summary>
public sealed record SimSellItem(int NetId, string Id, int BaseGold);

/// <summary>
/// A vendor good and its fixed price (<c>NpcVendorStock</c>; the cheapest row when several vendors stock it), and how many a
/// player can use in an hour at most (spec 3.4 consumable use, WP-M3-3: <c>ConsumableRules.MaxUsesPerHour</c>, the meters' decay
/// and the cooldown; 0 for an item without a <c>ConsumableDef</c> row).
/// </summary>
public sealed record SimVendorGood(string Id, int PriceGold, int MaxUsesPerHour = 0);

/// <summary>
/// The M2 economy tables in the simulator's form (spec 4.2, WP-M2-4 tables): spawn pools from <c>SpawnPoints</c> and the
/// world's markers and regions, loot from <c>LootTableDef</c>/<c>LootTableEntry</c>, prices from <c>PriceBase</c> and
/// <c>RarityMult</c>, the demand curves and settings, the vendor stock. Built from tables that passed the shared validator.
/// </summary>
public sealed class SimEconomy
{
    private readonly Dictionary<int, SimSellItem> _sellItems;
    private readonly Dictionary<int, int> _rarityPercentByTier;
    private readonly Dictionary<string, SimVendorGood> _vendorGoods;

    private SimEconomy(ConfigSet tables, IReadOnlyList<SimPool> pools, Dictionary<int, SimSellItem> sellItems,
        Dictionary<int, int> rarityPercentByTier, Dictionary<string, SimVendorGood> vendorGoods)
    {
        Tables = tables;
        Pools = pools;
        _sellItems = sellItems;
        _rarityPercentByTier = rarityPercentByTier;
        _vendorGoods = vendorGoods;
        Demand = tables.Single<DemandSettings>();
        Prices = tables.Single<PriceFloorRecovery>();
        SpawnCurve = Curve(tables, DemandCurve.SpawnDelay);
        RewardCurve = Curve(tables, DemandCurve.Reward);
        PriceCurve = Curve(tables, DemandCurve.Price);
    }

    public ConfigSet Tables { get; }

    /// <summary>Every spawn pool, in demand key order (monster net id, then region index; rule 6).</summary>
    public IReadOnlyList<SimPool> Pools { get; }

    public IEnumerable<SimSellItem> SellItems => _sellItems.Values.OrderBy(i => i.NetId);

    public DemandSettings Demand { get; }
    public PriceFloorRecovery Prices { get; }
    public DemandCurveShape SpawnCurve { get; }
    public DemandCurveShape RewardCurve { get; }
    public DemandCurveShape PriceCurve { get; }

    public IEnumerable<EconomySimScenario> Scenarios => Rows<EconomySimScenario>(Tables);

    public bool TryGetSellItem(int netId, out SimSellItem item) => _sellItems.TryGetValue(netId, out item!);

    public bool TryGetRarityPercent(int tier, out int percent) => _rarityPercentByTier.TryGetValue(tier, out percent);

    public SimVendorGood VendorGood(string itemId) => _vendorGoods[itemId];

    /// <summary>
    /// Loads and validates <paramref name="tablesDirectory"/> with the portable reader and the world's grid and markers
    /// (the region of each spawn point). Throws <see cref="InvalidDataException"/> with the first error.
    /// </summary>
    public static SimEconomy Load(string tablesDirectory, string worldDirectory)
    {
        var errors = new List<ValidationError>();
        var tables = ConfigTableReader.LoadAndValidate(ConfigTables.All, name =>
        {
            var path = Path.Combine(tablesDirectory, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, errors);
        if (errors.Count > 0)
            throw new InvalidDataException($"Config tables in {tablesDirectory} are invalid (run DataValidator): {errors[0]}");
        return From(tables, worldDirectory);
    }

    /// <summary>The simulator's economy from validated <paramref name="tables"/> and the world in <paramref name="worldDirectory"/>.</summary>
    public static SimEconomy From(ConfigSet tables, string worldDirectory)
    {
        if (!WorldPackage.TryLoadGrid(name =>
            {
                var path = Path.Combine(worldDirectory, name);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }, out var grid, out var error))
            throw new InvalidDataException($"World {worldDirectory}: {error}");
        var markers = new Dictionary<string, FixVec2>(StringComparer.Ordinal);
        using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(worldDirectory, WorldFiles.Markers))))
        {
            foreach (var marker in document.RootElement.GetProperty("markers").EnumerateArray())
                markers[marker.GetProperty("id").GetString()!] =
                    FixVec2.FromCm(marker.GetProperty("xCm").GetInt32(), marker.GetProperty("yCm").GetInt32());
        }
        var regionIds = new Dictionary<int, string>();
        using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(worldDirectory, WorldFiles.Regions))))
        {
            foreach (var region in document.RootElement.GetProperty("regions").EnumerateArray())
                regionIds[region.GetProperty("index").GetInt32()] = region.GetProperty("id").GetString()!;
        }
        return From(tables, point => markers.TryGetValue(point, out var at) ? grid!.RegionIndexAt(at) : (byte?)null,
            index => regionIds.TryGetValue(index, out var id) ? id : index.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The simulator's economy from validated <paramref name="tables"/>; <paramref name="regionOfMarker"/> gives a spawn
    /// marker's region index (null: no such marker, the point is skipped) and <paramref name="regionId"/> a region's id.
    /// </summary>
    public static SimEconomy From(ConfigSet tables, Func<string, byte?> regionOfMarker, Func<byte, string> regionId)
    {
        var start = tables.Single<StartingValuesM1>();
        var items = Rows<ItemDef>(tables).ToDictionary(i => i.Id, StringComparer.Ordinal);
        var rarityTier = Rows<RarityDef>(tables).ToDictionary(r => r.Id, r => r.Tier, StringComparer.Ordinal);
        var rarityPercent = new Dictionary<int, int>();
        foreach (var mult in Rows<RarityMult>(tables))
        {
            if (rarityTier.TryGetValue(mult.RarityId, out var tier))
                rarityPercent[tier] = mult.PricePercent;
        }
        var sellItems = new Dictionary<int, SimSellItem>();
        foreach (var price in Rows<PriceBase>(tables))
            sellItems[items[price.ItemId].NetId] = new SimSellItem(items[price.ItemId].NetId, price.ItemId, price.BaseGold);

        var lootByMonster = new Dictionary<string, (List<LootChoice> Choices, int Rolls, int Nothing)>(StringComparer.Ordinal);
        var entries = Rows<LootTableEntry>(tables).ToList();
        foreach (var table in Rows<LootTableDef>(tables))
        {
            var choices = entries.Where(e => e.TableId == table.Id)
                .Select(e => new LootChoice((ushort)items[e.ItemId].NetId, (byte)rarityTier[e.RarityId], e.Weight, e.CountMin, e.CountMax))
                .ToList();
            lootByMonster[table.MonsterId] = (choices, table.Rolls, table.NothingWeight);
        }

        var monsters = Rows<MonsterDef>(tables).ToDictionary(m => m.Id, StringComparer.Ordinal);
        var counts = new SortedDictionary<int, (MonsterDef Monster, byte Region, int Points)>();
        foreach (var point in Rows<SpawnPoints>(tables))
        {
            if (regionOfMarker(point.MarkerId) is not { } region || !monsters.TryGetValue(point.MonsterId, out var monster))
                continue;
            int key = DemandModel.Key(monster.NetId, region);
            counts[key] = counts.TryGetValue(key, out var seen) ? (monster, region, seen.Points + 1) : (monster, region, 1);
        }
        var pools = new List<SimPool>();
        foreach (var (_, (monster, region, points)) in counts)
        {
            var loot = lootByMonster.TryGetValue(monster.Id, out var table) ? table : (new List<LootChoice>(), 1, 1);
            pools.Add(new SimPool(monster.NetId, monster.Id, region, regionId(region), monster.Boss, points,
                monster.Boss ? start.BossRespawnMs : start.MonsterRespawnMs, loot.Item1, loot.Item2,
                loot.Item1.Count == 0 ? Math.Max(1, loot.Item3) : loot.Item3));
        }

        var meters = Rows<ConsumptionMeterSettings>(tables).FirstOrDefault();
        var consumables = Rows<ConsumableDef>(tables).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var goods = new Dictionary<string, SimVendorGood>(StringComparer.Ordinal);
        foreach (var stock in Rows<NpcVendorStock>(tables))
        {
            if (!goods.TryGetValue(stock.ItemId, out var good) || stock.PriceGold < good.PriceGold)
                goods[stock.ItemId] = new SimVendorGood(stock.ItemId, stock.PriceGold,
                    meters != null && consumables.TryGetValue(stock.ItemId, out var use) ? ConsumableRules.MaxUsesPerHour(use, meters) : 0);
        }
        return new SimEconomy(tables, pools, sellItems, rarityPercent, goods);
    }

    private static DemandCurveShape Curve(ConfigSet tables, string id)
    {
        var row = DemandCurve.Find(tables, id) ?? throw new InvalidDataException($"DemandCurve has no '{id}' row (R-4.2-03).");
        return new DemandCurveShape(row.RatioPermille, row.Percent);
    }

    internal static IEnumerable<T> Rows<T>(ConfigSet tables) =>
        tables.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>() : Enumerable.Empty<T>();
}
