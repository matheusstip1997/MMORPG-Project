using GameServer.Telemetry;
using GameServer.Tests.Support;

namespace GameServer.Tests.Telemetry;

public class TelemetryWriterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Keeps records in memory; can be made to block or throw.</summary>
    private sealed class MemorySink : ITelemetrySink
    {
        private readonly object _lock = new();
        private readonly List<TelemetryRecord> _records = [];

        public ManualResetEventSlim Gate { get; } = new(true);

        public ManualResetEventSlim Entered { get; } = new(false);

        public bool Throw { get; set; }

        public int Flushes { get; private set; }

        public bool Disposed { get; private set; }

        public List<TelemetryRecord> Records
        {
            get
            {
                lock (_lock)
                    return [.. _records];
            }
        }

        public void Write(ReadOnlySpan<TelemetryRecord> records)
        {
            Entered.Set();
            Gate.Wait();
            if (Throw)
                throw new IOException("disk full");
            lock (_lock)
                _records.AddRange(records.ToArray());
        }

        public void Flush() => Flushes++;

        public void Dispose() => Disposed = true;
    }

    private static TelemetryRecord Record(long n) => new(TelemetryKind.Damaged, 5_000 + n, EventId: n, V1: n);

    [Fact]
    public void Phase_12_flush_gets_records_to_the_sink()
    {
        var queue = new TelemetryQueue(64);
        var sink = new MemorySink();
        // A long interval: only the explicit flush can wake the writer in time.
        using var writer = new TelemetryWriter(queue, sink, TestPipelineSettings.Telemetry(flushIntervalMs: 60_000), new FakeClock());
        writer.Start();

        for (var i = 1; i <= 10; i++)
            queue.TryEnqueue(Record(i));
        writer.Flush();

        Assert.True(SpinWait.SpinUntil(() => sink.Records.Count == 10, Timeout));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), sink.Records.Select(r => r.EventId));
    }

    [Fact]
    public void Writer_drains_on_its_interval_without_a_flush()
    {
        var queue = new TelemetryQueue(64);
        var sink = new MemorySink();
        using var writer = new TelemetryWriter(queue, sink, TestPipelineSettings.Telemetry(flushIntervalMs: 10), new FakeClock());
        writer.Start();

        queue.TryEnqueue(Record(1));

        Assert.True(SpinWait.SpinUntil(() => sink.Records.Count == 1, Timeout));
    }

    [Fact]
    public void Dispose_writes_what_is_left_and_disposes_the_sink()
    {
        var queue = new TelemetryQueue(1_024);
        var sink = new MemorySink();
        var writer = new TelemetryWriter(queue, sink, TestPipelineSettings.Telemetry(maxBatchRecords: 7, flushIntervalMs: 60_000), new FakeClock());
        writer.Start();
        for (var i = 1; i <= 500; i++)
            queue.TryEnqueue(Record(i));

        writer.Dispose();

        Assert.Equal(500, sink.Records.Count);
        Assert.Equal(500, writer.RecordsWritten);
        Assert.True(sink.Disposed);
        writer.Flush(); // a late phase-12 call after shutdown is harmless
    }

    [Fact]
    public void Drops_are_reported_as_a_record()
    {
        var clock = new FakeClock();
        var queue = new TelemetryQueue(4);
        var sink = new MemorySink();
        var writer = new TelemetryWriter(queue, sink, TestPipelineSettings.Telemetry(queueCapacity: 4, maxBatchRecords: 4), clock);
        for (var i = 1; i <= 10; i++)
            queue.TryEnqueue(Record(i));

        writer.DrainOnce();
        queue.TryEnqueue(Record(11));
        writer.DrainOnce(); // no new drops: no new report

        var records = sink.Records;
        Assert.Equal(new TelemetryRecord(TelemetryKind.QueueDropped, clock.UtcNowMs, V0: 6, V1: 6), records[0]);
        Assert.Equal([1L, 2, 3, 4, 11], records.Skip(1).Select(r => r.EventId));
        writer.Dispose();
    }

    [Fact]
    public void A_failing_sink_is_counted_and_the_writer_keeps_going()
    {
        var queue = new TelemetryQueue(64);
        var sink = new MemorySink { Throw = true };
        var writer = new TelemetryWriter(queue, sink, TestPipelineSettings.Telemetry(maxBatchRecords: 4), new FakeClock());
        for (var i = 1; i <= 6; i++)
            queue.TryEnqueue(Record(i));

        writer.DrainOnce();
        Assert.Equal(2, writer.SinkFailures);
        Assert.Equal(6, writer.RecordsLost);

        sink.Throw = false;
        queue.TryEnqueue(Record(7));
        writer.DrainOnce();
        Assert.Equal([7L], sink.Records.Select(r => r.EventId));
        writer.Dispose();
    }

    /// <summary>
    /// Soak: producers hammer the queue while the sink is completely stuck. If a producer ever waited for the writer,
    /// the producers could not finish while the gate is closed. Every record is either written once or counted as dropped.
    /// </summary>
    [Fact]
    public void Producers_never_wait_for_a_stuck_writer()
    {
        const int producers = 4;
        const int perProducer = 250_000;
        var queue = new TelemetryQueue(4_096);
        var sink = new MemorySink();
        sink.Gate.Reset();
        var writer = new TelemetryWriter(queue, sink, TestPipelineSettings.Telemetry(queueCapacity: 4_096, maxBatchRecords: 512, flushIntervalMs: 10), new FakeClock());
        writer.Start();

        var accepted = new long[producers];
        var threads = Enumerable.Range(0, producers).Select(p => new Thread(() =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                if (queue.TryEnqueue(new TelemetryRecord(TelemetryKind.Moved, i, Subject: p, V0: i)))
                    accepted[p]++;
                if (i % 1_000 == 0)
                    writer.Flush(); // phase 12 from a busy tick
            }
        })).ToList();

        threads.ForEach(t => t.Start());
        Assert.True(sink.Entered.Wait(Timeout), "the writer never reached the sink");
        foreach (var thread in threads)
            Assert.True(thread.Join(Timeout), "a producer waited for the stuck writer");
        Assert.False(sink.Gate.IsSet);

        sink.Gate.Set();
        writer.Dispose();

        var written = sink.Records.Where(r => r.Kind == TelemetryKind.Moved).ToList();
        var dropReports = sink.Records.Where(r => r.Kind == TelemetryKind.QueueDropped).ToList();
        Assert.Equal(accepted.Sum(), written.Count);
        Assert.Equal(producers * perProducer, accepted.Sum() + queue.Dropped);
        Assert.True(queue.Dropped > 0, "the stuck sink should have forced drops");
        Assert.Equal(queue.Dropped, dropReports.Sum(r => r.V0));
    }
}
