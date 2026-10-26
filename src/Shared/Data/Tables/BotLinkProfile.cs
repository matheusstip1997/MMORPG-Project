namespace Shared.Data.Tables
{
    /// <summary>
    /// A simulated network link for bots and test clients (backlog J, spec 11.5): the BotClient's UDP relay delays and drops
    /// datagrams below LiteNetLib, so LiteNetLib's own round trip (which caps the view lag a client may claim, R-3.1-03)
    /// sees the delay. Latency in the spec means round trip (user decision 2026-10-26): "100 ms" is 50 ms each way.
    /// Tools only; not a game rule.
    /// </summary>
    [Table("BotLinkProfile", 1)]
    public sealed class BotLinkProfile
    {
        /// <summary>For example <c>rtt100</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>One-way delay from the client to the server.</summary>
        [Range(0, 2_000)] public int UpDelayMs { get; set; }

        /// <summary>One-way delay from the server to the client.</summary>
        [Range(0, 2_000)] public int DownDelayMs { get; set; }

        /// <summary>Each datagram's delay varies uniformly by up to this much either way (never below 0).</summary>
        [Range(0, 500)] public int JitterMs { get; set; }

        /// <summary>Chance that a datagram is dropped, per direction.</summary>
        [Range(0, 100)] public int LossPercent { get; set; }

        /// <summary>False keeps each direction in order (jitter never lets a datagram overtake an earlier one).</summary>
        public bool AllowReorder { get; set; }
    }
}
