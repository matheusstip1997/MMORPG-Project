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

        /// <summary>
        /// Economy snapshots (R-7.2-02, spec 7.1 <c>faucet_sink_snapshots</c>; WP-M4-5): after a demand control step the world
        /// writes a snapshot row when at least this long passed since its previous row (0: after every control step).
        /// </summary>
        [Optional] [Range(0, 86_400_000)] public int SnapshotIntervalMs { get; set; } = 60_000;

        /// <summary>
        /// Snapshot rows the background writer holds while the database is unreachable; past it the oldest is dropped and
        /// counted (WP-M4-5). It bounds memory only: a snapshot moves no value.
        /// </summary>
        [Optional] [Range(1, 10_000)] public int SnapshotQueueMax { get; set; } = 60;
    }
}
