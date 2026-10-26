namespace GameServer.Net;

/// <summary>
/// How many reliable requests of one peer may wait for the world at once (R-2.5-03 reliable backlog;
/// <c>ServerLoop.maxPendingRequestsPerPeer</c>). The network thread takes a slot before it posts a request to the
/// <see cref="GameServer.World.WorldInbox"/> and drops the request when none is free; the world gives the slot back when
/// it applies the request. So a client that floods the reliable channel cannot grow the inbox. Thread safe.
/// </summary>
public sealed class RequestBudget
{
    private readonly int _capacity;
    private int _pending;

    public RequestBudget(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "must be at least 1");
        _capacity = capacity;
    }

    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Network thread: false when <c>capacity</c> requests already wait.</summary>
    public bool TryTake()
    {
        while (true)
        {
            int pending = Volatile.Read(ref _pending);
            if (pending >= _capacity)
                return false;
            if (Interlocked.CompareExchange(ref _pending, pending + 1, pending) == pending)
                return true;
        }
    }

    /// <summary>World thread: the request was applied (or dropped there).</summary>
    public void Release() => Interlocked.Decrement(ref _pending);
}
