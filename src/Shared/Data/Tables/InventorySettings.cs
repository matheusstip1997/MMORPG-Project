namespace Shared.Data.Tables
{
    /// <summary>Server limits of the bag move requests (backlog G, WP16 inventory window). Placeholders in data/tables.</summary>
    [Table("InventorySettings", 1)]
    [Singleton]
    public sealed class InventorySettings
    {
        /// <summary>
        /// Move requests one session may have waiting for the world (all handled in the next phase 1), on top of
        /// <see cref="LootSettings.MaxQueuedPickupRequests"/>; a request that arrives with the queue full is dropped and
        /// counted (R-2.5-03). The client's preview of a dropped move times out.
        /// </summary>
        [Range(1, 64)] public int MaxQueuedMoveRequests { get; set; }
    }
}
