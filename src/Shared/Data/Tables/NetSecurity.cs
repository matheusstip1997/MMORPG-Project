namespace Shared.Data.Tables
{
    /// <summary>Network security limits (spec 2.5, 8.5). Values in data/tables/NetSecurity.json are placeholders until approved.</summary>
    [Table("NetSecurity", 2)]
    [Singleton]
    public sealed class NetSecurity : IValidatableRow
    {
        /// <summary>R-2.5-01: session tickets expire after about 60 s.</summary>
        [Range(1_000, 600_000)] public int TicketLifetimeMs { get; set; }

        /// <summary>Connected peers the game server accepts before refusing with ServerFull (R-2.5-03 total peers).</summary>
        [Range(1, 10_000)] public int MaxPeers { get; set; }

        /// <summary>R-2.5-03: connection attempts one IP (or IPv6 /64) may make per minute, refilled continuously.</summary>
        [Range(1, 600)] public int ConnectAttemptsPerIpPerMinute { get; set; }

        /// <summary>Attempts one IP may make back to back before the per-minute rate applies.</summary>
        [Range(1, 100)] public int ConnectAttemptBurstPerIp { get; set; }

        /// <summary>Distinct IPs the per-IP limiter remembers; bounds memory under a spoofed-address flood.</summary>
        [Range(100, 1_000_000)] public int MaxTrackedIps { get; set; }

        /// <summary>R-2.5-03 pending peers: connection requests the server examines per second, from all IPs.</summary>
        [Range(1, 10_000)] public int ConnectRequestsPerSecond { get; set; }

        /// <summary>R-2.5-03 packet size: largest reliable message payload a client may send, in bytes, before crypto overhead.</summary>
        [Range(256, 1_048_576)] public int MaxReliableMessageBytes { get; set; }

        /// <summary>Largest unreliable message payload a client may send, in bytes, before the input tag; it must fit one datagram.</summary>
        [Range(64, 1_400)] public int MaxUnreliableMessageBytes { get; set; }

        /// <summary>R-2.5-03: messages per second one peer may send on all channels; extra messages are dropped.</summary>
        [Range(1, 10_000)] public int MessagesPerSecondPerPeer { get; set; }

        /// <summary>Messages one peer may send back to back before the per-second rate applies.</summary>
        [Range(1, 10_000)] public int MessageBurstPerPeer { get; set; }

        public void Validate(RowErrors errors)
        {
            if (MaxUnreliableMessageBytes > MaxReliableMessageBytes)
                errors.Add(nameof(MaxUnreliableMessageBytes), "Must not be larger than MaxReliableMessageBytes.");
        }
    }
}
