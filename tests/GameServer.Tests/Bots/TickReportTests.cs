using BotClient.LoadTest;
using GameServer.Telemetry;
using GameServer.Tests.Support;

namespace GameServer.Tests.Bots;

/// <summary>
/// Backlog J: the load test's tick p99 comes from the files the real telemetry writer writes (spec 7.2), so a renamed
/// kind or field breaks this test rather than silently giving an empty report.
/// </summary>
public class TickReportTests
{
    [Fact]
    public void Percentiles_window_and_failure_counts_come_from_the_written_files()
    {
        using var directory = new TempDirectory();
        var clock = new FakeClock();
        var settings = TestPipelineSettings.Telemetry();
        var queue = new TelemetryQueue(1_024);
        using (var writer = new TelemetryWriter(queue, new JsonLinesTelemetrySink(directory.Path, "run", clock, settings.MaxFileBytes), settings, clock))
        {
            // 100 ticks of 1..100 ms at t = 1000..1099, and two ticks outside the window.
            queue.TryEnqueue(new TelemetryRecord(TelemetryKind.TickTiming, 999, WorldId: 1, V0: 0, V1: 999_000, V2: 0));
            for (int i = 1; i <= 100; i++)
                queue.TryEnqueue(new TelemetryRecord(TelemetryKind.TickTiming, 999 + i, WorldId: 1, V0: i, V1: i * 1_000, V2: i == 50 ? 7_000 : 0));
            queue.TryEnqueue(new TelemetryRecord(TelemetryKind.TickTiming, 2_000, WorldId: 1, V0: 101, V1: 888_000, V2: 0));
            // Running totals: overruns 2 -> 5 and malformed inputs 1 -> 4 inside the window.
            queue.TryEnqueue(new TelemetryRecord(TelemetryKind.TickStats, 1_000, WorldId: 1, V0: 1, V1: 1, V2: 2, V3: 0));
            queue.TryEnqueue(new TelemetryRecord(TelemetryKind.TickStats, 1_090, WorldId: 1, V0: 1, V1: 1, V2: 5, V3: 0));
            queue.TryEnqueue(new TelemetryRecord(TelemetryKind.WorldCounters, 1_000, WorldId: 1, V0: 1, V1: 0, V2: 0, V3: 0));
            queue.TryEnqueue(new TelemetryRecord(TelemetryKind.WorldCounters, 1_090, WorldId: 1, V0: 4, V1: 2, V2: 0, V3: 0));
            queue.TryEnqueue(new TelemetryRecord(TelemetryKind.Security, 1_500, Subject: 3, V0: 1));
            // Unreliable sends refused as too large (a running total): any in the run counts, inside the window or not.
            queue.TryEnqueue(new TelemetryRecord(TelemetryKind.SendCounters, 1_000, WorldId: 1, Subject: 3, V0: 0));
            queue.TryEnqueue(new TelemetryRecord(TelemetryKind.SendCounters, 1_800, WorldId: 1, Subject: 3, V0: 2, V1: 1_100, V2: 1_020));
            writer.Start();
            writer.Flush();
        }

        var report = TickReport.Read(directory.Path, fromMs: 1_000, toMs: 1_099);

        Assert.Equal(100, report.Ticks);
        Assert.Equal((50_000L, 95_000L, 99_000L, 100_000L), (report.P50Us, report.P95Us, report.P99Us, report.MaxUs));
        Assert.Equal(7_000, report.MaxLateUs);
        Assert.Equal((3L, 3L, 2L), (report.Overruns, report.MalformedInputs, report.StaleFrames));
        Assert.Equal((0, 1, 0), (report.WorldCrashed, report.SecurityEvents, report.QueueDropped));
        Assert.Equal(2, report.UnreliableTooLarge);
    }

    [Theory]
    [InlineData(50, 5)]
    [InlineData(99, 10)]
    [InlineData(100, 10)]
    [InlineData(1, 1)]
    public void Percentiles_use_the_nearest_rank(int percent, long expected)
    {
        long[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.Equal(expected, TickReport.Percentile(sorted, percent));
        Assert.Equal(0, TickReport.Percentile([], percent));
    }
}
