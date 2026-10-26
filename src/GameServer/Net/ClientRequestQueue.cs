using Shared.Protocol;

namespace GameServer.Net;

/// <summary>A decoded reliable request of a client, waiting for the world (backlog G: pickups; local chat: the line).</summary>
public readonly record struct ClientRequest(ReliableMessageId Kind, uint DropEntityId, string? Text = null);

/// <summary>
/// Reliable requests of one session (spec 1.6: the network thread only enqueues decoded requests; the world's simulation
/// thread takes them all in phase 1). Fixed capacity (<c>LootSettings.maxQueuedPickupRequests</c>): a request that
/// arrives with the queue full is dropped and counted, so a client cannot queue work faster than the world does it.
/// Never allocates after construction.
/// </summary>
public sealed class ClientRequestQueue
{
    private readonly ClientRequest[] _requests;
    private readonly Lock _lock = new();
    private int _head;
    private int _count;
    private long _dropped;

    /// <param name="capacity">0 accepts nothing (a world without the tables the requests need).</param>
    public ClientRequestQueue(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "must not be negative");
        _requests = new ClientRequest[capacity];
    }

    public int Capacity => _requests.Length;

    /// <summary>Requests dropped because the queue was full.</summary>
    public long Dropped
    {
        get { lock (_lock) return _dropped; }
    }

    /// <summary>False (and counted as dropped) when the queue is full.</summary>
    public bool TryEnqueue(in ClientRequest request)
    {
        lock (_lock)
        {
            if (_count == _requests.Length)
            {
                _dropped++;
                return false;
            }
            _requests[(_head + _count) % _requests.Length] = request;
            _count++;
            return true;
        }
    }

    public bool TryDequeue(out ClientRequest request)
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                request = default;
                return false;
            }
            request = _requests[_head];
            _head = (_head + 1) % _requests.Length;
            _count--;
            return true;
        }
    }
}
