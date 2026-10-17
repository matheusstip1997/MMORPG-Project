using Shared.Data.Tables;

namespace GameServer.Tests.Support;

/// <summary>A valid <see cref="NetSecurity"/> row with generous defaults; tests narrow the limit they exercise.</summary>
public static class TestLimits
{
    public static NetSecurity Create(
        int maxPeers = 10,
        int attemptsPerIpPerMinute = 600,
        int attemptBurstPerIp = 100,
        int maxTrackedIps = 100,
        int requestsPerSecond = 1_000,
        int maxReliableBytes = 4_096,
        int maxUnreliableBytes = 1_200,
        int messagesPerSecond = 1_000,
        int messageBurst = 1_000) => new()
    {
        TicketLifetimeMs = 60_000,
        MaxPeers = maxPeers,
        ConnectAttemptsPerIpPerMinute = attemptsPerIpPerMinute,
        ConnectAttemptBurstPerIp = attemptBurstPerIp,
        MaxTrackedIps = maxTrackedIps,
        ConnectRequestsPerSecond = requestsPerSecond,
        MaxReliableMessageBytes = maxReliableBytes,
        MaxUnreliableMessageBytes = maxUnreliableBytes,
        MessagesPerSecondPerPeer = messagesPerSecond,
        MessageBurstPerPeer = messageBurst,
    };
}
