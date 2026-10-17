using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Shared.Data.Tables;

namespace GameServer.Net.Security;

public enum ConnectAttemptResult : byte
{
    Allowed,
    /// <summary>This IP (or IPv6 /64) used up its attempts.</summary>
    IpLimited,
    /// <summary>The server-wide budget for connection requests is used up.</summary>
    GlobalLimited,
    /// <summary>Too many distinct IPs are being tracked; new ones are refused until old ones expire.</summary>
    TooManyTrackedIps,
}

/// <summary>
/// Network rate limits (R-2.5-03), all from the <see cref="NetSecurity"/> table and checked before any crypto work:
/// connection attempts per IP, a server-wide budget for connection requests, and messages per second per peer.
/// The server accepts or refuses each request synchronously, so the server-wide request budget is what bounds
/// pending connections. IPv6 addresses are limited per /64, since one host usually owns a whole /64.
/// Not thread safe: the transport calls it from the network thread only.
/// </summary>
public sealed class RateLimiter(NetSecurity limits)
{
    private const int MinuteMs = 60_000;
    private const int SecondMs = 1_000;

    private readonly Dictionary<IpKey, TokenBucket> _perIp = new();
    private TokenBucket _global;
    private long _lastPruneMs = long.MinValue;

    public int TrackedIps => _perIp.Count;

    public ConnectAttemptResult TryConnectAttempt(IPAddress address, long nowMs)
    {
        var key = IpKey.From(address);
        if (!_perIp.TryGetValue(key, out var bucket))
        {
            if (_perIp.Count >= limits.MaxTrackedIps && !TryPrune(nowMs))
                return ConnectAttemptResult.TooManyTrackedIps;
            bucket = default;
        }

        var allowed = bucket.TryTake(nowMs, limits.ConnectAttemptBurstPerIp, limits.ConnectAttemptsPerIpPerMinute, MinuteMs);
        _perIp[key] = bucket;
        if (!allowed)
            return ConnectAttemptResult.IpLimited;

        // Checked after the per-IP limit so a single flooding IP is reported as such and cannot drain the global budget.
        if (!_global.TryTake(nowMs, limits.ConnectRequestsPerSecond, limits.ConnectRequestsPerSecond, SecondMs))
            return ConnectAttemptResult.GlobalLimited;

        return ConnectAttemptResult.Allowed;
    }

    /// <summary>Takes one message from a peer's bucket. The bucket lives with the peer and is dropped with it.</summary>
    public bool TryMessage(ref TokenBucket peerBucket, long nowMs) =>
        peerBucket.TryTake(nowMs, limits.MessageBurstPerPeer, limits.MessagesPerSecondPerPeer, SecondMs);

    /// <summary>
    /// Forgets IPs whose bucket refilled completely (forgetting them changes nothing). Runs at most once per time it
    /// takes one per-IP token to refill, so a flood of new addresses cannot make every request scan the whole table.
    /// </summary>
    private bool TryPrune(long nowMs)
    {
        var interval = MinuteMs / limits.ConnectAttemptsPerIpPerMinute;
        if (_lastPruneMs != long.MinValue && nowMs - _lastPruneMs < interval)
            return false;
        _lastPruneMs = nowMs;

        foreach (var (key, bucket) in _perIp)
        {
            var copy = bucket;
            if (copy.IsFull(nowMs, limits.ConnectAttemptBurstPerIp, limits.ConnectAttemptsPerIpPerMinute, MinuteMs))
                _perIp.Remove(key);
        }
        return _perIp.Count < limits.MaxTrackedIps;
    }

    private readonly record struct IpKey(bool IsV6, ulong Value)
    {
        public static IpKey From(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            Span<byte> bytes = stackalloc byte[16];
            address.TryWriteBytes(bytes, out var written);
            return address.AddressFamily == AddressFamily.InterNetworkV6
                ? new IpKey(true, BinaryPrimitives.ReadUInt64BigEndian(bytes))
                : new IpKey(false, BinaryPrimitives.ReadUInt32BigEndian(bytes[..written]));
        }
    }
}
