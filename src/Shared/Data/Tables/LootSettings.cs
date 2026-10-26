namespace Shared.Data.Tables
{
    /// <summary>
    /// Ground drops and pickup (R-4.3-01, backlog G). Placeholders in data/tables. The lock and expiry windows are
    /// <c>StartingValuesM1.partyLootLockMs</c> and <c>bagExpiryMs</c> (spec 12.7).
    /// </summary>
    [Table("LootSettings", 1)]
    [Singleton]
    public sealed class LootSettings
    {
        /// <summary>A player picks up a drop whose position is at most this far from their own (server check).</summary>
        [Range(1, 10_000)] public int PickupRangeCm { get; set; }

        /// <summary>Drops of one kill are spread on a ring of this radius around the death position (0 = all on the spot).</summary>
        [Range(0, 1_000)] public int DropScatterCm { get; set; }

        /// <summary>
        /// Pickup requests one session may have waiting for the world (handled all in the next phase 1); a request that
        /// arrives with the queue full is dropped and counted (R-2.5-03).
        /// </summary>
        [Range(1, 64)] public int MaxQueuedPickupRequests { get; set; }

        /// <summary>Most ground drops one world holds; drops over the cap are not spawned and are counted.</summary>
        [Range(1, 100_000)] public int MaxDropsPerWorld { get; set; }
    }
}
