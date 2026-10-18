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
        var locks = Locks(ttlMs: 1_000);
        Assert.True(await locks.TryAcquireAsync(9, "crashed:1"));
        Assert.False(await locks.RefreshAsync(9, "other:1"));

        await Task.Delay(600);
        Assert.True(await locks.RefreshAsync(9, "crashed:1")); // the TTL starts again
        await Task.Delay(600);
        Assert.False(await locks.TryAcquireAsync(9, "other:1")); // 1.2 s after the acquire, still held

        // The holder stops refreshing (it crashed): the lock expires and another server may take the character.
        await Task.Delay(1_200);
        Assert.False(await locks.RefreshAsync(9, "crashed:1"));
        Assert.True(await locks.TryAcquireAsync(9, "other:1"));
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
