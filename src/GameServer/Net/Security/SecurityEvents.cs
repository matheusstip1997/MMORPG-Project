using System.Net;
using Shared.Protocol;

namespace GameServer.Net.Security;

public enum SecurityEventKind : byte
{
    /// <summary>Connection request refused by version, capacity, or ticket checks (<see cref="SecurityEvent.RejectReason"/>).</summary>
    ConnectRejected,
    /// <summary>Connection request refused by the per-IP or global attempt limit.</summary>
    ConnectRateLimited,
    /// <summary>
    /// A peer sent more messages per second than allowed. An unreliable message is dropped; for a reliable one the peer
    /// is disconnected, since LiteNetLib already acknowledged it.
    /// </summary>
    MessageRateLimited,
    /// <summary>A peer sent a message above the size cap; the peer was disconnected.</summary>
    MessageTooLarge,
    /// <summary>A reliable message did not decrypt or was malformed; the peer was disconnected.</summary>
    ReliableDecryptFailed,
    /// <summary>A reliable message reused a sequence number; the peer was disconnected.</summary>
    ReliableReplayed,
    /// <summary>A client input had a wrong or missing integrity tag; the packet was dropped.</summary>
    InputTagFailed,
    /// <summary>A client input reused a sequence number; the packet was dropped.</summary>
    InputReplayed,
}

/// <summary>
/// One security fact for telemetry and abuse scoring (R-2.5-06). <see cref="PeerId"/> is -1 and
/// <see cref="AccountId"/> 0 before a peer exists.
/// </summary>
public readonly record struct SecurityEvent(
    SecurityEventKind Kind,
    long TimestampMs,
    IPAddress? Address,
    int PeerId = -1,
    long AccountId = 0,
    ConnectRejectReason RejectReason = ConnectRejectReason.None);

/// <summary>
/// Receives security events from the transport, on the network thread. Implementations must not block or do I/O
/// in <see cref="Report"/> (architecture rule 10): enqueue and return.
/// </summary>
public interface ISecurityEventSink
{
    void Report(in SecurityEvent securityEvent);
}

/// <summary>Drops every event, until the telemetry pipeline exists.</summary>
public sealed class NullSecurityEventSink : ISecurityEventSink
{
    public static readonly NullSecurityEventSink Instance = new();

    public void Report(in SecurityEvent securityEvent)
    {
    }
}
