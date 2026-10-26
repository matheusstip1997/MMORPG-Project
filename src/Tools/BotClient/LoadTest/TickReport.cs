using System.Text.Json;

namespace BotClient.LoadTest;

/// <summary>Tick time percentiles and server health counters over a time window of a server's telemetry (spec 7.2, 11.5).</summary>
public sealed record TickReport(
    int Ticks,
    long P50Us,
    long P95Us,
    long P99Us,
    long MaxUs,
    long MaxLateUs,
    long Overruns,
    long MalformedInputs,
    long StaleFrames,
    long SnapshotsTruncated,
    int WorldCrashed,
    int SecurityEvents,
    int QueueDropped,
    IReadOnlyDictionary<string, int> SecurityByEvent)
{
    /// <summary>Kind names of telemetry schema v1 (<c>TelemetrySchema</c>, GameServer) this report reads.</summary>
    public const string TickTimingKind = "TickTiming";
    public const string TickStatsKind = "TickStats";
    public const string WorldCountersKind = "WorldCounters";
    public const string WorldCrashedKind = "WorldCrashed";
    public const string SecurityKind = "Security";
    public const string QueueDroppedKind = "QueueDropped";

    /// <summary>
    /// Reads every JSON lines file under <paramref name="directory"/>. Tick times come from the per-tick
    /// <c>TickTiming</c> records with a time in [<paramref name="fromMs"/>, <paramref name="toMs"/>]; overruns and the
    /// counters are the growth of the running totals inside the window; crashes, security events and queue drops are
    /// counted over the whole files (any is a failure).
    /// </summary>
    public static TickReport Read(string directory, long fromMs = long.MinValue, long toMs = long.MaxValue)
    {
        var ticks = new List<long>();
        long maxLate = 0;
        long? firstOverruns = null, lastOverruns = null;
        long[]? firstCounters = null, lastCounters = null;
        int crashed = 0, security = 0, dropped = 0;
        var securityByEvent = new Dictionary<string, int>(StringComparer.Ordinal);

        var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.jsonl", SearchOption.AllDirectories) : [];
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            // The writer may still append: share the file and skip a torn last line.
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
                    continue;
                }
                using (document)
                {
                    var root = document.RootElement;
                    if (!root.TryGetProperty("k", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
                        continue;
                    string kind = kindElement.GetString()!;
                    long time = root.TryGetProperty("t", out var t) ? t.GetInt64() : 0;
                    bool inWindow = time >= fromMs && time <= toMs;
                    switch (kind)
                    {
                        case TickTimingKind when inWindow:
                            ticks.Add(Long(root, "tickUs"));
                            maxLate = Math.Max(maxLate, Long(root, "lateUs"));
                            break;
                        case TickStatsKind when inWindow:
                            firstOverruns ??= Long(root, "overruns");
                            lastOverruns = Long(root, "overruns");
                            break;
                        case WorldCountersKind when inWindow:
                            var counters = new[]
                            {
                                Long(root, "malformedInputs"), Long(root, "staleFrames"), Long(root, "snapshotsTruncated"),
                            };
                            firstCounters ??= counters;
                            lastCounters = counters;
                            break;
                        case WorldCrashedKind:
                            crashed++;
                            break;
                        case SecurityKind:
                            security++;
                            string name = root.TryGetProperty("event", out var e) ? e.ToString() : "?";
                            securityByEvent[name] = securityByEvent.GetValueOrDefault(name) + 1;
                            break;
                        case QueueDroppedKind:
                            dropped++;
                            break;
                    }
                }
            }
        }

        ticks.Sort();
        long Delta(int i) => firstCounters == null ? 0 : lastCounters![i] - firstCounters[i];
        return new TickReport(ticks.Count, Percentile(ticks, 50), Percentile(ticks, 95), Percentile(ticks, 99),
            ticks.Count > 0 ? ticks[^1] : 0, maxLate, (lastOverruns ?? 0) - (firstOverruns ?? 0), Delta(0), Delta(1), Delta(2),
            crashed, security, dropped, securityByEvent);
    }

    /// <summary>Nearest-rank percentile of sorted values; 0 for none.</summary>
    public static long Percentile(IReadOnlyList<long> sorted, int percent)
    {
        if (sorted.Count == 0)
            return 0;
        int rank = (int)Math.Ceiling(percent / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private static long Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
}
