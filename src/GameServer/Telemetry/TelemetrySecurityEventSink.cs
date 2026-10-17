using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Net.Security;

namespace GameServer.Telemetry;

/// <summary>
/// Feeds the transport's security events into telemetry (R-2.5-06). Called on the network thread: it only enqueues
/// (never blocks) and allocates nothing.
/// </summary>
/// <remarks>
/// The address is kept as the unit the rate limiter uses: the whole IPv4 address, or the IPv6 /64 prefix
/// (<c>ipFamily</c> 4 or 6, 0 when there is none). TODO(open): whether storing IPs in telemetry is allowed, and for how
/// long, is a privacy decision the spec does not make yet.
/// </remarks>
public sealed class TelemetrySecurityEventSink(TelemetryQueue queue) : ISecurityEventSink
{
    public void Report(in SecurityEvent securityEvent)
    {
        var (family, prefix) = AddressFields(securityEvent.Address);
        queue.TryEnqueue(new TelemetryRecord(
            TelemetryKind.Security,
            securityEvent.TimestampMs,
            AccountId: securityEvent.AccountId,
            Subject: securityEvent.PeerId,
            V0: (long)securityEvent.Kind,
            V1: (long)securityEvent.RejectReason,
            V2: family,
            V3: prefix));
    }

    internal static (long Family, long Prefix) AddressFields(IPAddress? address)
    {
        if (address == null)
            return (0, 0);

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written))
            return (0, 0);

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return (4, BinaryPrimitives.ReadUInt32BigEndian(bytes[..written]));
        if (address.IsIPv4MappedToIPv6)
            return (4, BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(12, 4)));
        return (6, (long)BinaryPrimitives.ReadUInt64BigEndian(bytes));
    }
}
