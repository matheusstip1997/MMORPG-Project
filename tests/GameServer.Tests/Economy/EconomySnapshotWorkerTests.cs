using GameServer.Economy;
using GameServer.Persistence;
using Npgsql;
using Shared.Data.Tables;

namespace GameServer.Tests.Economy;

/// <summary>
/// The economy snapshot writer runs off the tick (WP-M4-5, spec 1.6, CLAUDE.md: no I/O on the tick thread): the world's
/// <c>Save</c> only queues, even while the database hangs; rows go in the order taken, a failed insert is retried with the
/// next snapshot, the queue is capped (oldest dropped, counted), and dispose writes what is left.
/// </summary>
public class EconomySnapshotWorkerTests
{
    private static EconomySnapshot Snapshot(long created) => new(1_000 + created, 1_000, created, 0, 0, 0, []);

    private static EconomySettings Settings(int queueMax, int rejectTries = 3, int shutdownWaitMs = 5_000) =>
        new() { SnapshotQueueMax = queueMax, SnapshotRejectTries = rejectTries, SnapshotShutdownWaitMs = shutdownWaitMs };

    [Fact]
    public async Task A_row_the_database_refuses_is_dropped_after_its_tries_and_the_rows_behind_it_are_written()
    {
        // Fixer backlog M4 wave 0, item 1: a refused row (here a CHECK violation) used to stay at the head for ever.
        var store = new RefusingStore(refusedGold: 1);
        var log = new List<string>();
        var worker = new EconomySnapshotWorker(store, 1, Settings(queueMax: 10, rejectTries: 3), log: line => { lock (log) log.Add(line); });

        worker.Save(Snapshot(1));
        await store.Attempts.WaitAsync(TimeSpan.FromSeconds(10)); // try 1 refused
        worker.Save(Snapshot(2));
        worker.Save(Snapshot(3));
        await worker.DisposeAsync(); // tries 2 and 3 (with the next snapshots, then at dispose), then the rows behind it

        Assert.Equal([2L, 3L], store.Written.Select(s => s.GoldCreated));
        Assert.Equal((2L, 1L, 0L, 0L), (worker.Saved, worker.Rejected, worker.Dropped, worker.Unwritten));
        Assert.Single(log, l => l.Contains("refused 3 times and is dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_table_drops_no_row_however_often_it_fails()
    {
        // Review of #252: a failure that says nothing about the row (here 42P01, the migration not applied yet) keeps it at the
        // head with the rows behind it, even with a single try allowed for refused rows.
        var store = new RefusingStore(refusedGold: 1, PostgresErrorCodes.UndefinedTable);
        var worker = new EconomySnapshotWorker(store, 1, Settings(queueMax: 10, rejectTries: 1));
        worker.Save(Snapshot(1));
        await store.Attempts.WaitAsync(TimeSpan.FromSeconds(10));
        worker.Save(Snapshot(2));
        await worker.DisposeAsync();

        Assert.Empty(store.Written);
        Assert.Equal((0L, 0L, 2L), (worker.Rejected, worker.Dropped, worker.Unwritten)); // both still held at shutdown
    }

    [Fact]
    public async Task While_the_database_hangs_the_hand_over_stays_bounded_and_counts_the_dropped_at_once()
    {
        // Item 2: the channel was unbounded, so a hung insert let every later snapshot pile up in memory.
        var store = new GatedStore();
        var worker = new EconomySnapshotWorker(store, 1, Settings(queueMax: 2));
        worker.Save(Snapshot(1));
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the insert of row 1 hangs
        for (int i = 2; i <= 10; i++)
            worker.Save(Snapshot(i));

        Assert.Equal(7, worker.Dropped); // rows 2 to 8, while the database still hangs; 9 and 10 wait

        store.Gate.SetResult();
        await worker.DisposeAsync();
        Assert.Equal([1L, 9L, 10L], store.Written.Select(s => s.GoldCreated));
    }

    [Fact]
    public async Task Dispose_waits_at_most_the_shutdown_wait_for_a_hung_database_and_counts_what_was_not_written()
    {
        var store = new GatedStore(); // never opened
        var log = new List<string>();
        var worker = new EconomySnapshotWorker(store, 1, Settings(queueMax: 10, shutdownWaitMs: 200), log: line => { lock (log) log.Add(line); });
        worker.Save(Snapshot(1));
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        worker.Save(Snapshot(2));

        var dispose = worker.DisposeAsync().AsTask();
        Assert.Same(dispose, await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(30)))); // used to wait for ever
        await dispose;

        Assert.True(store.Cancelled.Task.IsCompleted, "the insert in flight was not cancelled");
        Assert.Equal((0L, 2L), (worker.Saved, worker.Unwritten));
        Assert.Single(log, l => l.Contains("2 rows not written at shutdown", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("23514", true)] // check violation: the row itself
    [InlineData("22003", true)] // numeric value out of range: the row itself
    [InlineData("53300", false)] // too many clients
    [InlineData("08006", false)] // connection failure
    [InlineData("57P01", false)] // admin shutdown
    [InlineData("42P01", false)] // the table is missing (a migration not applied yet): says nothing about the row
    [InlineData("42501", false)] // no permission
    [InlineData("28P01", false)] // sign-in failed
    [InlineData("3D000", false)] // the database is missing
    public void Only_a_refusal_of_the_row_itself_counts_toward_its_tries_anything_else_keeps_it_at_the_head(string sqlState, bool refusal)
    {
        // Review of #252: the rule names what is about the row (classes 22 and 23), so an unknown failure keeps the rows.
        Assert.Equal(refusal, EconomySnapshotWorker.IsRefusal(new PostgresException("x", "ERROR", "ERROR", sqlState)));
        Assert.False(EconomySnapshotWorker.IsRefusal(new TimeoutException()));
        Assert.False(EconomySnapshotWorker.IsRefusal(new OperationCanceledException()));
        Assert.False(EconomySnapshotWorker.IsRefusal(new NpgsqlException("no answer")));
        Assert.True(EconomySnapshotWorker.IsRefusal(new InvalidOperationException())); // building the row failed
    }

    [Fact]
    public async Task Save_returns_at_once_while_the_database_hangs_and_the_insert_runs_on_another_thread()
    {
        var store = new GatedStore();
        var worker = new EconomySnapshotWorker(store, 1, Settings(queueMax: 10));
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
        var worker = new EconomySnapshotWorker(store, 1, Settings(queueMax: 10), log: line => { lock (log) log.Add(line); });

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
        var worker = new EconomySnapshotWorker(store, 1, Settings(queueMax: 2), log: line => { lock (log) log.Add(line); });
        for (int i = 1; i <= 5; i++)
            worker.Save(Snapshot(i));
        await worker.DisposeAsync();
        Assert.Equal(3, worker.Dropped); // 5 taken, 2 kept, nothing written
        // However many tries fail and rows drop, the log gets one line for each, not one per row.
        Assert.Single(log, l => l.Contains("writing a row failed", StringComparison.Ordinal));
        Assert.Single(log, l => l.Contains("dropping the oldest", StringComparison.Ordinal));

        store.Recover();
        var again = new EconomySnapshotWorker(store, 1, Settings(queueMax: 2));
        again.Save(Snapshot(6));
        await again.DisposeAsync();
        Assert.Equal([6L], store.Written.Select(s => s.GoldCreated));
    }

    private sealed class GatedStore : IEconomySnapshotStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<EconomySnapshot> Written { get; } = [];
        public List<int> Threads { get; } = [];

        public async Task InsertSnapshotAsync(int worldId, EconomySnapshot snapshot, CancellationToken ct = default)
        {
            lock (Threads)
                Threads.Add(Environment.CurrentManagedThreadId);
            ct.Register(() => Cancelled.TrySetResult());
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

    /// <summary>Refuses every row with <c>GoldCreated == refusedGold</c> as the database would a CHECK violation; writes the rest.</summary>
    private sealed class RefusingStore(long refusedGold, string sqlState = PostgresErrorCodes.CheckViolation) : IEconomySnapshotStore
    {
        public SemaphoreSlim Attempts { get; } = new(0);
        public List<EconomySnapshot> Written { get; } = [];

        public Task InsertSnapshotAsync(int worldId, EconomySnapshot snapshot, CancellationToken ct = default)
        {
            Attempts.Release();
            if (snapshot.GoldCreated == refusedGold)
                throw new PostgresException("refused", "ERROR", "ERROR", sqlState);
            Written.Add(snapshot);
            return Task.CompletedTask;
        }
    }
}
