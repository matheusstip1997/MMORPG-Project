using System.Text.Json;

namespace GameServer.Telemetry;

/// <summary>The first line of every telemetry file.</summary>
/// <param name="RunId">The server run; with world ID and event ID it makes a bus event globally unique.</param>
/// <param name="FileIndex">Position of this file in the run (files rotate by size).</param>
public sealed record TelemetryFileHeader(string Schema, int Version, string RunId, long StartedUtcMs, int FileIndex);

/// <summary>
/// JSON lines encoding of schema v1: a header line, then one compact object per record, for example
/// <c>{"k":"Damaged","t":1800000000000,"e":12,"w":1,"c":42,"source":7,"target":9,"action":3,"amount":120,"hpAfter":880}</c>.
/// Envelope fields that are 0 are omitted; payload fields are written under the names in <see cref="TelemetrySchema"/>.
/// </summary>
public static class TelemetryJsonFormat
{
    private static readonly JsonEncodedText SchemaKey = JsonEncodedText.Encode("schema");
    private static readonly JsonEncodedText VersionKey = JsonEncodedText.Encode("v");
    private static readonly JsonEncodedText RunKey = JsonEncodedText.Encode("run");
    private static readonly JsonEncodedText StartedKey = JsonEncodedText.Encode("startedMs");
    private static readonly JsonEncodedText FileKey = JsonEncodedText.Encode("file");
    private static readonly JsonEncodedText Kind = JsonEncodedText.Encode(TelemetrySchema.KindKey);
    private static readonly JsonEncodedText Time = JsonEncodedText.Encode(TelemetrySchema.TimeKey);
    private static readonly JsonEncodedText EventId = JsonEncodedText.Encode(TelemetrySchema.EventIdKey);
    private static readonly JsonEncodedText World = JsonEncodedText.Encode(TelemetrySchema.WorldKey);
    private static readonly JsonEncodedText Region = JsonEncodedText.Encode(TelemetrySchema.RegionKey);
    private static readonly JsonEncodedText Character = JsonEncodedText.Encode(TelemetrySchema.CharacterKey);
    private static readonly JsonEncodedText Account = JsonEncodedText.Encode(TelemetrySchema.AccountKey);

    public static void WriteHeader(Utf8JsonWriter writer, TelemetryFileHeader header)
    {
        writer.WriteStartObject();
        writer.WriteString(SchemaKey, header.Schema);
        writer.WriteNumber(VersionKey, header.Version);
        writer.WriteString(RunKey, header.RunId);
        writer.WriteNumber(StartedKey, header.StartedUtcMs);
        writer.WriteNumber(FileKey, header.FileIndex);
        writer.WriteEndObject();
    }

    /// <summary>Writes one record. A kind missing from the schema is written by number with no payload fields.</summary>
    public static void WriteRecord(Utf8JsonWriter writer, in TelemetryRecord record)
    {
        writer.WriteStartObject();
        var known = TelemetrySchema.TryGet(record.Kind, out var layout);
        if (known)
            writer.WriteString(Kind, layout.EncodedName);
        else
            writer.WriteNumber(Kind, (ushort)record.Kind);

        writer.WriteNumber(Time, record.UtcMs);
        WriteIfSet(writer, EventId, record.EventId);
        WriteIfSet(writer, World, record.WorldId);
        WriteIfSet(writer, Region, record.RegionId);
        WriteIfSet(writer, Character, record.CharacterId);
        WriteIfSet(writer, Account, record.AccountId);

        if (known)
        {
            var fields = layout.Fields;
            WriteField(writer, fields[0], record.Subject);
            WriteField(writer, fields[1], record.Target);
            WriteField(writer, fields[2], record.V0);
            WriteField(writer, fields[3], record.V1);
            WriteField(writer, fields[4], record.V2);
            WriteField(writer, fields[5], record.V3);
        }

        writer.WriteEndObject();
    }

    public static TelemetryFileHeader ParseHeader(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var header = new TelemetryFileHeader(
            root.GetProperty("schema").GetString() ?? "",
            root.GetProperty("v").GetInt32(),
            root.GetProperty("run").GetString() ?? "",
            root.GetProperty("startedMs").GetInt64(),
            root.GetProperty("file").GetInt32());
        if (header.Schema != TelemetrySchema.Name || header.Version != TelemetrySchema.Version)
            throw new NotSupportedException($"Telemetry schema {header.Schema} v{header.Version} is not {TelemetrySchema.Name} v{TelemetrySchema.Version}.");
        return header;
    }

    public static TelemetryRecord ParseRecord(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var kindElement = root.GetProperty(TelemetrySchema.KindKey);
        KindLayout? layout = null;
        TelemetryKind kind;
        if (kindElement.ValueKind == JsonValueKind.String)
        {
            var name = kindElement.GetString() ?? "";
            if (!TelemetrySchema.TryGet(name, out layout))
                throw new FormatException($"Unknown telemetry kind '{name}'.");
            kind = layout.Kind;
        }
        else
        {
            kind = (TelemetryKind)kindElement.GetUInt16();
        }

        long Field(int index) =>
            layout?.Fields[index].Name is { } fieldName && root.TryGetProperty(fieldName, out var value) ? value.GetInt64() : 0;

        return new TelemetryRecord(
            kind,
            root.GetProperty(TelemetrySchema.TimeKey).GetInt64(),
            Optional(root, TelemetrySchema.EventIdKey),
            (int)Optional(root, TelemetrySchema.WorldKey),
            (int)Optional(root, TelemetrySchema.RegionKey),
            Optional(root, TelemetrySchema.CharacterKey),
            Optional(root, TelemetrySchema.AccountKey),
            Field(0),
            Field(1),
            Field(2),
            Field(3),
            Field(4),
            Field(5));
    }

    /// <summary>Reads a whole file: the header, then every record. For tests and offline tools, not the game server.</summary>
    public static (TelemetryFileHeader Header, List<TelemetryRecord> Records) ReadFile(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var header = ParseHeader(reader.ReadLine() ?? throw new FormatException($"Empty telemetry file: {path}"));
        var records = new List<TelemetryRecord>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
                records.Add(ParseRecord(line));
        }

        return (header, records);
    }

    private static long Optional(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) ? value.GetInt64() : 0;

    private static void WriteIfSet(Utf8JsonWriter writer, JsonEncodedText key, long value)
    {
        if (value != 0)
            writer.WriteNumber(key, value);
    }

    private static void WriteField(Utf8JsonWriter writer, (string? Name, JsonEncodedText Encoded) field, long value)
    {
        if (field.Name != null)
            writer.WriteNumber(field.Encoded, value);
    }
}
