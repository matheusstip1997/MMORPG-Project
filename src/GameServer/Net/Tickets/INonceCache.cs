using Shared.Util;

namespace GameServer.Net.Tickets;

/// <summary>Remembers used ticket nonces until the ticket expires, so a ticket works only once (R-2.5-01).</summary>
public interface INonceCache
{
    /// <summary>Returns false if the nonce was already used.</summary>
    bool TryUse(ReadOnlySpan<byte> nonce, long expiresAtMs);
}

/// <summary>Single-server nonce cache. Moves to Redis with SessionManager (spec 2.5 implementation notes).</summary>
public sealed class InMemoryNonceCache(IClock clock) : INonceCache
{
    private readonly Dictionary<string, long> _used = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public bool TryUse(ReadOnlySpan<byte> nonce, long expiresAtMs)
    {
        var key = Convert.ToHexString(nonce);
        lock (_lock)
        {
            RemoveExpired();
            return _used.TryAdd(key, expiresAtMs);
        }
    }

    private void RemoveExpired()
    {
        var now = clock.UtcNowMs;
        foreach (var (key, expiresAt) in _used)
        {
            if (expiresAt <= now)
                _used.Remove(key);
        }
    }
}
