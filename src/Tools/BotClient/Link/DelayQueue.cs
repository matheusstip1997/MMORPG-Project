using System.Buffers;

namespace BotClient.Link;

/// <summary>A datagram waiting in a <see cref="DelayQueue{T}"/>: its pooled bytes and where it goes.</summary>
public readonly struct DelayedDatagram<T>
{
    public DelayedDatagram(long dueUs, long order, byte[] buffer, int length, T target)
    {
        DueUs = dueUs;
        Order = order;
        Buffer = buffer;
        Length = length;
        Target = target;
    }

    public long DueUs { get; }

    /// <summary>Arrival order: datagrams due at the same time leave in the order they came.</summary>
    public long Order { get; }

    /// <summary>Rented from <see cref="ArrayPool{T}.Shared"/>; <see cref="DelayQueue{T}.Return"/> gives it back.</summary>
    public byte[] Buffer { get; }

    public int Length { get; }

    public T Target { get; }

    public ReadOnlySpan<byte> Data => Buffer.AsSpan(0, Length);
}

/// <summary>
/// Datagrams held until their delivery time (backlog J link simulation): a min-heap on (due time, arrival order), so ties
/// keep arrival order. Copies each datagram into a pooled buffer. Not thread safe.
/// </summary>
public sealed class DelayQueue<T>
{
    private readonly PriorityQueue<DelayedDatagram<T>, (long DueUs, long Order)> _heap = new();
    private long _order;

    public int Count => _heap.Count;

    public void Enqueue(long dueUs, ReadOnlySpan<byte> data, T target)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, data.Length));
        data.CopyTo(buffer);
        long order = _order++;
        _heap.Enqueue(new DelayedDatagram<T>(dueUs, order, buffer, data.Length, target), (dueUs, order));
    }

    /// <summary>The earliest due time, or false when empty.</summary>
    public bool TryPeekDue(out long dueUs)
    {
        if (_heap.TryPeek(out _, out var priority))
        {
            dueUs = priority.DueUs;
            return true;
        }
        dueUs = 0;
        return false;
    }

    /// <summary>Takes the earliest datagram if it is due at <paramref name="nowUs"/>. Pass it to <see cref="Return"/> after sending.</summary>
    public bool TryDequeueDue(long nowUs, out DelayedDatagram<T> datagram)
    {
        if (_heap.TryPeek(out datagram, out var priority) && priority.DueUs <= nowUs)
        {
            _heap.Dequeue();
            return true;
        }
        datagram = default;
        return false;
    }

    public static void Return(in DelayedDatagram<T> datagram) => ArrayPool<byte>.Shared.Return(datagram.Buffer);

    /// <summary>Drops everything still waiting.</summary>
    public void Clear()
    {
        while (_heap.TryDequeue(out var datagram, out _))
            Return(datagram);
    }
}
