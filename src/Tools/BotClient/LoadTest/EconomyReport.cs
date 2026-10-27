using System.Text.Json;

namespace BotClient.LoadTest;

/// <summary>Gold created and destroyed in one UTC hour, by reason (R-4.2-07).</summary>
public sealed record EconomyHour(long HourStartMs, long GoldCreated, long GoldDestroyed,
    IReadOnlyDictionary<string, long> CreatedByReason, IReadOnlyDictionary<string, long> DestroyedByReason);

/// <summary>
/// Faucet and sink totals per hour from a server's telemetry (spec 7.2 economy metrics, R-4.2-07; WP-M2-4): every
/// <c>GoldChanged</c> record (positive: a faucet, negative: a sink), bucketed by UTC hour and by reason, plus the items
/// sold (<c>ItemSold</c>) and the newest dynamic sell multiplier of each item (<c>PriceChanged</c>). Event records are
/// exact (one per committed gold change), so the totals do not depend on the snapshot interval.
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

    public const long HourMs = 3_600_000;

    /// <summary>
    /// Names of <c>InventoryReason</c> (GameServer/Events/ItemEvents.cs; this tool has no server reference). Append when it
    /// grows; an unknown value prints as its number.
    /// </summary>
    private static readonly string[] ReasonNames = ["none", "lootPickup", "playerMove", "admin", "starterGear", "outpostSale", "vendorPurchase"];

    public static string ReasonName(long reason) =>
        reason >= 0 && reason < ReasonNames.Length ? ReasonNames[reason] : reason.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Reads every JSON lines file under <paramref name="directory"/> (like <see cref="TickReport.Read"/>).</summary>
    public static EconomyReport Read(string directory, long fromMs = long.MinValue, long toMs = long.MaxValue)
    {
        var hours = new SortedDictionary<long, HourTotals>();
        long created = 0, destroyed = 0, sold = 0;
        var prices = new SortedDictionary<long, long>();
        var priceTimes = new Dictionary<long, long>();

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
                            long hourStart = time - Mod(time, HourMs);
                            if (!hours.TryGetValue(hourStart, out var hour))
                                hours.Add(hourStart, hour = new HourTotals());
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
                            sold += Long(root, "count");
                            break;
                        case PriceChangedKind:
                        {
                            long item = Long(root, "item");
                            if (!priceTimes.TryGetValue(item, out long seen) || time >= seen)
                            {
                                priceTimes[item] = time;
                                prices[item] = Long(root, "pricePercent");
                            }
                            break;
                        }
                    }
                }
            }
        }

        var result = hours.Select(h => new EconomyHour(h.Key, h.Value.Created, h.Value.Destroyed,
            new SortedDictionary<string, long>(h.Value.CreatedByReason, StringComparer.Ordinal),
            new SortedDictionary<string, long>(h.Value.DestroyedByReason, StringComparer.Ordinal))).ToList();
        return new EconomyReport(result, created, destroyed, sold, prices);
    }

    private static long Mod(long value, long divisor) => ((value % divisor) + divisor) % divisor;

    private static long Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;

    private sealed class HourTotals
    {
        public long Created { get; set; }
        public long Destroyed { get; set; }
        public Dictionary<string, long> CreatedByReason { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> DestroyedByReason { get; } = new(StringComparer.Ordinal);
    }
}
