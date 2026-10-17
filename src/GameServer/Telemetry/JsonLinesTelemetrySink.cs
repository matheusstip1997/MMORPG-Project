using System.Text.Json;
using Shared.Util;

namespace GameServer.Telemetry;

/// <summary>Where the <see cref="TelemetryWriter"/> puts records. Called only from the writer thread.</summary>
public interface ITelemetrySink : IDisposable
{
    void Write(ReadOnlySpan<TelemetryRecord> records);

    /// <summary>Pushes buffered records to storage; called after each drained batch.</summary>
    void Flush();
}

/// <summary>
/// Local M1 sink (spec 7.2): appends JSON lines (<see cref="TelemetryJsonFormat"/>) to
/// <c>&lt;directory&gt;/&lt;runId&gt;-&lt;nnnn&gt;.jsonl</c>, starting a new file once one passes
/// <see cref="Shared.Data.Tables.TelemetrySettings.MaxFileBytes"/>. Files are never deleted here.
/// TODO(open): raw event retention (spec 7.2 "configured retention") and shipping files to analytics storage belong to
/// Workers and have no values yet.
/// </summary>
public sealed class JsonLinesTelemetrySink : ITelemetrySink
{
    private readonly string _directory;
    private readonly string _runId;
    private readonly IClock _clock;
    private readonly long _maxFileBytes;
    private FileStream _stream;
    private Utf8JsonWriter _json;
    private int _fileIndex;

    public JsonLinesTelemetrySink(string directory, string runId, IClock clock, int maxFileBytes)
    {
        if (runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Run ID must be usable in a file name.", nameof(runId));

        _directory = directory;
        _runId = runId;
        _clock = clock;
        _maxFileBytes = maxFileBytes;
        Directory.CreateDirectory(directory);
        (_stream, _json) = OpenFile();
    }

    /// <summary>The file being written now.</summary>
    public string CurrentPath => _stream.Name;

    public void Write(ReadOnlySpan<TelemetryRecord> records)
    {
        foreach (ref readonly var record in records)
        {
            TelemetryJsonFormat.WriteRecord(_json, record);
            EndLine();
        }

        if (_stream.Position >= _maxFileBytes)
            Rotate();
    }

    public void Flush() => _stream.Flush();

    public void Dispose()
    {
        _json.Dispose();
        _stream.Dispose();
    }

    private (FileStream Stream, Utf8JsonWriter Json) OpenFile()
    {
        var path = Path.Combine(_directory, $"{_runId}-{_fileIndex:D4}.jsonl");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024);
        var json = new Utf8JsonWriter(stream);
        _stream = stream;
        _json = json;
        TelemetryJsonFormat.WriteHeader(json, new TelemetryFileHeader(
            TelemetrySchema.Name, TelemetrySchema.Version, _runId, _clock.UtcNowMs, _fileIndex));
        EndLine();
        return (stream, json);
    }

    private void Rotate()
    {
        Dispose();
        _fileIndex++;
        (_stream, _json) = OpenFile();
    }

    private void EndLine()
    {
        // One writer, many top-level values: flush the object, add the newline, and reset for the next one.
        _json.Flush();
        _stream.WriteByte((byte)'\n');
        _json.Reset();
    }
}

/// <summary>Names one server run in telemetry files: UTC start time plus a random suffix, safe in file names.</summary>
public static class TelemetryRunId
{
    public static string Create(IClock clock) =>
        DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNowMs).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'")
        + "-" + Guid.NewGuid().ToString("N")[..8];
}
