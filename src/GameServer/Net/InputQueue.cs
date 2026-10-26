using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Input frames of one session, waiting for the world (spec 1.6: network threads only enqueue decoded inputs).
/// The network thread enqueues and the world's simulation thread dequeues one frame per tick.
/// Fixed capacity (<c>ServerLoop.MaxQueuedInputFrames</c>): when full, the oldest frame is dropped, so a client that
/// sends faster than the tick cannot build up latency or move faster. Never allocates after construction.
/// </summary>
public sealed class InputQueue
{
    private readonly InputFrame[] _frames;
    private readonly Lock _lock = new();
    private int _head;
    private int _count;
    private long _dropped;
    private int _roundTripMs = -1;

    /// <param name="requestCapacity">Size of <see cref="Requests"/>; 0 accepts no reliable requests.</param>
    public InputQueue(int capacity, int requestCapacity = 0)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "must be at least 1");
        _frames = new InputFrame[capacity];
        Requests = new ClientRequestQueue(requestCapacity);
    }

    /// <summary>The session's decoded reliable requests (pickups), next to its input frames.</summary>
    public ClientRequestQueue Requests { get; }

    public int Capacity => _frames.Length;

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    /// <summary>Frames dropped because the queue was full.</summary>
    public long Dropped
    {
        get { lock (_lock) return _dropped; }
    }

    /// <summary>
    /// The peer's round-trip time in ms as the transport measured it, or -1 before the first sample (and for queues that
    /// have no peer). Written by the network thread, read by the world thread in phase 1 (R-3.1-03 clock-sync limits).
    /// </summary>
    public int RoundTripMs
    {
        get => Volatile.Read(ref _roundTripMs);
        set => Volatile.Write(ref _roundTripMs, value);
    }

    public void Enqueue(in InputFrame frame)
    {
        lock (_lock)
        {
            if (_count == _frames.Length)
            {
                _head = (_head + 1) % _frames.Length;
                _count--;
                _dropped++;
            }
            _frames[(_head + _count) % _frames.Length] = frame;
            _count++;
        }
    }

    public bool TryDequeue(out InputFrame frame)
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                frame = default;
                return false;
            }
            frame = _frames[_head];
            _head = (_head + 1) % _frames.Length;
            _count--;
            return true;
        }
    }
}
