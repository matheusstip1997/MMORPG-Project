namespace Shared.Data.Tables
{
    /// <summary>Gold and outpost limits (spec 4.2, WP-M2-4). Placeholders in data.</summary>
    [Table("EconomySettings", 1)]
    [Singleton]
    public sealed class EconomySettings
    {
        /// <summary>Most gold one character holds; a sale that would pass it is refused (GoldCapReached, rule 2).</summary>
        [Range(1, 9_000_000_000_000_000)] public long MaxGold { get; set; }

        /// <summary>
        /// Outpost and vendor requests (interact, quote, sell, buy) one session may have waiting for the world; a request
        /// that arrives with the queue full is dropped and counted (R-2.5-03).
        /// </summary>
        [Range(1, 64)] public int MaxQueuedRequests { get; set; }

        /// <summary>
        /// A visit to an outpost NPC: an interaction after this long without one with the same NPC starts a new visit
        /// (event OutpostVisited, an achievement counter of WP-M2-3).
        /// </summary>
        [Range(0, 86_400_000)] public int VisitGapMs { get; set; }
    }
}
