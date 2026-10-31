using System.Text.Json;
using Shared.Data;
using Shared.Data.Tables;
using Shared.World;

namespace BotClient.LoadTest;

/// <summary>Gold created and destroyed in one UTC hour, by reason (R-4.2-07), and the exp granted in it.</summary>
public sealed record EconomyHour(long HourStartMs, long GoldCreated, long GoldDestroyed,
    IReadOnlyDictionary<string, long> CreatedByReason, IReadOnlyDictionary<string, long> DestroyedByReason, long ExpGranted = 0);

/// <summary>Units of an item sold at outposts and the gold they created (<c>ItemSold</c>).</summary>
public sealed record ItemSales(long Units, long Gold);

/// <summary>A multiplier over the report's time: its first, lowest, highest and newest value.</summary>
public sealed record Trend(long First, long Min, long Max, long Newest)
{
    public Trend With(long value) => new(First, Math.Min(Min, value), Math.Max(Max, value), value);

    public static Trend Of(long value) => new(value, value, value, value);
}

/// <summary>The demand loop's outputs for one monster type in one region (<c>DemandUpdated</c>, R-4.2-03).</summary>
public sealed record DemandTrend(Trend SpawnDelayPercent, Trend RewardPercent, Trend RatioPermille);

/// <summary>Harvests of one node type and the units they yielded (<c>Harvested</c>, WP-M3-5).</summary>
public sealed record HarvestTotals(long Harvests, long Units);

/// <summary>Exp from one source: a monster type's kills (its net id) or, for net id 0, other grants (outpost run claims).</summary>
public sealed record ExpSource(long Exp, long Grants);

/// <summary>
/// The economy dashboard report from a server's telemetry (spec 7.2 economy metrics, R-4.2-07; WP-M2-4, WP-M2-12): faucet and
/// sink totals per UTC hour and by reason from every <c>GoldChanged</c> record (positive: a faucet, negative: a sink); gold
/// created per sold item (<c>ItemSold</c>); gold destroyed by reason (<c>GoldDestroyed</c>) and the vendor goods bought
/// (<c>ItemCreated</c> with reason vendorPurchase); every item's dynamic sell multiplier over time (<c>PriceChanged</c>); the
/// demand loop's spawn delay and reward multipliers per monster type and region (<c>DemandUpdated</c>); exp granted per
/// source (<c>ExpGained</c>: kills by monster type, run claims as source 0) and per hour; the gold held online at the last
/// snapshot (<c>EconomyTotals</c>). Event records are exact (one per committed change), so the totals do not depend on the
/// snapshot interval.
/// </summary>
public sealed record EconomyReport(
    IReadOnlyList<EconomyHour> Hours,
    long GoldCreated,
    long GoldDestroyed,
    long UnitsSold,
    IReadOnlyDictionary<long, long> PricePercentByItem)
{
    /// <summary>Kind names of telemetry schema v1 (<c>TelemetrySchema</c>, GameServer) this report reads.</summary>
    public const string GoldChangedKind = "GoldChanged";
    public const string ItemSoldKind = "ItemSold";
    public const string PriceChangedKind = "PriceChanged";
    public const string GoldDestroyedKind = "GoldDestroyed";
    public const string ItemCreatedKind = "ItemCreated";
    public const string ItemDestroyedKind = "ItemDestroyed";
    public const string DemandUpdatedKind = "DemandUpdated";
    public const string ExpGainedKind = "ExpGained";
    public const string EconomyTotalsKind = "EconomyTotals";

    public const long HourMs = 3_600_000;

    /// <summary>The <c>InventoryReason</c> of a vendor purchase (see <see cref="ReasonNames"/>).</summary>
    public const long VendorPurchaseReason = 6;

    /// <summary>Job XP granted (WP-M3-1; field names fixed by WP-M3-12): subject entity, amount, job, activity.</summary>
    public const string JobXpGainedKind = "JobXpGained";

    /// <summary>A committed harvest (WP-M3-5; field names fixed by WP-M3-12): subject entity, nodeDef, item, count, seed; region r.</summary>
    public const string HarvestedKind = "Harvested";

    /// <summary>
    /// Names of <c>InventoryReason</c> in value order (GameServer/Events/ItemEvents.cs, camelCase; this tool has no server
    /// reference). A WP that appends a reason appends its name here in the same change (a test compares the two); an
    /// unknown value prints as its number.
    /// </summary>
    public static readonly IReadOnlyList<string> InventoryReasonNames = [
        "none", "lootPickup", "playerMove", "admin", "starterGear", "outpostSale", "vendorPurchase", "questDrop", "questDelivery",
        "jobStart", // WP-M3-1: the starter items of a job pick
        "itemUsed", // WP-M3-3: a consumable used
        "harvest", // WP-M3-5: a resource node harvested
    ];

    /// <summary>
    /// The M3 item reasons the report always shows (WP-M3-12, spec 12.5), zero until their WP appends the
    /// <c>InventoryReason</c> member of that camelCase name: jobStart (WP-M3-1, starter molds), harvest (WP-M3-5), refine,
    /// craft, convert, convertFailed (WP-M3-6), enchant (WP-M3-8, stones), gemSocket, gemRemove (WP-M3-9), itemUsed (WP-M3-3).
    /// </summary>
    public static readonly IReadOnlyList<string> M3ItemReasons = [
        "jobStart", "harvest", "refine", "craft", "convert", "convertFailed", "enchant", "gemSocket", "gemRemove", "itemUsed",
    ];

    /// <summary>
    /// The <c>activity</c> values of <c>JobXpGained</c> (WP-M3-12 proposal for WP-M3-1's job activities, decision 8): 0 other,
    /// 1 gather, 2 refine, 3 craft, 4 convert, 5 enchant, 6 socket.
    /// </summary>
    public static readonly IReadOnlyList<string> JobActivityNames = ["other", "gather", "refine", "craft", "convert", "enchant", "socket"];

    /// <summary>Units and gold per sold item net id.</summary>
    public IReadOnlyDictionary<long, ItemSales> SalesByItem { get; init; } = new SortedDictionary<long, ItemSales>();

    /// <summary>Gold destroyed by reason name (<c>GoldDestroyed</c>).</summary>
    public IReadOnlyDictionary<string, long> GoldDestroyedByReason { get; init; } = new SortedDictionary<string, long>(StringComparer.Ordinal);

    /// <summary>Units bought from vendors per item net id.</summary>
    public IReadOnlyDictionary<long, long> VendorUnitsByItem { get; init; } = new SortedDictionary<long, long>();

    /// <summary>The dynamic sell multiplier of each item over time.</summary>
    public IReadOnlyDictionary<long, Trend> PriceByItem { get; init; } = new SortedDictionary<long, Trend>();

    /// <summary>The demand loop's multipliers per (monster net id, region index).</summary>
    public IReadOnlyDictionary<(long MonsterDef, long Region), DemandTrend> DemandByTypeAndRegion { get; init; } =
        new SortedDictionary<(long, long), DemandTrend>();

    /// <summary>Exp per source: monster net id, 0 for grants that are not kills (run claims).</summary>
    public IReadOnlyDictionary<long, ExpSource> ExpBySource { get; init; } = new SortedDictionary<long, ExpSource>();

    public long ExpGranted { get; init; }

    /// <summary>Gold held by the online characters at the newest <c>EconomyTotals</c> snapshot; null without one.</summary>
    public long? GoldOnline { get; init; }

    /// <summary>Units of items created per <c>InventoryReason</c> name (<c>ItemCreated</c>).</summary>
    public IReadOnlyDictionary<string, long> ItemsCreatedByReason { get; init; } = new SortedDictionary<string, long>(StringComparer.Ordinal);

    /// <summary>Units of items destroyed per <c>InventoryReason</c> name (<c>ItemDestroyed</c>).</summary>
    public IReadOnlyDictionary<string, long> ItemsDestroyedByReason { get; init; } = new SortedDictionary<string, long>(StringComparer.Ordinal);

    /// <summary>Job XP and grants per activity name (<c>JobXpGained</c>, <see cref="JobActivityNames"/>).</summary>
    public IReadOnlyDictionary<string, ExpSource> JobXpByActivity { get; init; } = new SortedDictionary<string, ExpSource>(StringComparer.Ordinal);

    /// <summary>Job XP per job net id (<c>JobDef.netId</c>).</summary>
    public IReadOnlyDictionary<long, long> JobXpByJob { get; init; } = new SortedDictionary<long, long>();

    /// <summary>Harvests and the units they yielded per node net id (<c>Harvested</c>; <c>NodeDef.netId</c>).</summary>
    public IReadOnlyDictionary<long, HarvestTotals> HarvestsByNode { get; init; } = new SortedDictionary<long, HarvestTotals>();

    public long JobXpGranted => JobXpByActivity.Values.Sum(a => a.Exp);

    public static string ReasonName(long reason) =>
        reason >= 0 && reason < InventoryReasonNames.Count ? InventoryReasonNames[(int)reason] : reason.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string ActivityName(long activity) =>
        activity >= 0 && activity < JobActivityNames.Count ? JobActivityNames[(int)activity] : "activity " + activity.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Reads every JSON lines file under <paramref name="directory"/> (like <see cref="TickReport.Read"/>).</summary>
    public static EconomyReport Read(string directory, long fromMs = long.MinValue, long toMs = long.MaxValue)
    {
        var hours = new SortedDictionary<long, HourTotals>();
        long created = 0, destroyed = 0, sold = 0, exp = 0;
        var prices = new SortedDictionary<long, long>();
        var priceTimes = new Dictionary<long, long>();
        var priceTrends = new SortedDictionary<long, Trend>();
        var sales = new SortedDictionary<long, ItemSales>();
        var destroyedByReason = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var vendorUnits = new SortedDictionary<long, long>();
        var demand = new SortedDictionary<(long, long), DemandTrend>();
        var expBySource = new SortedDictionary<long, ExpSource>();
        var itemsCreated = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var itemsDestroyed = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var jobXp = new SortedDictionary<string, ExpSource>(StringComparer.Ordinal);
        var jobXpByJob = new SortedDictionary<long, long>();
        var harvests = new SortedDictionary<long, HarvestTotals>();
        long? goldOnline = null;
        long goldOnlineTime = long.MinValue;

        var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.jsonl", SearchOption.AllDirectories) : [];
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] != '{')
                    continue;
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue; // a torn last line while the writer appends
                }
                using (document)
                {
                    var root = document.RootElement;
                    if (!root.TryGetProperty("k", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
                        continue;
                    long time = root.TryGetProperty("t", out var t) ? t.GetInt64() : 0;
                    if (time < fromMs || time > toMs)
                        continue;
                    switch (kindElement.GetString())
                    {
                        case GoldChangedKind:
                        {
                            long delta = Long(root, "delta");
                            string reason = ReasonName(Long(root, "reason"));
                            var hour = Hour(hours, time);
                            if (delta > 0)
                            {
                                created += delta;
                                hour.Created += delta;
                                hour.CreatedByReason[reason] = hour.CreatedByReason.GetValueOrDefault(reason) + delta;
                            }
                            else
                            {
                                destroyed -= delta;
                                hour.Destroyed -= delta;
                                hour.DestroyedByReason[reason] = hour.DestroyedByReason.GetValueOrDefault(reason) - delta;
                            }
                            break;
                        }
                        case ItemSoldKind:
                        {
                            long count = Long(root, "count");
                            long item = Long(root, "item");
                            sold += count;
                            var seen = sales.GetValueOrDefault(item) ?? new ItemSales(0, 0);
                            sales[item] = new ItemSales(seen.Units + count, seen.Gold + Long(root, "gold"));
                            break;
                        }
                        case PriceChangedKind:
                        {
                            long item = Long(root, "item");
                            long percent = Long(root, "pricePercent");
                            if (!priceTimes.TryGetValue(item, out long seen) || time >= seen)
                            {
                                priceTimes[item] = time;
                                prices[item] = percent;
                            }
                            priceTrends[item] = priceTrends.TryGetValue(item, out var trend) ? trend.With(percent) : Trend.Of(percent);
                            break;
                        }
                        case GoldDestroyedKind:
                        {
                            string reason = ReasonName(Long(root, "reason"));
                            destroyedByReason[reason] = destroyedByReason.GetValueOrDefault(reason) + Long(root, "amount");
                            break;
                        }
                        case ItemCreatedKind:
                        {
                            long item = Long(root, "item"), count = Long(root, "count"), reason = Long(root, "reason");
                            string name = ReasonName(reason);
                            itemsCreated[name] = itemsCreated.GetValueOrDefault(name) + count;
                            if (reason == VendorPurchaseReason)
                                vendorUnits[item] = vendorUnits.GetValueOrDefault(item) + count;
                            break;
                        }
                        case ItemDestroyedKind:
                        {
                            string name = ReasonName(Long(root, "reason"));
                            itemsDestroyed[name] = itemsDestroyed.GetValueOrDefault(name) + Long(root, "count");
                            break;
                        }
                        case JobXpGainedKind:
                        {
                            long amount = Long(root, "amount");
                            string activity = ActivityName(Long(root, "activity"));
                            var seen = jobXp.GetValueOrDefault(activity) ?? new ExpSource(0, 0);
                            jobXp[activity] = new ExpSource(seen.Exp + amount, seen.Grants + 1);
                            long job = Long(root, "job");
                            jobXpByJob[job] = jobXpByJob.GetValueOrDefault(job) + amount;
                            break;
                        }
                        case HarvestedKind:
                        {
                            long node = Long(root, "nodeDef");
                            var seen = harvests.GetValueOrDefault(node) ?? new HarvestTotals(0, 0);
                            harvests[node] = new HarvestTotals(seen.Harvests + 1, seen.Units + Long(root, "count"));
                            break;
                        }
                        case DemandUpdatedKind:
                        {
                            var key = (Long(root, "monsterDef"), Long(root, "r"));
                            long spawn = Long(root, "spawnDelayPercent"), reward = Long(root, "rewardPercent"), ratio = Long(root, "ratioPermille");
                            demand[key] = demand.TryGetValue(key, out var trend)
                                ? new DemandTrend(trend.SpawnDelayPercent.With(spawn), trend.RewardPercent.With(reward), trend.RatioPermille.With(ratio))
                                : new DemandTrend(Trend.Of(spawn), Trend.Of(reward), Trend.Of(ratio));
                            break;
                        }
                        case ExpGainedKind:
                        {
                            long amount = Long(root, "amount");
                            long source = Long(root, "victimDef");
                            exp += amount;
                            Hour(hours, time).Exp += amount;
                            var seen = expBySource.GetValueOrDefault(source) ?? new ExpSource(0, 0);
                            expBySource[source] = new ExpSource(seen.Exp + amount, seen.Grants + 1);
                            break;
                        }
                        case EconomyTotalsKind when time >= goldOnlineTime:
                            goldOnlineTime = time;
                            goldOnline = Long(root, "goldOnline");
                            break;
                    }
                }
            }
        }

        var result = hours.Select(h => new EconomyHour(h.Key, h.Value.Created, h.Value.Destroyed,
            new SortedDictionary<string, long>(h.Value.CreatedByReason, StringComparer.Ordinal),
            new SortedDictionary<string, long>(h.Value.DestroyedByReason, StringComparer.Ordinal), h.Value.Exp)).ToList();
        return new EconomyReport(result, created, destroyed, sold, prices)
        {
            SalesByItem = sales,
            GoldDestroyedByReason = destroyedByReason,
            VendorUnitsByItem = vendorUnits,
            PriceByItem = priceTrends,
            DemandByTypeAndRegion = demand,
            ExpBySource = expBySource,
            ExpGranted = exp,
            GoldOnline = goldOnline,
            ItemsCreatedByReason = itemsCreated,
            ItemsDestroyedByReason = itemsDestroyed,
            JobXpByActivity = jobXp,
            JobXpByJob = jobXpByJob,
            HarvestsByNode = harvests,
        };
    }

    /// <summary>
    /// Gold created by sales per monster type: an item's gold goes to the one type whose loot table drops it; items several
    /// types drop (or none) are summed under null.
    /// </summary>
    public IReadOnlyDictionary<long, long> SaleGoldByMonster(EconomyNames names, out long sharedGold)
    {
        var byMonster = new SortedDictionary<long, long>();
        sharedGold = 0;
        foreach (var (item, sale) in SalesByItem)
        {
            if (names.DroppedBy.TryGetValue(item, out var monsters) && monsters.Count == 1)
                byMonster[monsters[0]] = byMonster.GetValueOrDefault(monsters[0]) + sale.Gold;
            else
                sharedGold += sale.Gold;
        }
        return byMonster;
    }

    /// <summary>The report as text lines: per hour, then totals, sales, sinks, prices, demand and exp (BotClient <c>report --kind economy</c>).</summary>
    public IEnumerable<string> Describe(EconomyNames names)
    {
        foreach (var hour in Hours)
        {
            var start = DateTimeOffset.FromUnixTimeMilliseconds(hour.HourStartMs).UtcDateTime;
            yield return Invariant($"{start:yyyy-MM-dd HH}:00Z  created {hour.GoldCreated} ({Join(hour.CreatedByReason)})  ")
                + Invariant($"destroyed {hour.GoldDestroyed} ({Join(hour.DestroyedByReason)})  net {hour.GoldCreated - hour.GoldDestroyed}  exp {hour.ExpGranted}");
        }
        yield return Invariant($"total: gold created {GoldCreated}, destroyed {GoldDestroyed}, net {GoldCreated - GoldDestroyed}, units sold {UnitsSold}")
            + (GoldOnline is { } online ? Invariant($", gold online at the last snapshot {online}") : "");
        yield return "sales (faucet): " + (SalesByItem.Count == 0 ? "-"
            : string.Join(", ", SalesByItem.Select(s => Invariant($"{names.Item(s.Key)} {s.Value.Units} units {s.Value.Gold} gold"))));
        var byMonster = SaleGoldByMonster(names, out long shared);
        yield return "sale gold by monster type: " + (byMonster.Count == 0 && shared == 0 ? "-"
            : string.Join(", ", byMonster.Select(m => Invariant($"{names.Monster(m.Key)} {m.Value}"))) + (shared > 0 ? Invariant($", several types {shared}") : ""));
        yield return "gold destroyed (sinks): " + Join(GoldDestroyedByReason) + "; vendor goods bought: "
            + (VendorUnitsByItem.Count == 0 ? "-" : string.Join(", ", VendorUnitsByItem.Select(v => Invariant($"{names.Item(v.Key)} {v.Value}"))));
        yield return "sell price % by item (first/min/max/newest): " + (PriceByItem.Count == 0 ? "-"
            : string.Join(", ", PriceByItem.Select(p => $"{names.Item(p.Key)} {Describe(p.Value)}")));
        yield return "demand by monster type and region (spawn delay % and reward %, first/min/max/newest):";
        foreach (var ((monster, region), trend) in DemandByTypeAndRegion)
            yield return Invariant($"  {names.Monster(monster)}@{names.Region(region)}: spawn {Describe(trend.SpawnDelayPercent)}, ")
                + Invariant($"reward {Describe(trend.RewardPercent)}, ratio newest {trend.RatioPermille.Newest / 1000.0:0.00}");
        yield return Invariant($"exp granted {ExpGranted}: ") + (ExpBySource.Count == 0 ? "-"
            : string.Join(", ", ExpBySource.Select(e => Invariant($"{(e.Key == 0 ? "run claims" : names.Monster(e.Key))} {e.Value.Exp} ({e.Value.Grants}x)"))));
        yield return "items created by reason (units): " + Join(ItemsCreatedByReason);
        yield return "items destroyed by reason (units): " + Join(ItemsDestroyedByReason);
        foreach (var line in DescribeM3())
            yield return line;

        static string Describe(Trend trend) => Invariant($"{trend.First}/{trend.Min}/{trend.Max}/{trend.Newest}");
    }

    /// <summary>
    /// The M3 section (WP-M3-12, spec 12.5): items created and destroyed for every M3 reason, job XP per activity and the
    /// harvests per node type. Always printed, with zeros until the WPs write the telemetry (BotClient README, "M3 telemetry").
    /// </summary>
    public IEnumerable<string> DescribeM3()
    {
        yield return "M3 items by reason (created/destroyed units): " + string.Join(", ", M3ItemReasons.Select(r =>
            Invariant($"{r} {ItemsCreatedByReason.GetValueOrDefault(r)}/{ItemsDestroyedByReason.GetValueOrDefault(r)}")));
        yield return Invariant($"M3 job xp {JobXpGranted} by activity: ") + string.Join(", ", JobActivityNames.Skip(1)
            .Concat(JobXpByActivity.Keys.Where(k => !JobActivityNames.Skip(1).Contains(k)))
            .Select(a => Invariant($"{a} {JobXpByActivity.GetValueOrDefault(a)?.Exp ?? 0} ({JobXpByActivity.GetValueOrDefault(a)?.Grants ?? 0}x)")))
            + "; by job: " + (JobXpByJob.Count == 0 ? "-" : string.Join(", ", JobXpByJob.Select(j => Invariant($"job {j.Key} {j.Value}"))));
        yield return Invariant($"M3 harvests {HarvestsByNode.Values.Sum(h => h.Harvests)}, units {HarvestsByNode.Values.Sum(h => h.Units)}: ")
            + (HarvestsByNode.Count == 0 ? "-" : string.Join(", ", HarvestsByNode.Select(h => Invariant($"node {h.Key} {h.Value.Harvests}x {h.Value.Units} units"))));
    }

    private static string Join(IReadOnlyDictionary<string, long> byReason) =>
        byReason.Count == 0 ? "-" : string.Join(", ", byReason.Select(r => Invariant($"{r.Key} {r.Value}")));

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    private static HourTotals Hour(SortedDictionary<long, HourTotals> hours, long time)
    {
        long hourStart = time - Mod(time, HourMs);
        if (!hours.TryGetValue(hourStart, out var hour))
            hours.Add(hourStart, hour = new HourTotals());
        return hour;
    }

    private static long Mod(long value, long divisor) => ((value % divisor) + divisor) % divisor;

    private static long Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

    private sealed class HourTotals
    {
        public long Created { get; set; }
        public long Destroyed { get; set; }
        public long Exp { get; set; }
        public Dictionary<string, long> CreatedByReason { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> DestroyedByReason { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>
/// Names for the economy report's net ids and region indexes: items and monsters from the config tables, regions from the
/// exported world's <c>regions.json</c>, and which monster types drop each item (<c>LootTableDef</c>/<c>LootTableEntry</c>),
/// so the gold of a sale can be put to the monster types it came from. Without names the report prints the numbers.
/// </summary>
public sealed class EconomyNames
{
    public static readonly EconomyNames None = new(new Dictionary<long, string>(), new Dictionary<long, string>(),
        new Dictionary<long, string>(), new Dictionary<long, IReadOnlyList<long>>());

    private readonly IReadOnlyDictionary<long, string> _items;
    private readonly IReadOnlyDictionary<long, string> _monsters;
    private readonly IReadOnlyDictionary<long, string> _regions;

    public EconomyNames(IReadOnlyDictionary<long, string> items, IReadOnlyDictionary<long, string> monsters,
        IReadOnlyDictionary<long, string> regions, IReadOnlyDictionary<long, IReadOnlyList<long>> droppedBy)
    {
        _items = items;
        _monsters = monsters;
        _regions = regions;
        DroppedBy = droppedBy;
    }

    /// <summary>The monster net ids whose loot tables hold an item, by item net id.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<long>> DroppedBy { get; }

    public string Item(long netId) => _items.TryGetValue(netId, out var id) ? id : "item " + netId;

    public string Monster(long netId) => _monsters.TryGetValue(netId, out var id) ? id : "monster " + netId;

    public string Region(long index) => _regions.TryGetValue(index, out var id) ? id : "region " + index;

    /// <summary>
    /// From validated <paramref name="tables"/> and the world's <c>regions.json</c> in <paramref name="worldDirectory"/>
    /// (regions are left as numbers when the file is missing).
    /// </summary>
    public static EconomyNames From(ConfigSet tables, string? worldDirectory)
    {
        var items = Rows<ItemDef>(tables).ToDictionary(i => (long)i.NetId, i => i.Id);
        var monsters = Rows<MonsterDef>(tables).ToDictionary(m => (long)m.NetId, m => m.Id);
        var itemNetIds = Rows<ItemDef>(tables).ToDictionary(i => i.Id, i => (long)i.NetId, StringComparer.Ordinal);
        var monsterNetIds = Rows<MonsterDef>(tables).ToDictionary(m => m.Id, m => (long)m.NetId, StringComparer.Ordinal);
        var lootMonster = Rows<LootTableDef>(tables).ToDictionary(t => t.Id, t => monsterNetIds[t.MonsterId], StringComparer.Ordinal);
        var droppedBy = Rows<LootTableEntry>(tables)
            .GroupBy(e => itemNetIds[e.ItemId])
            .ToDictionary(g => g.Key, g => (IReadOnlyList<long>)g.Select(e => lootMonster[e.TableId]).Distinct().Order().ToList());
        var regions = new Dictionary<long, string>();
        var regionsPath = worldDirectory == null ? null : Path.Combine(worldDirectory, WorldFiles.Regions);
        if (regionsPath != null && File.Exists(regionsPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(regionsPath));
            foreach (var region in document.RootElement.GetProperty("regions").EnumerateArray())
                regions[region.GetProperty("index").GetInt64()] = region.GetProperty("id").GetString()!;
        }
        return new EconomyNames(items, monsters, regions, droppedBy);
    }

    private static IEnumerable<T> Rows<T>(ConfigSet tables) =>
        tables.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>() : Enumerable.Empty<T>();
}
