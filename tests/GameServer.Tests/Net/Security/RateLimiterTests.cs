using System.Net;
using GameServer.Net.Security;
using GameServer.Tests.Support;
using Shared.Data.Tables;

namespace GameServer.Tests.Net.Security;

/// <summary>R-2.5-03: connection attempts per IP, server-wide request budget, messages per second per peer.</summary>
public class RateLimiterTests
{
    private readonly FakeClock _clock = new();

    private static NetSecurity Limits(
        int attemptsPerIpPerMinute = 6,
        int attemptBurstPerIp = 3,
        int maxTrackedIps = 100,
        int requestsPerSecond = 1_000,
        int messagesPerSecond = 10,
        int messageBurst = 5) =>
        TestLimits.Create(
            attemptsPerIpPerMinute: attemptsPerIpPerMinute,
            attemptBurstPerIp: attemptBurstPerIp,
            maxTrackedIps: maxTrackedIps,
            requestsPerSecond: requestsPerSecond,
            messagesPerSecond: messagesPerSecond,
            messageBurst: messageBurst);

    private static IPAddress Ip(string text) => IPAddress.Parse(text);

    [Fact]
    public void Token_bucket_allows_a_burst_then_refills_at_the_rate()
    {
        var bucket = new TokenBucket();
        const long t0 = 1_000_000;

        for (var i = 0; i < 3; i++)
            Assert.True(bucket.TryTake(t0, capacity: 3, tokensPerPeriod: 2, periodMs: 1_000));
        Assert.False(bucket.TryTake(t0, 3, 2, 1_000));

        Assert.False(bucket.TryTake(t0 + 499, 3, 2, 1_000));
        Assert.True(bucket.TryTake(t0 + 500, 3, 2, 1_000));
        Assert.False(bucket.TryTake(t0 + 500, 3, 2, 1_000));
    }

    [Fact]
    public void Token_bucket_never_holds_more_than_its_capacity()
    {
        var bucket = new TokenBucket();
        Assert.True(bucket.TryTake(0, 2, 1, 1_000));

        var later = 10L * 365 * 24 * 3600 * 1000;
        Assert.True(bucket.TryTake(later, 2, 1, 1_000));
        Assert.True(bucket.TryTake(later, 2, 1, 1_000));
        Assert.False(bucket.TryTake(later, 2, 1, 1_000));
    }

    [Fact]
    public void Token_bucket_refills_nothing_when_the_clock_steps_back()
    {
        var bucket = new TokenBucket();
        Assert.True(bucket.TryTake(10_000, 1, 1, 1_000));

        Assert.False(bucket.TryTake(5_000, 1, 1, 1_000));
        Assert.False(bucket.TryTake(10_999, 1, 1, 1_000));
        Assert.True(bucket.TryTake(11_000, 1, 1, 1_000));
    }

    [Fact]
    public void One_ip_gets_its_burst_then_is_limited_until_tokens_refill()
    {
        var limiter = new RateLimiter(Limits(attemptsPerIpPerMinute: 6, attemptBurstPerIp: 3));
        var ip = Ip("203.0.113.7");

        for (var i = 0; i < 3; i++)
            Assert.Equal(ConnectAttemptResult.Allowed, limiter.TryConnectAttempt(ip, _clock.UtcNowMs));
        Assert.Equal(ConnectAttemptResult.IpLimited, limiter.TryConnectAttempt(ip, _clock.UtcNowMs));

        _clock.Advance(10_000); // 6 per minute: one token every 10 s
        Assert.Equal(ConnectAttemptResult.Allowed, limiter.TryConnectAttempt(ip, _clock.UtcNowMs));
        Assert.Equal(ConnectAttemptResult.IpLimited, limiter.TryConnectAttempt(ip, _clock.UtcNowMs));
    }

    [Fact]
    public void A_limited_ip_does_not_affect_another_ip()
    {
        var limiter = new RateLimiter(Limits(attemptBurstPerIp: 1));

        Assert.Equal(ConnectAttemptResult.Allowed, limiter.TryConnectAttempt(Ip("203.0.113.7"), _clock.UtcNowMs));
        Assert.Equal(ConnectAttemptResult.IpLimited, limiter.TryConnectAttempt(Ip("203.0.113.7"), _clock.UtcNowMs));
        Assert.Equal(ConnectAttemptResult.Allowed, limiter.TryConnectAttempt(Ip("203.0.113.8"), _clock.UtcNowMs));
    }

    [Fact]
    public void Ipv4_mapped_ipv6_counts_as_the_same_ipv4_address()
    {
        var limiter = new RateLimiter(Limits(attemptBurstPerIp: 1));
        var ip = Ip("203.0.113.7");

        Assert.Equal(ConnectAttemptResult.Allowed, limiter.TryConnectAttempt(ip, _clock.UtcNowMs));
        Assert.Equal(ConnectAttemptResult.IpLimited, limiter.TryConnectAttempt(ip.MapToIPv6(), _clock.UtcNowMs));
    }

    [Fact]
    public void Ipv6_addresses_in_one_slash_64_share_a_limit()
    {
        var limiter = new RateLimiter(Limits(attemptBurstPerIp: 1));

        Assert.Equal(ConnectAttemptResult.Allowed, limiter.TryConnectAttempt(Ip("2001:db8:1:2::1"), _clock.UtcNowMs));
        Assert.Equal(ConnectAttemptResult.IpLimited, limiter.TryConnectAttempt(Ip("2001:db8:1:2:ffff::9"), _clock.UtcNowMs));
        Assert.Equal(ConnectAttemptResult.Allowed, limiter.TryConnectAttempt(Ip("2001:db8:1:3::1"), _clock.UtcNowMs));
    }

    [Fact]
    public void Server_wide_budget_limits_requests_from_many_ips()
    {
        var limiter = new RateLimiter(Limits(requestsPerSecond: 3));

        for (var i = 1; i <= 3; i++)
            Assert.Equal(ConnectAttemptResult.Allowed, limiter.TryConnectAttempt(Ip($"198.51.100.{i}"), _clock.UtcNowMs));
        Assert.Equal(ConnectAttemptResult.GlobalLimited, limiter.TryConnectAttempt(Ip("198.51.100.4"), _clock.UtcNowMs));

        _clock.Advance(1_000);
        Assert.Equal(ConnectAttemptResult.Allowed, limiter.TryConnectAttempt(Ip("198.51.100.5"), _clock.UtcNowMs));
    }

    [Fact]
    public void Tracked_ips_are_capped_and_idle_ones_are_forgotten()
    {
        var limiter = new RateLimiter(Limits(attemptsPerIpPerMinute: 60, attemptBurstPerIp: 1, maxTrackedIps: 100));
        for (var i = 0; i < 100; i++)
            limiter.TryConnectAttempt(Ip($"10.0.{i / 256}.{i % 256}"), _clock.UtcNowMs);

        Assert.Equal(ConnectAttemptResult.TooManyTrackedIps, limiter.TryConnectAttempt(Ip("10.1.0.1"), _clock.UtcNowMs));
        Assert.Equal(100, limiter.TrackedIps);

        _clock.Advance(1_000); // 60 per minute with a burst of 1: every bucket is full again after 1 s
        Assert.Equal(ConnectAttemptResult.Allowed, limiter.TryConnectAttempt(Ip("10.1.0.1"), _clock.UtcNowMs));
        Assert.Equal(1, limiter.TrackedIps);
    }

    [Fact]
    public void Peer_messages_get_a_burst_then_the_per_second_rate()
    {
        var limiter = new RateLimiter(Limits(messagesPerSecond: 10, messageBurst: 5));
        var bucket = new TokenBucket();

        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryMessage(ref bucket, _clock.UtcNowMs));
        Assert.False(limiter.TryMessage(ref bucket, _clock.UtcNowMs));

        _clock.Advance(100);
        Assert.True(limiter.TryMessage(ref bucket, _clock.UtcNowMs));
        Assert.False(limiter.TryMessage(ref bucket, _clock.UtcNowMs));
    }
}
