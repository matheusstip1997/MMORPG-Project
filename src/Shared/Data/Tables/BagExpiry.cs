using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Death loot bags (spec 4.3 R-4.3-03, 8.5; M4 decision 8; WP-M4-9): when an unclaimed bag expires (its items are destroyed,
    /// a sink counted under <c>BagExpired</c>), the world's cap on open bags (at the cap a death drops nothing and the character
    /// keeps the Normal bag; user answer 2026-11-13), the expiry job, and how many own bags a player is sent. Ground drops keep
    /// <c>StartingValuesM1.bagExpiryMs</c>. Every number is a placeholder (balance is deferred).
    /// </summary>
    [Table("BagExpiry", 1)]
    [Singleton]
    public sealed class BagExpiry : IValidatableRow
    {
        /// <summary>Milliseconds after the death when the bag expires and what is left is destroyed.</summary>
        [Range(1_000, 86_400_000)] public int ExpiryMs { get; set; }

        /// <summary>Open bags a world holds at most; a death at the cap drops nothing (counted in telemetry).</summary>
        [Range(1, 100_000)] public int MaxBagsPerWorld { get; set; }

        /// <summary>How often the world looks for bags past their expiry (each one is closed off the tick).</summary>
        [Range(50, 600_000)] public int ExpiryCheckIntervalMs { get; set; }

        /// <summary>Wait before an expiry whose outcome was unknown (the database failed) is tried again.</summary>
        [Range(100, 600_000)] public int ExpiryRetryMs { get; set; }

        /// <summary>Own bags a player is sent (<c>OwnLootBags</c>, newest first) for the minimap and the run back.</summary>
        [Range(1, ReliableMessages.MaxOwnLootBags)] public int OwnBagsListedMax { get; set; }

        /// <summary>A session's loot bag requests waiting for the next tick (bounds a flooding client).</summary>
        [Range(1, 64)] public int MaxQueuedRequests { get; set; }

        public void Validate(RowErrors errors)
        {
        }
    }
}
