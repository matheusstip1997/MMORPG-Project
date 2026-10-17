using StackExchange.Redis;

namespace GameServer.Sessions;

/// <summary>
/// The character ownership lock (R-2.1-06): a character is loaded in exactly one place at a time. Every hold has its
/// own token, so a late release or refresh of an old hold can never touch a newer one. Called from tasks the
/// <see cref="SessionManager"/> starts, never awaited on the tick thread.
/// </summary>
public interface IOwnershipLocks
{
    /// <summary>Takes the lock if nobody holds it. False when another token holds it.</summary>
    Task<bool> TryAcquireAsync(long characterId, string token);

    /// <summary>Extends the lock's time to live. False when <paramref name="token"/> no longer holds it (expired or taken).</summary>
    Task<bool> RefreshAsync(long characterId, string token);

    /// <summary>Deletes the lock if <paramref name="token"/> still holds it; does nothing otherwise.</summary>
    Task ReleaseAsync(long characterId, string token);
}

/// <summary>
/// Redis lock <c>{prefix}realm:{realmId}:char:{characterId}</c> holding the hold's token, with a TTL of
/// <c>SessionSettings.OwnershipLockTtlMs</c> that the world's heartbeat refreshes (spec 2.1 implementation). The key
/// carries the realm because character ids are per realm database (R-2.1-03). A crashed server's locks expire on
/// their own after the TTL.
/// </summary>
public sealed class RedisOwnershipLocks(IConnectionMultiplexer redis, string keyPrefix, int realmId, int ttlMs) : IOwnershipLocks
{
    // Compare-and-set scripts: only the token that holds the lock may extend or delete it.
    private const string RefreshScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('pexpire', KEYS[1], ARGV[2]) else return 0 end";
    private const string ReleaseScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    private readonly TimeSpan _ttl = TimeSpan.FromMilliseconds(ttlMs);

    public string KeyFor(long characterId) => $"{keyPrefix}realm:{realmId}:char:{characterId}";

    public Task<bool> TryAcquireAsync(long characterId, string token) =>
        redis.GetDatabase().StringSetAsync(KeyFor(characterId), token, _ttl, When.NotExists);

    public async Task<bool> RefreshAsync(long characterId, string token)
    {
        var result = await redis.GetDatabase().ScriptEvaluateAsync(RefreshScript,
            [(RedisKey)KeyFor(characterId)], [(RedisValue)token, (RedisValue)ttlMs]);
        return (long)result == 1;
    }

    public Task ReleaseAsync(long characterId, string token) =>
        redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [(RedisKey)KeyFor(characterId)], [(RedisValue)token]);
}
