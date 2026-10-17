using System.Runtime.InteropServices;
using Shared.Data.Tables;

namespace GameServer.Telemetry;

/// <summary>
/// Bounded queue between producers (world threads, the network thread) and the <see cref="TelemetryWriter"/>
/// (spec 7.2, architecture rule 10). <see cref="TryEnqueue"/> never waits: when the queue is full the record is dropped
/// and counted, so telemetry drops before the game blocks.
/// </summary>
/// <remarks>
/// A lock-free bounded ring (D. Vyukov's MPMC design, used here with a single consumer): each cell carries a sequence
/// number that says whether it is free for the producer at a position or filled for the consumer. Producers claim a
/// position with one compare-exchange and never wait on the consumer. Records are structs stored in place, so enqueueing
/// allocates nothing.
/// </remarks>
public sealed class TelemetryQueue
{
    private readonly Cell[] _cells;
    private readonly long _mask;
    private PaddedLong _enqueuePosition;
    private PaddedLong _dequeuePosition;
    private PaddedLong _dropped;

    public TelemetryQueue(TelemetrySettings settings)
        : this(settings.QueueCapacity)
    {
    }

    /// <param name="capacity">A power of two (validated in <see cref="TelemetrySettings"/>).</param>
    public TelemetryQueue(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Must be a power of two of at least 2.");

        _cells = new Cell[capacity];
        _mask = capacity - 1;
        for (var i = 0; i < capacity; i++)
            _cells[i].Sequence = i;
    }

    public int Capacity => _cells.Length;

    /// <summary>Records refused because the queue was full, since creation.</summary>
    public long Dropped => Volatile.Read(ref _dropped.Value);

    /// <summary>Records waiting for the writer; approximate while producers run.</summary>
    public int Count =>
        (int)Math.Clamp(Volatile.Read(ref _enqueuePosition.Value) - Volatile.Read(ref _dequeuePosition.Value), 0, _cells.Length);

    /// <summary>Adds a record, or drops it and counts the drop when the queue is full. Safe from any thread; never waits.</summary>
    public bool TryEnqueue(in TelemetryRecord record)
    {
        var position = Volatile.Read(ref _enqueuePosition.Value);
        while (true)
        {
            ref var cell = ref _cells[position & _mask];
            var difference = Volatile.Read(ref cell.Sequence) - position;
            if (difference == 0)
            {
                var observed = Interlocked.CompareExchange(ref _enqueuePosition.Value, position + 1, position);
                if (observed == position)
                {
                    cell.Record = record;
                    Volatile.Write(ref cell.Sequence, position + 1);
                    return true;
                }

                position = observed;
            }
            else if (difference < 0)
            {
                // The cell still holds the record from one lap ago: the queue is full.
                Interlocked.Increment(ref _dropped.Value);
                return false;
            }
            else
            {
                // Another producer took this position; retry from the current one.
                position = Volatile.Read(ref _enqueuePosition.Value);
            }
        }
    }

    /// <summary>
    /// Moves up to <c>destination.Length</c> records out of the queue, oldest first, and returns how many.
    /// Single consumer: only one thread (the writer) may call this.
    /// </summary>
    public int DrainTo(Span<TelemetryRecord> destination)
    {
        var position = _dequeuePosition.Value;
        var count = 0;
        while (count < destination.Length)
        {
            ref var cell = ref _cells[position & _mask];
            if (Volatile.Read(ref cell.Sequence) != position + 1)
                break; // empty, or a producer claimed the cell and has not finished writing it

            destination[count++] = cell.Record;
            Volatile.Write(ref cell.Sequence, position + _cells.Length);
            position++;
        }

        Volatile.Write(ref _dequeuePosition.Value, position);
        return count;
    }

    private struct Cell
    {
        public long Sequence;
        public TelemetryRecord Record;
    }

    /// <summary>Keeps each counter on its own cache line, so producers and the consumer do not slow each other down.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct PaddedLong
    {
        [FieldOffset(64)] public long Value;
    }
}
