using System.Text;
using System.Text.Json;
using GameServer.Telemetry;
using GameServer.Tests.Support;

namespace GameServer.Tests.Telemetry;

public class TelemetryFormatTests
{
    private static string Line(in TelemetryRecord record)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            TelemetryJsonFormat.WriteRecord(writer, record);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A record with every field the kind's layout names set to a distinct non-zero value.</summary>
    private static TelemetryRecord Full(KindLayout layout) => new(
        layout.Kind,
        UtcMs: 1_800_000_000_123,
        EventId: 99,
        WorldId: 2,
        RegionId: 3,
        CharacterId: 4_000_000_000L,
        AccountId: 76_561_198_000_000_000L,
        Subject: layout.Subject == null ? 0 : 11,
        Target: layout.Target == null ? 0 : 12,
        V0: layout.V0 == null ? 0 : -13,
        V1: layout.V1 == null ? 0 : 14,
        V2: layout.V2 == null ? 0 : long.MaxValue,
        V3: layout.V3 == null ? 0 : long.MinValue);

    [Fact]
    public void Every_kind_has_a_layout()
    {
        foreach (var kind in Enum.GetValues<TelemetryKind>())
            Assert.True(TelemetrySchema.TryGet(kind, out _), $"{kind} has no layout in TelemetrySchema");
        Assert.Equal(Enum.GetValues<TelemetryKind>().Length, TelemetrySchema.Kinds.Count);
    }

    [Fact]
    public void Field_names_are_unique_and_do_not_clash_with_the_envelope()
    {
        string[] envelope =
        [
            TelemetrySchema.KindKey, TelemetrySchema.TimeKey, TelemetrySchema.EventIdKey, TelemetrySchema.WorldKey,
            TelemetrySchema.RegionKey, TelemetrySchema.CharacterKey, TelemetrySchema.AccountKey,
        ];
        foreach (var layout in TelemetrySchema.Kinds)
        {
            var names = new[] { layout.Subject, layout.Target, layout.V0, layout.V1, layout.V2, layout.V3 }.OfType<string>().ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.Empty(names.Intersect(envelope));
        }
    }

    [Fact]
    public void Every_kind_round_trips()
    {
        foreach (var layout in TelemetrySchema.Kinds)
        {
            var record = Full(layout);
            Assert.Equal(record, TelemetryJsonFormat.ParseRecord(Line(record)));
        }
    }

    [Fact]
    public void Format_v1_is_pinned()
    {
        // If this test has to change, files written by older servers read differently: bump TelemetrySchema.Version.
        var record = new TelemetryRecord(TelemetryKind.Damaged, 1_800_000_000_000, EventId: 12, WorldId: 1, CharacterId: 42,
            Subject: 7, Target: 9, V0: 3, V1: 120, V2: 880);

        Assert.Equal(
            """{"k":"Damaged","t":1800000000000,"e":12,"w":1,"c":42,"source":7,"target":9,"action":3,"amount":120,"hpAfter":880}""",
            Line(record));
        Assert.Equal(1, TelemetrySchema.Version);
    }

    [Fact]
    public void Unknown_kinds_are_written_by_number()
    {
        var record = new TelemetryRecord((TelemetryKind)999, 5);

        var line = Line(record);

        Assert.Equal("""{"k":999,"t":5}""", line);
        Assert.Equal(record, TelemetryJsonFormat.ParseRecord(line));
    }

    [Fact]
    public void Header_with_another_version_is_refused()
    {
        Assert.Throws<NotSupportedException>(() =>
            TelemetryJsonFormat.ParseHeader("""{"schema":"mmorpg.telemetry","v":2,"run":"r","startedMs":1,"file":0}"""));
    }

    [Fact]
    public void Sink_writes_a_header_and_records_that_read_back()
    {
        using var directory = new TempDirectory();
        var clock = new FakeClock();
        var records = TelemetrySchema.Kinds.Select(Full).ToArray();
        string path;

        using (var sink = new JsonLinesTelemetrySink(directory.Path, "run1", clock, maxFileBytes: 1_048_576))
        {
            sink.Write(records);
            sink.Flush();
            path = sink.CurrentPath;
        }

        var (header, read) = TelemetryJsonFormat.ReadFile(path);
        Assert.Equal(new TelemetryFileHeader(TelemetrySchema.Name, TelemetrySchema.Version, "run1", clock.UtcNowMs, 0), header);
        Assert.Equal(records, read);
        Assert.EndsWith("run1-0000.jsonl", path);
    }

    [Fact]
    public void Sink_rotates_files_by_size_and_loses_nothing()
    {
        using var directory = new TempDirectory();
        var written = Enumerable.Range(1, 200)
            .Select(i => new TelemetryRecord(TelemetryKind.Moved, i, EventId: i, Subject: i, V0: i, V1: i, V2: i))
            .ToArray();

        using (var sink = new JsonLinesTelemetrySink(directory.Path, "run2", new FakeClock(), maxFileBytes: 2_048))
        {
            foreach (var chunk in written.Chunk(10))
                sink.Write(chunk);
        }

        var files = Directory.GetFiles(directory.Path, "run2-*.jsonl").Order(StringComparer.Ordinal).ToList();
        Assert.True(files.Count > 2, $"expected rotation, got {files.Count} file(s)");
        var read = files.Select(TelemetryJsonFormat.ReadFile).ToList();
        Assert.Equal(Enumerable.Range(0, files.Count), read.Select(f => f.Header.FileIndex));
        Assert.Equal(written, read.SelectMany(f => f.Records));
    }

    [Fact]
    public void Run_ids_are_file_name_safe_and_start_with_the_utc_time()
    {
        var id = TelemetryRunId.Create(new FakeClock(1_800_000_000_000));

        Assert.StartsWith("20270115T080000Z-", id);
        Assert.Equal(-1, id.IndexOfAny(Path.GetInvalidFileNameChars()));
    }
}
