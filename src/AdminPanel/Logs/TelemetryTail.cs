using System.Text.Json;
using GameServer.Telemetry;

namespace AdminPanel.Logs;

/// <summary>What to keep of the telemetry: a character (the envelope's actor), an account, a record kind; null keeps all.</summary>
public readonly record struct TelemetryFilter(long? CharacterId, long? AccountId, string? Kind);

/// <summary>
/// The newest telemetry records (spec 7.2 JSON lines, <c>&lt;run&gt;-&lt;nnnn&gt;.jsonl</c>) for a character, an account or the
/// realm. Reads the newest files backwards, at most <c>maxBytes</c> in all, while the game server may still be writing
/// them; the last, partial line of a file being written is skipped. Never touches the game server.
/// </summary>
public static class TelemetryTail
{
    public const string Pattern = "*.jsonl";

    /// <summary>Matching records, newest first, at most <paramref name="maxRecords"/>.</summary>
    public static IReadOnlyList<JsonElement> Read(string directory, TelemetryFilter filter, int maxRecords, int maxBytes)
    {
        var result = new List<JsonElement>();
        if (!Directory.Exists(directory))
            return result;

        long budget = maxBytes;
        var files = new DirectoryInfo(directory).GetFiles(Pattern)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (budget <= 0 || result.Count >= maxRecords)
                break;
            var lines = LogTail.ReadTailLines(file.FullName, (int)Math.Min(budget, int.MaxValue));
            budget -= Math.Min(file.Length, budget);
            for (int i = lines.Count - 1; i >= 0 && result.Count < maxRecords; i--)
            {
                if (TryMatch(lines[i], filter, out var record))
                    result.Add(record);
            }
        }
        return result;
    }

    private static bool TryMatch(string line, TelemetryFilter filter, out JsonElement record)
    {
        record = default;
        if (line.Length == 0 || line[0] != '{')
            return false;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return false; // a line still being written
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(TelemetrySchema.KindKey, out var kind))
                return false; // the file header
            if (filter.CharacterId is { } characterId && !HasNumber(root, TelemetrySchema.CharacterKey, characterId))
                return false;
            if (filter.AccountId is { } accountId && !HasNumber(root, TelemetrySchema.AccountKey, accountId))
                return false;
            if (!string.IsNullOrEmpty(filter.Kind) && kind.ToString() != filter.Kind)
                return false;
            record = root.Clone();
            return true;
        }
    }

    private static bool HasNumber(JsonElement root, string key, long value) =>
        root.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var v) && v == value;
}
