using GameServer.Economy;
using GameServer.Persistence;

namespace GameServer.Tests.Economy;

/// <summary>
/// The economy snapshot writer runs off the tick (WP-M4-5, spec 1.6, CLAUDE.md: no I/O on the tick thread): the world's
/// <c>Save</c> only queues, even while the database hangs; rows go in the order taken, a failed insert is retried with the
/// next snapshot, the queue is capped (oldest dropped, counted), and dispose writes what is left.
/// </summary>
public class EconomySnapshotWorkerTests
{
    private static EconomySnapshot Snapshot(long created) => new(1_000 + created, 1_000, created, 0, 0, 0, []);

    [Fact]
    public async Task Save_returns_at_once_while_the_database_hangs_and_the_insert_runs_on_another_thread()
    {
        var store = new GatedStore();
        var worker = new EconomySnapshotWorker(store, 1, queueMax: 10);
        // A dedicated thread stands for the world's tick thread (never a pool thread the worker could share).
        int tickThread = 0;
        var tick = new Thread(() =>
        {
            tickThread = Environment.CurrentManagedThreadId;
            worker.Save(Snapshot(1));
            store.Entered.Task.Wait(TimeSpan.FromSeconds(10)); // the insert started, and hangs
            worker.Save(Snapshot(2)); // returns while the database hangs: the tick goes on
        });
        tick.Start();
        Assert.True(tick.Join(TimeSpan.FromSeconds(20)), "the tick thread was blocked by the database");
        Assert.Empty(store.Written);

        store.Gate.SetResult();
        await worker.DisposeAsync();

        Assert.Equal([1L, 2L], store.Written.Select(s => s.GoldCreated));
        Assert.DoesNotContain(tickThread, store.Threads);
    }

    [Fact]
    public async Task A_failed_insert_is_retried_with_the_next_snapshot_in_order()
    {
        var store = new FlakyStore(failures: 1);
        var log = new List<string>();
        var worker = new EconomySnapshotWorker(store, 1, queueMax: 10, log: line => { lock (log) log.Add(line); });

        worker.Save(Snapshot(1));
        await store.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the first insert failed
        worker.Save(Snapshot(2));
        await worker.DisposeAsync();

        Assert.Equal([1L, 2L], store.Written.Select(s => s.GoldCreated));
        Assert.Equal((2L, 1L, 0L), (worker.Saved, worker.Failures, worker.Dropped));
        // The operator sees the outage begin (with the exception) and end, one line each.
        Assert.Collection(log,
            line => Assert.Contains("writing a row failed", line, StringComparison.Ordinal),
            line => Assert.Contains("works again", line, StringComparison.Ordinal));
        Assert.Contains("database down", log[0], StringComparison.Ordinal);
        Assert.Contains("1 failed tries, 0 rows dropped", log[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_the_database_is_down_the_queue_keeps_the_newest_rows_and_counts_the_dropped()
    {
        var store = new FlakyStore(failures: int.MaxValue);
        var log = new List<string>();
        var worker = new EconomySnapshotWorker(store, 1, queueMax: 2, log: line => { lock (log) log.Add(line); });
        for (int i = 1; i <= 5; i++)
            worker.Save(Snapshot(i));
        await worker.DisposeAsync();
        Assert.Equal(3, worker.Dropped); // 5 taken, 2 kept, nothing written
        // However many tries fail and rows drop, the log gets one line for each, not one per row.
        Assert.Single(log, l => l.Contains("writing a row failed", StringComparison.Ordinal));
        Assert.Single(log, l => l.Contains("dropping the oldest", StringComparison.Ordinal));

        store.Recover();
        var again = new EconomySnapshotWorker(store, 1, queueMax: 2);
        again.Save(Snapshot(6));
        await again.DisposeAsync();
        Assert.Equal([6L], store.Written.Select(s => s.GoldCreated));
    }

    private sealed class GatedStore : IEconomySnapshotStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<EconomySnapshot> Written { get; } = [];
        public List<int> Threads { get; } = [];

        public async Task InsertSnapshotAsync(int worldId, EconomySnapshot snapshot, CancellationToken ct = default)
        {
            lock (Threads)
                Threads.Add(Environment.CurrentManagedThreadId);
            Entered.TrySetResult();
            await Gate.Task;
            lock (Written)
                Written.Add(snapshot);
        }
    }

    private sealed class FlakyStore(int failures) : IEconomySnapshotStore
    {
        private int _failures = failures;

        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<EconomySnapshot> Written { get; } = [];

        public void Recover() => _failures = 0;

        public Task InsertSnapshotAsync(int worldId, EconomySnapshot snapshot, CancellationToken ct = default)
        {
            Attempted.TrySetResult();
            if (_failures > 0)
            {
                _failures--;
                throw new TimeoutException("database down");
            }
            Written.Add(snapshot);
            return Task.CompletedTask;
        }
    }
}
