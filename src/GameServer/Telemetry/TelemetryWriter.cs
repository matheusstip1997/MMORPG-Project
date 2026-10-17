using Shared.Data.Tables;
using Shared.Util;

namespace GameServer.Telemetry;

/// <summary>
/// Drains the <see cref="TelemetryQueue"/> into an <see cref="ITelemetrySink"/> on its own background thread, outside
/// the tick (spec 7.2, architecture rule 10). It wakes when a tick asks for a flush (phase 12) and at least every
/// <see cref="TelemetrySettings.FlushIntervalMs"/>. When the queue dropped records it writes a
/// <see cref="TelemetryKind.QueueDropped"/> record, so gaps are visible in the files.
/// </summary>
public sealed class TelemetryWriter : IDisposable
{
    private readonly TelemetryQueue _queue;
    private readonly ITelemetrySink _sink;
    private readonly IClock _clock;
    private readonly int _flushIntervalMs;
    private readonly TelemetryRecord[] _batch;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private int _flushRequested;
    private int _stopping;
    private bool _disposed;
    private long _droppedReported;
    private long _recordsWritten;
    private long _recordsLost;
    private long _sinkFailures;

    public TelemetryWriter(TelemetryQueue queue, ITelemetrySink sink, TelemetrySettings settings, IClock clock)
    {
        _queue = queue;
        _sink = sink;
        _clock = clock;
        _flushIntervalMs = settings.FlushIntervalMs;
        _batch = new TelemetryRecord[settings.MaxBatchRecords];
    }

    /// <summary>Records handed to the sink without an error, including <see cref="TelemetryKind.QueueDropped"/> reports.</summary>
    public long RecordsWritten => Interlocked.Read(ref _recordsWritten);

    /// <summary>Records drained from the queue but lost because the sink threw.</summary>
    public long RecordsLost => Interlocked.Read(ref _recordsLost);

    public long SinkFailures => Interlocked.Read(ref _sinkFailures);

    /// <summary>Starts the background thread.</summary>
    public void Start()
    {
        if (_thread != null)
            throw new InvalidOperationException("Already started.");
        _thread = new Thread(Run) { IsBackground = true, Name = "telemetry-writer" };
        _thread.Start();
    }

    /// <summary>
    /// Tick phase 12: asks the writer to drain now. Never blocks and never does I/O on the caller's thread; repeated
    /// requests before the writer wakes cost one interlocked exchange each.
    /// </summary>
    public void Flush()
    {
        if (Interlocked.Exchange(ref _flushRequested, 1) == 0)
            _wake.Set();
    }

    /// <summary>Stops the thread after a last drain, then disposes the sink. Records enqueued after this are not written.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Volatile.Write(ref _stopping, 1);
        if (_thread != null)
        {
            _wake.Set();
            _thread.Join();
        }
        else
        {
            DrainOnce();
        }

        // The wake event is left to the finalizer: a late Flush from a tick thread must not hit a disposed handle.
        _sink.Dispose();
    }

    /// <summary>One drain pass on the caller's thread. For tests that run without the background thread.</summary>
    internal void DrainOnce()
    {
        var wrote = ReportDrops();
        int count;
        while ((count = _queue.DrainTo(_batch)) > 0)
        {
            WriteToSink(_batch.AsSpan(0, count));
            wrote = true;
        }

        if (!wrote)
            return;

        try
        {
            _sink.Flush();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _sinkFailures);
        }
    }

    private void Run()
    {
        while (true)
        {
            _wake.WaitOne(_flushIntervalMs);
            Volatile.Write(ref _flushRequested, 0);
            var stopping = Volatile.Read(ref _stopping) == 1;
            DrainOnce();
            if (stopping)
                return;
        }
    }

    private bool ReportDrops()
    {
        var dropped = _queue.Dropped;
        if (dropped == _droppedReported)
            return false;

        var report = new TelemetryRecord(TelemetryKind.QueueDropped, _clock.UtcNowMs, V0: dropped - _droppedReported, V1: dropped);
        _droppedReported = dropped;
        WriteToSink(new ReadOnlySpan<TelemetryRecord>(in report));
        return true;
    }

    private void WriteToSink(ReadOnlySpan<TelemetryRecord> records)
    {
        try
        {
            _sink.Write(records);
            Interlocked.Add(ref _recordsWritten, records.Length);
        }
        catch (Exception)
        {
            // A broken disk must not stop the writer; the batch is lost and counted.
            Interlocked.Increment(ref _sinkFailures);
            Interlocked.Add(ref _recordsLost, records.Length);
        }
    }
}
