using System.Diagnostics;
using GameServer.Telemetry;
using GameServer.Tests.Support;

namespace GameServer.Tests.Telemetry;

public class TelemetryQueueTests
{
    private static TelemetryRecord Record(long n) => new(TelemetryKind.Moved, 1_000 + n, EventId: n, V0: n);

    [Fact]
    public void Records_come_out_in_order()
    {
        var queue = new TelemetryQueue(8);
        for (var i = 1; i <= 5; i++)
            Assert.True(queue.TryEnqueue(Record(i)));

        var buffer = new TelemetryRecord[16];
        var count = queue.DrainTo(buffer);

        Assert.Equal(5, count);
        Assert.Equal([1L, 2, 3, 4, 5], buffer.Take(count).Select(r => r.EventId));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Drain_respects_the_destination_size()
    {
        var queue = new TelemetryQueue(8);
        for (var i = 1; i <= 5; i++)
            queue.TryEnqueue(Record(i));

        var buffer = new TelemetryRecord[3];
        Assert.Equal(3, queue.DrainTo(buffer));
        Assert.Equal(2, queue.DrainTo(buffer));
        Assert.Equal([4L, 5], buffer.Take(2).Select(r => r.EventId));
    }

    [Fact]
    public void A_full_queue_drops_and_counts_without_blocking()
    {
        var queue = new TelemetryQueue(4);
        for (var i = 1; i <= 4; i++)
            Assert.True(queue.TryEnqueue(Record(i)));

        var watch = Stopwatch.StartNew();
        for (var i = 5; i <= 100_004; i++)
            Assert.False(queue.TryEnqueue(Record(i)));
        watch.Stop();

        Assert.Equal(100_000, queue.Dropped);
        // A rejection is a few loads and one increment; this bound only catches a queue that waits.
        Assert.True(watch.ElapsedMilliseconds < 2_000, $"100k rejected enqueues took {watch.ElapsedMilliseconds} ms");

        // The oldest records were kept; there is room again after a drain.
        var buffer = new TelemetryRecord[8];
        Assert.Equal(4, queue.DrainTo(buffer));
        Assert.Equal([1L, 2, 3, 4], buffer.Take(4).Select(r => r.EventId));
        Assert.True(queue.TryEnqueue(Record(7)));
    }

    [Fact]
    public void Positions_wrap_around_many_laps()
    {
        var queue = new TelemetryQueue(4);
        var buffer = new TelemetryRecord[3];
        long next = 1, expected = 1;
        for (var lap = 0; lap < 1_000; lap++)
        {
            for (var i = 0; i < 3; i++)
                Assert.True(queue.TryEnqueue(Record(next++)));
            var count = queue.DrainTo(buffer);
            for (var i = 0; i < count; i++)
                Assert.Equal(expected++, buffer[i].EventId);
        }

        Assert.Equal(next, expected);
        Assert.Equal(0, queue.Dropped);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public void Capacity_must_be_a_power_of_two(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryQueue(capacity));
    }

    [Fact]
    public void Enqueue_allocates_nothing()
    {
        var queue = new TelemetryQueue(TestPipelineSettings.Telemetry(queueCapacity: 1_024));
        queue.TryEnqueue(Record(0));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5_000; i++)
            queue.TryEnqueue(Record(i)); // most of these are drops once the queue is full
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Concurrent_producers_lose_nothing_that_was_accepted()
    {
        const int producers = 4;
        const int perProducer = 50_000;
        var queue = new TelemetryQueue(1_024);
        var accepted = new long[producers];
        var drained = new List<TelemetryRecord>();
        var done = 0;

        var threads = Enumerable.Range(0, producers).Select(p => new Thread(() =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                if (queue.TryEnqueue(new TelemetryRecord(TelemetryKind.Moved, 0, Subject: p, V0: i)))
                    accepted[p]++;
            }

            Interlocked.Increment(ref done);
        })).ToList();
        threads.ForEach(t => t.Start());

        var buffer = new TelemetryRecord[256];
        while (true)
        {
            var finished = Volatile.Read(ref done) == producers;
            int count;
            while ((count = queue.DrainTo(buffer)) > 0)
                drained.AddRange(buffer.Take(count));
            if (finished)
                break;
        }

        threads.ForEach(t => t.Join());
        Assert.Equal(accepted.Sum(), drained.Count);
        Assert.Equal(producers * perProducer, accepted.Sum() + queue.Dropped);
        // Per producer, records stay in the order they were enqueued.
        foreach (var group in drained.GroupBy(r => r.Subject))
            Assert.Equal(group.Select(r => r.V0).Order(), group.Select(r => r.V0));
    }
}
