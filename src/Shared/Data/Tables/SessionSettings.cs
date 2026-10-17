namespace Shared.Data.Tables
{
    /// <summary>
    /// Session and ownership-lock timing (spec 2.1 implementation, backlog B4). Values in data/tables/SessionSettings.json
    /// are placeholders to tune during Milestone 1. Linger windows are in <see cref="StartingValuesM1"/>.
    /// </summary>
    [Table("SessionSettings", 1)]
    [Singleton]
    public sealed class SessionSettings : IValidatableRow
    {
        /// <summary>
        /// Time to live of the Redis ownership lock <c>char:{id}</c> (R-2.1-06). A server that crashes holds its characters
        /// at most this long; a server that cannot refresh for this long gives them up.
        /// </summary>
        [Range(1_000, 600_000)] public int OwnershipLockTtlMs { get; set; }

        /// <summary>How often the world refreshes the TTL of every lock it holds.</summary>
        [Range(100, 300_000)] public int HeartbeatIntervalMs { get; set; }

        /// <summary>Graceful shutdown waits at most this long for the final saves before releasing locks (R-2.1-09).</summary>
        [Range(100, 600_000)] public int ShutdownSaveTimeoutMs { get; set; }

        public void Validate(RowErrors errors)
        {
            // One late or failed refresh must not lose a lock that is still in use.
            if (HeartbeatIntervalMs * 2L > OwnershipLockTtlMs)
                errors.Add(nameof(HeartbeatIntervalMs), "must be at most half of OwnershipLockTtlMs");
        }
    }
}
