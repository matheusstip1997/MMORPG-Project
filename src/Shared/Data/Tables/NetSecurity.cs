namespace Shared.Data.Tables
{
    /// <summary>Network security limits (spec 2.5, 8.5). More limits arrive with rate limiting (R-2.5-03).</summary>
    [Table("NetSecurity", 1)]
    [Singleton]
    public sealed class NetSecurity
    {
        /// <summary>R-2.5-01: session tickets expire after about 60 s.</summary>
        [Range(1_000, 600_000)] public int TicketLifetimeMs { get; set; }

        /// <summary>Connected peers the game server accepts before refusing with ServerFull. Placeholder.</summary>
        [Range(1, 10_000)] public int MaxPeers { get; set; }
    }
}
