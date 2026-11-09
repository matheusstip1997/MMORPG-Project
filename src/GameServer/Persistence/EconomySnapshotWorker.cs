using System.Threading.Channels;
using GameServer.Economy;

namespace GameServer.Persistence;

/// <summary>Where the world hands its economy snapshots to be written. Must never block the calling (tick) thread.</summary>
public interface IEconomySnapshotSink
{
    void Save(EconomySnapshot snapshot);
}

/// <summary>
/// Writes the world's economy snapshots (WP-M4-5, R-7.2-02) off the world thread (spec 1.6), like the demand state
/// (<see cref="DemandStateWorker"/>), but every snapshot is a row of its own: one background loop inserts them in the order
/// taken. A failed insert stays queued and is tried again with the next snapshot (a retry of a row that did commit is a
/// no-op); at most <c>EconomySettings.snapshotQueueMax</c> rows wait, and the oldest beyond that is dropped and counted. A
/// snapshot moves no value: <c>gold_ledger</c> stays the authoritative record.
/// <para>
/// So an operator sees that no rows are written, the host's log gets one line when inserts start failing (with the
/// exception), one when rows start being dropped, and one when inserts work again (with how many failed and were dropped
/// meanwhile): a few lines per outage, however long it lasts.
/// </para>
/// </summary>
public sealed class EconomySnapshotWorker : IEconomySnapshotSink, IAsyncDisposable
{
    private readonly IEconomySnapshotStore _store;
    private readonly int _worldId;
    private readonly int _queueMax;
    private readonly Action<string> _log;
    private readonly Channel<EconomySnapshot> _snapshots =
        Channel.CreateUnbounded<EconomySnapshot>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<EconomySnapshot> _pending = new();
    private readonly Task _loop;
    private long _saved;
    private long _failures;
    private long _dropped;
    private long _failuresInOutage;
    private long _droppedInOutage;

    /// <param name="log">The host's log (default: standard error, as the other background workers).</param>
    public EconomySnapshotWorker(IEconomySnapshotStore store, int worldId, int queueMax, Action<string>? log = null)
    {
        if (queueMax < 1)
            throw new ArgumentOutOfRangeException(nameof(queueMax));
        _store = store;
        _worldId = worldId;
        _queueMax = queueMax;
        _log = log ?? Console.Error.WriteLine;
        _loop = Task.Run(RunAsync);
    }

    public long Saved => Interlocked.Read(ref _saved);
    public long Failures => Interlocked.Read(ref _failures);
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Save(EconomySnapshot snapshot) => _snapshots.Writer.TryWrite(snapshot);

    /// <summary>Stops taking snapshots, writes what is still queued (one more try for rows a failure left), then stops.</summary>
    public async ValueTask DisposeAsync()
    {
        _snapshots.Writer.TryComplete();
        await _loop;
    }

    private async Task RunAsync()
    {
        while (await _snapshots.Reader.WaitToReadAsync())
        {
            while (_snapshots.Reader.TryRead(out var snapshot))
            {
                _pending.Enqueue(snapshot);
                while (_pending.Count > _queueMax)
                {
                    _pending.Dequeue();
                    Interlocked.Increment(ref _dropped);
                    if (_droppedInOutage++ == 0)
                        _log($"Economy snapshots: more than {_queueMax} rows wait to be written; dropping the oldest (world {_worldId}).");
                }
            }
            await WritePendingAsync();
        }
        await WritePendingAsync();
    }

    private async Task WritePendingAsync()
    {
        while (_pending.Count > 0)
        {
            try
            {
                await _store.InsertSnapshotAsync(_worldId, _pending.Peek());
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _failures);
                if (_failuresInOutage++ == 0)
                    _log($"Economy snapshots: writing a row failed, rows are kept and tried again with the next (world {_worldId}): {e.Message}");
                return; // tried again with the next snapshot, or at dispose
            }
            _pending.Dequeue();
            Interlocked.Increment(ref _saved);
            if (_failuresInOutage > 0 || _droppedInOutage > 0)
            {
                _log($"Economy snapshots: writing works again (world {_worldId}); {_failuresInOutage} failed tries, {_droppedInOutage} rows dropped meanwhile.");
                _failuresInOutage = 0;
                _droppedInOutage = 0;
            }
        }
    }
}
