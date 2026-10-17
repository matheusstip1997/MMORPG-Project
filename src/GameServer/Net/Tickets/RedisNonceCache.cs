using StackExchange.Redis;
using Shared.Util;

namespace GameServer.Net.Tickets;

/// <summary>
/// Ticket nonces in Redis (spec 2.5 implementation), so a ticket works once across every game server of the realm
/// (R-2.5-01). Each used nonce is a key <c>{prefix}nonce:{hex}</c> that expires with its ticket.
/// <para>
/// Synchronous: LiteNetLib approves connection requests inside its poll callback, so this runs on the network thread
/// (never the tick thread). One round trip per accepted ticket; connection attempts are already rate limited per
/// address (R-2.5-03). TODO(B4): defer approval off the network thread if login bursts show up in the tick stats.
/// If Redis cannot be reached the call throws and <see cref="TicketValidator"/> refuses the ticket (fail closed).
/// </para>
/// </summary>
public sealed class RedisNonceCache(IConnectionMultiplexer redis, string keyPrefix, IClock clock) : INonceCache
{
    public bool TryUse(ReadOnlySpan<byte> nonce, long expiresAtMs)
    {
        // At least 1 ms: the validator already refused expired tickets, but the clock may have moved since.
        var ttl = TimeSpan.FromMilliseconds(Math.Max(1, expiresAtMs - clock.UtcNowMs));
        return redis.GetDatabase().StringSet($"{keyPrefix}nonce:{Convert.ToHexString(nonce)}", 1, ttl, When.NotExists);
    }
}
