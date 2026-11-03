using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Input frames of one session, waiting for the world (spec 1.6: network threads only enqueue decoded inputs).
/// The network thread enqueues and the world's simulation thread dequeues one frame per tick.
/// Fixed capacity (<c>ServerLoop.MaxQueuedInputFrames</c>): when full, the oldest frame is dropped (its buttons carry over
/// to the next frame), so a client that sends faster than the tick cannot build up latency or move faster. A backlog below
/// that is drained by the world's input catch-up (<c>ServerLoop.InputBacklogTicks</c>). Never allocates after construction.
/// </summary>
public sealed class InputQueue
{
    private readonly InputFrame[] _frames;
    private readonly Lock _lock = new();
    private int _head;
    private int _count;
    private long _dropped;
    private int _roundTripMs = -1;
    private uint _lockTargetId;
    private readonly (uint TargetId, ushort FromSequence)[] _lockChanges = new (uint, ushort)[MaxPendingLockChanges];
    private int _lockChangeHead;
    private int _lockChangeCount;

    /// <param name="requestCapacity">Size of <see cref="Requests"/>; 0 accepts no reliable requests.</param>
    /// <param name="chatCapacity">Size of <see cref="ChatRequests"/>; 0 accepts no chat lines.</param>
    public InputQueue(int capacity, int requestCapacity = 0, int chatCapacity = 0)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "must be at least 1");
        _frames = new InputFrame[capacity];
        Requests = new ClientRequestQueue(requestCapacity);
        ChatRequests = new ClientRequestQueue(chatCapacity);
    }

    /// <summary>The session's decoded reliable requests (pickups), next to its input frames.</summary>
    public ClientRequestQueue Requests { get; }

    /// <summary>The session's chat lines (<c>ChatSend</c>, R-6.5-01), apart from the pickups so neither can crowd out the other.</summary>
    public ClientRequestQueue ChatRequests { get; }

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

    /// <summary>
    /// A <c>TargetLockChanged</c> (R-2.2-06, M3 decision 15): from the frame <paramref name="fromSequence"/> on, the client
    /// aims its locked frames at <paramref name="targetId"/> (0: none). Called by the network thread in arrival order (the
    /// reliable channel is ordered); frames queued before it keep the previous target. More than
    /// <see cref="MaxPendingLockChanges"/> changes between two frames fold the oldest into the current target.
    /// </summary>
    public void ChangeLock(uint targetId, ushort fromSequence)
    {
        lock (_lock)
        {
            if (_lockChangeCount == _lockChanges.Length)
            {
                _lockTargetId = _lockChanges[_lockChangeHead].TargetId;
                _lockChangeHead = (_lockChangeHead + 1) % _lockChanges.Length;
                _lockChangeCount--;
            }
            _lockChanges[(_lockChangeHead + _lockChangeCount) % _lockChanges.Length] = (targetId, fromSequence);
            _lockChangeCount++;
        }
    }

    /// <summary>
    /// The locked target of the frame <paramref name="sequence"/>, 0 for none: what phase 1 checks a locked frame's facing
    /// against. The world thread asks for every dequeued frame in order, which applies each change that frame reached; a new
    /// connection starts with a new queue, so with none.
    /// </summary>
    public uint LockTargetFor(ushort sequence)
    {
        lock (_lock)
        {
            while (_lockChangeCount > 0 && !Sequence.IsNewer(_lockChanges[_lockChangeHead].FromSequence, sequence))
            {
                _lockTargetId = _lockChanges[_lockChangeHead].TargetId;
                _lockChangeHead = (_lockChangeHead + 1) % _lockChanges.Length;
                _lockChangeCount--;
            }
            return _lockTargetId;
        }
    }

    /// <summary>Lock changes kept until a frame reaches them (a few Tab presses within one queue's worth of frames).</summary>
    public const int MaxPendingLockChanges = 4;

    public void Enqueue(in InputFrame frame)
    {
        lock (_lock)
        {
            var added = frame;
            if (_count == _frames.Length)
            {
                // Spec 1.6: the oldest frame is dropped, but its buttons are not: they are held in the frame after it, so
                // a press the dropped frame carried still reaches the action machine (fix/parry-feel). Its movement is
                // lost (the client's prediction corrects it); the newer frames keep the latest movement input.
                var dropped = _frames[_head];
                _head = (_head + 1) % _frames.Length;
                _count--;
                _dropped++;
                if (_count > 0)
                    Keep(ref _frames[_head], dropped);
                else
                    Keep(ref added, dropped);
            }
            _frames[(_head + _count) % _frames.Length] = added;
            _count++;
        }
    }

    /// <summary>A dropped frame's held buttons and hotbar slot carry over to <paramref name="into"/>.</summary>
    private static void Keep(ref InputFrame into, in InputFrame dropped)
    {
        into.Actions |= dropped.Actions;
        if (into.SkillSlot == 0)
            into.SkillSlot = dropped.SkillSlot;
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
