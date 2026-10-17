using Shared.Data.Tables;

namespace GameServer.Tests.Support;

/// <summary>Valid <see cref="EventBusLimits"/> and <see cref="TelemetrySettings"/> rows; tests narrow what they exercise.</summary>
public static class TestPipelineSettings
{
    public static EventBusLimits BusLimits(int maxEventsPerDispatch = 10_000, int initialCapacityPerType = 64) => new()
    {
        MaxEventsPerDispatch = maxEventsPerDispatch,
        InitialCapacityPerType = initialCapacityPerType,
    };

    public static TelemetrySettings Telemetry(
        int queueCapacity = 1_024,
        int maxBatchRecords = 128,
        int flushIntervalMs = 10,
        int maxFileBytes = 1_048_576) => new()
    {
        QueueCapacity = queueCapacity,
        MaxBatchRecords = maxBatchRecords,
        FlushIntervalMs = flushIntervalMs,
        MaxFileBytes = maxFileBytes,
    };
}

/// <summary>A fresh folder under the system temp directory, deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mmorpg-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
