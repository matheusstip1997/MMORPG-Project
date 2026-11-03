using GameServer.Net.Tickets;
using GameServer.Sessions;
using GameServer.Tests.Support;
using Shared.Protocol;
using StackExchange.Redis;

namespace GameServer.Tests.Sessions;

/// <summary>The Redis ownership lock (R-2.1-06) and ticket nonce cache (R-2.5-01) against a real Redis.</summary>
public sealed class RedisIntegrationTests : IAsyncLifetime
{
    private const int TtlMs = 30_000;
    private readonly string _prefix = TestRedis.NewPrefix();
    private ConnectionMultiplexer _redis = null!;

    public Task InitializeAsync()
    {
        _redis = TestRedis.Connect();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestRedis.DeleteAsync(_redis, _prefix);
        _redis.Dispose();
    }

    [Fact]
    public async Task One_token_holds_a_character_and_another_is_refused_until_it_is_released()
    {
        var locks = Locks(TtlMs);

        Assert.True(await locks.TryAcquireAsync(42, "server-a:1"));
        Assert.False(await locks.TryAcquireAsync(42, "server-b:1"));
        Assert.False(await locks.TryAcquireAsync(42, "server-a:2")); // a second hold of the same server too
        Assert.True(await locks.TryAcquireAsync(43, "server-b:1")); // other characters are independent

        await locks.ReleaseAsync(42, "server-b:1"); // not the holder: nothing happens
        Assert.Equal("server-a:1", (string?)await _redis.GetDatabase().StringGetAsync(locks.KeyFor(42)));

        await locks.ReleaseAsync(42, "server-a:1");
        Assert.True(await locks.TryAcquireAsync(42, "server-b:1"));
    }

    [Fact]
    public async Task The_lock_key_carries_the_realm_and_the_ttl()
    {
        var locks = Locks(TtlMs);
        await locks.TryAcquireAsync(7, "server-a:1");

        Assert.Equal($"{_prefix}realm:1:char:7", locks.KeyFor(7));
        var ttl = await _redis.GetDatabase().KeyTimeToLiveAsync(locks.KeyFor(7));
        Assert.InRange(ttl!.Value.TotalMilliseconds, TtlMs - 5_000, TtlMs);
    }

    [Fact]
    public async Task Refresh_extends_only_the_holder_and_a_lock_left_by_a_crashed_server_expires_with_its_ttl()
    {
        // Wave 2 fixer (a CI flake): the steps follow the key's own TTL in Redis instead of fixed sleeps, whose 400 ms margin
        // a loaded runner could overrun (the refresh then found the lock already expired).
        const int ttlMs = 2_000;
        var locks = Locks(ttlMs);
        var db = _redis.GetDatabase();
        var key = locks.KeyFor(9);
        Assert.True(await locks.TryAcquireAsync(9, "crashed:1"));
        Assert.False(await locks.RefreshAsync(9, "other:1"));

        double before = await WaitForTtlBelowAsync(db, key, ttlMs / 2); // half the TTL has run down
        Assert.True(await locks.RefreshAsync(9, "crashed:1")); // the TTL starts again
        Assert.True((await db.KeyTimeToLiveAsync(key))!.Value.TotalMilliseconds > before, "the refresh restarted the TTL");
        await WaitForTtlBelowAsync(db, key, ttlMs / 2 - 200); // the refresh came at half the TTL: now past the first one
        Assert.False(await locks.TryAcquireAsync(9, "other:1")); // past the first TTL since the acquire, still held

        // The holder stops refreshing (it crashed): the lock expires and another server may take the character.
        await WaitForTtlBelowAsync(db, key, 0);
        Assert.False(await locks.RefreshAsync(9, "crashed:1"));
        Assert.True(await locks.TryAcquireAsync(9, "other:1"));
    }

    /// <summary>Polls the key's TTL until it is below <paramref name="ms"/> (0: the key expired); returns the last TTL seen.</summary>
    private static async Task<double> WaitForTtlBelowAsync(IDatabase db, RedisKey key, double ms)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var ttl = await db.KeyTimeToLiveAsync(key);
            double left = ttl?.TotalMilliseconds ?? 0; // no TTL: the key is gone
            if (left < ms || (ms <= 0 && ttl == null))
                return left;
            await Task.Delay(20);
        }
        throw new TimeoutException($"{key} kept a TTL above {ms} ms");
    }

    [Fact]
    public void A_nonce_works_once_across_caches_and_expires_with_its_ticket()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var serverA = new RedisNonceCache(_redis, _prefix, clock);
        var serverB = new RedisNonceCache(_redis, _prefix, clock);
        var nonce = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        Assert.True(serverA.TryUse(nonce, clock.UtcNowMs + 60_000));
        Assert.False(serverB.TryUse(nonce, clock.UtcNowMs + 60_000)); // R-2.5-01: single use across servers
        Assert.True(serverB.TryUse(nonce.Reverse().ToArray(), clock.UtcNowMs + 60_000));

        var key = $"{_prefix}nonce:{Convert.ToHexString(nonce)}";
        var ttl = _redis.GetDatabase().KeyTimeToLive(key);
        Assert.InRange(ttl!.Value.TotalMilliseconds, 50_000, 60_000);
    }

    [Fact]
    public void A_nonce_store_that_cannot_be_reached_refuses_the_ticket_with_ServerUnavailable()
    {
        var clock = new FakeClock();
        var secret = Enumerable.Repeat((byte)7, 32).ToArray();
        var validator = new TicketValidator(new TicketKeyRing().Add(1, secret), new ThrowingNonceCache(), clock, 1, 60_000);
        var issued = new DevTicketIssuer(1, secret, clock, 60_000).Issue(5, 1, 0);

        Assert.Equal(ConnectRejectReason.ServerUnavailable, validator.Validate(issued.Ticket).Reason);
    }

    private RedisOwnershipLocks Locks(int ttlMs) => new(_redis, _prefix, realmId: 1, ttlMs);

    private sealed class ThrowingNonceCache : INonceCache
    {
        public bool TryUse(ReadOnlySpan<byte> nonce, long expiresAtMs) => throw new TimeoutException("redis down");
    }
}
