using System.Threading.Channels;
using GameServer.Economy;
using Npgsql;
using Shared.Data.Tables;

namespace GameServer.Persistence;

/// <summary>Where the world hands its economy snapshots to be written. Must never block the calling (tick) thread.</summary>
public interface IEconomySnapshotSink
{
    void Save(EconomySnapshot snapshot);
}

/// <summary>
/// Writes the world's economy snapshots (WP-M4-5, R-7.2-02) off the world thread (spec 1.6), like the demand state
/// (<see cref="DemandStateWorker"/>), but every snapshot is a row of its own: one background loop inserts them in the order
/// taken. A snapshot moves no value: <c>gold_ledger</c> stays the authoritative record, so every bound below drops rows
/// rather than wait.
/// <list type="bullet">
/// <item>The hand-over is a channel of <c>EconomySettings.snapshotQueueMax</c> rows: while the database hangs the oldest row
/// waiting is dropped and counted at once (the tick never waits and memory stays bounded).</item>
/// <item>An insert that failed for a transient reason (the database unreachable, a timeout, too many connections) stays at the
/// head and is tried again with the next snapshot (a retry of a row that did commit is a no-op); at most
/// <c>snapshotQueueMax</c> rows wait behind it, the oldest beyond that is dropped and counted.</item>
/// <item>A row the database refuses for any other reason (a constraint, a bad value) is tried
/// <c>EconomySettings.snapshotRejectTries</c> times, then dropped and counted (<see cref="Rejected"/>), so it never holds the
/// rows behind it.</item>
/// <item>Dispose writes what is left and waits at most <c>EconomySettings.snapshotShutdownWaitMs</c>; then the write in
/// flight is cancelled and the rows still queued are counted as <see cref="Unwritten"/>.</item>
/// </list>
/// <para>
/// So an operator sees that no rows are written, the host's log gets one line when inserts start failing (with the
/// exception), one when rows start being dropped, one per rejected row, and one when inserts work again (with how many
/// failed and were dropped meanwhile): a few lines per outage, however long it lasts. Nothing is logged on the tick thread.
/// </para>
/// </summary>
public sealed class EconomySnapshotWorker : IEconomySnapshotSink, IAsyncDisposable
{
    private readonly IEconomySnapshotStore _store;
    private readonly int _worldId;
    private readonly int _queueMax;
    private readonly int _rejectTries;
    private readonly int _shutdownWaitMs;
    private readonly Action<string> _log;
    private readonly Channel<EconomySnapshot> _snapshots;
    private readonly Queue<EconomySnapshot> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _saved;
    private long _failures;
    private long _dropped;
    private long _rejected;
    private long _unwritten;
    private long _droppedLogged;
    private long _failuresInOutage;
    private long _droppedAtOutageStart;
    private int _headRejections;
    private int _finished;

    /// <param name="settings">The queue bound, the tries of a refused row and the shutdown wait (<see cref="EconomySettings"/>).</param>
    /// <param name="log">The host's log (default: standard error, as the other background workers).</param>
    public EconomySnapshotWorker(IEconomySnapshotStore store, int worldId, EconomySettings settings, Action<string>? log = null)
    {
        if (settings.SnapshotQueueMax < 1 || settings.SnapshotRejectTries < 1 || settings.SnapshotShutdownWaitMs < 0)
            throw new ArgumentOutOfRangeException(nameof(settings));
        _store = store;
        _worldId = worldId;
        _queueMax = settings.SnapshotQueueMax;
        _rejectTries = settings.SnapshotRejectTries;
        _shutdownWaitMs = settings.SnapshotShutdownWaitMs;
        _log = log ?? Console.Error.WriteLine;
        // Dropping the oldest is decided on the writer's (tick) thread: the callback only counts, the loop logs.
        _snapshots = Channel.CreateBounded<EconomySnapshot>(
            new BoundedChannelOptions(_queueMax) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest },
            _ => Interlocked.Increment(ref _dropped));
        _loop = Task.Run(RunAsync);
    }

    public long Saved => Interlocked.Read(ref _saved);
    public long Failures => Interlocked.Read(ref _failures);
    /// <summary>Rows dropped as the oldest beyond <c>snapshotQueueMax</c> (in the hand-over or behind a failing insert).</summary>
    public long Dropped => Interlocked.Read(ref _dropped);
    /// <summary>Rows the database refused <c>snapshotRejectTries</c> times, then dropped.</summary>
    public long Rejected => Interlocked.Read(ref _rejected);
    /// <summary>Rows still queued when the shutdown wait ran out.</summary>
    public long Unwritten => Interlocked.Read(ref _unwritten);

    public void Save(EconomySnapshot snapshot) => _snapshots.Writer.TryWrite(snapshot);

    /// <summary>
    /// Stops taking snapshots, writes what is still queued (one more try for rows a failure left) and stops, waiting at most
    /// <c>snapshotShutdownWaitMs</c>: past it the write in flight is cancelled and what is left is counted as unwritten.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _snapshots.Writer.TryComplete();
        if (await Task.WhenAny(_loop, Task.Delay(_shutdownWaitMs)) == _loop)
            return;
        // The wait ran out (a hung database): the insert in flight is cancelled and never awaited, so shutdown goes on. The
        // count read here races only with that insert returning, which can no longer report (one report per worker).
        _stop.Cancel();
        ReportUnwritten(_pending.Count + _snapshots.Reader.Count);
    }

    private async Task RunAsync()
    {
        while (await _snapshots.Reader.WaitToReadAsync())
        {
            Take();
            await WritePendingAsync();
        }
        Take();
        await WritePendingAsync();
        if (_pending.Count > 0)
            ReportUnwritten(_pending.Count);
    }

    private void ReportUnwritten(int rows)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
            return;
        Interlocked.Add(ref _unwritten, rows);
        _log($"Economy snapshots: {rows} rows not written at shutdown (world {_worldId}).");
    }

    /// <summary>Moves the handed-over rows behind the pending ones, keeping at most <c>snapshotQueueMax</c>.</summary>
    private void Take()
    {
        while (_snapshots.Reader.TryRead(out var snapshot))
        {
            _pending.Enqueue(snapshot);
            while (_pending.Count > _queueMax)
            {
                _pending.Dequeue(); // the head: its refusals go with it
                _headRejections = 0;
                Interlocked.Increment(ref _dropped);
            }
        }
        long dropped = Dropped;
        if (dropped > _droppedLogged)
        {
            if (_droppedLogged == _droppedAtOutageStart)
                _log($"Economy snapshots: more than {_queueMax} rows wait to be written; dropping the oldest (world {_worldId}).");
            _droppedLogged = dropped;
        }
    }

    private async Task WritePendingAsync()
    {
        while (_pending.Count > 0)
        {
            if (_stop.IsCancellationRequested)
                return;
            try
            {
                await _store.InsertSnapshotAsync(_worldId, _pending.Peek(), _stop.Token);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failures);
                if (_stop.IsCancellationRequested)
                    return;
                if (!IsTransient(e) && ++_headRejections >= _rejectTries)
                {
                    // Refused every time: the database will not take this row, so it no longer holds the rows behind it.
                    var refused = _pending.Dequeue();
                    _headRejections = 0;
                    Interlocked.Increment(ref _rejected);
                    _log($"Economy snapshots: the row taken at {refused.TakenAtMs} was refused {_rejectTries} times and is dropped (world {_worldId}): {e.Message}");
                    continue;
                }
                if (_failuresInOutage++ == 0)
                    _log($"Economy snapshots: writing a row failed, rows are kept and tried again with the next (world {_worldId}): {e.Message}");
                return; // tried again with the next snapshot, or at dispose
            }
            _pending.Dequeue();
            _headRejections = 0;
            Interlocked.Increment(ref _saved);
            long droppedInOutage = _droppedLogged - _droppedAtOutageStart;
            if (_failuresInOutage > 0 || droppedInOutage > 0)
            {
                _log($"Economy snapshots: writing works again (world {_worldId}); {_failuresInOutage} failed tries, {droppedInOutage} rows dropped meanwhile.");
                _failuresInOutage = 0;
                _droppedAtOutageStart = _droppedLogged;
            }
        }
    }

    /// <summary>
    /// A failure that says nothing about the row: the database unreachable or busy (Npgsql's <c>IsTransient</c>: connection,
    /// too many clients, serialization), an operator stopping the server (SQL state class 57) or a timeout. Anything else is
    /// the database refusing the row.
    /// </summary>
    internal static bool IsTransient(Exception e) => e switch
    {
        TimeoutException or OperationCanceledException => true,
        PostgresException postgres => postgres.IsTransient || postgres.SqlState.StartsWith("57", StringComparison.Ordinal),
        NpgsqlException => true, // no answer from the server at all
        _ => false,
    };
}
