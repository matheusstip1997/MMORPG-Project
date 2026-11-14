namespace Shared.Data.Tables
{
    /// <summary>
    /// Player shop stalls (spec 4.5 "M4 stalls", R-4.5-01, R-4.5-03; M4 decision 7, user answers 2026-11-12 and 2026-11-13; spec 7.6
    /// rate limits; WP-M4-8). A stall stands on a free <c>stall</c> marker of a city (the cap per city is the marker count), opens
    /// with one listing for <see cref="DurationMs"/>, holds up to <see cref="ListingsPerStall"/> listings of the shared listing model
    /// (WP-M4-7's <c>ListingService</c>; each pays the listing fee), sells while its owner is offline and closes at its time, when
    /// its last listing is gone, or by its owner. One stall per character (a rule, not a number). Every number is a placeholder
    /// (balance is deferred). TODO(open) decision 14: the level that unlocks stalls (no <c>UnlockSchedule</c> row until WP-M4-11).
    /// </summary>
    [Table("StallSettings", 1)]
    [Singleton]
    public sealed class StallSettings : IValidatableRow
    {
        /// <summary>How long a stall stays open from its opening (ms); every listing on it ends at that time.</summary>
        [Range(60_000, 2_592_000_000)] public long DurationMs { get; set; }

        /// <summary>Listings one stall holds at most, counted apart from the auction's cap (user answer 2026-11-13).</summary>
        [Range(1, 32)] public int ListingsPerStall { get; set; }

        /// <summary>
        /// Most distance (cm) between a player and a stall place for every stall request (open, add, cancel, close, browse, buy); two
        /// stall places of one world stand at least twice this far apart (a world check), so one place is in reach at a time.
        /// </summary>
        [Range(50, 2_000)] public int InteractRangeCm { get; set; }

        /// <summary>Token bucket of the stall requests per character (spec 7.6).</summary>
        [Range(1, 1_000)] public int RequestsPerPeriod { get; set; }

        [Range(100, 3_600_000)] public int RequestPeriodMs { get; set; }

        [Range(1, 1_000)] public int RequestBurst { get; set; }

        /// <summary>After a failed write of a stall's close (off the tick), the next try waits this long.</summary>
        [Range(100, 600_000)] public int CloseRetryMs { get; set; }

        public void Validate(RowErrors errors)
        {
            if (RequestBurst < RequestsPerPeriod)
                errors.Add(nameof(RequestBurst), "must be at least RequestsPerPeriod");
        }

        public static StallSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(StallSettings), out var table) && table.Rows.Count == 1 ? (StallSettings)table.Rows[0] : null;
    }
}
