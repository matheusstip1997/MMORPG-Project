using System;
using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Listings and the auction house (spec 4.5 "M4 listings", M4 decision 6, user answers 2026-11-12 and 2026-11-13; spec 7.6
    /// rate limits; WP-M4-7). Buyout only (bids are TODO(open)); a listing holds one bag slot's units in escrow for one of
    /// <see cref="DurationsMs"/>; a buyer takes any count of it; the auctioneer must be in reach for every request. Every
    /// number is a placeholder (balance is deferred). TODO(open) decision 14: the level that unlocks the auction (no
    /// <c>UnlockSchedule</c> row until WP-M4-11).
    /// </summary>
    [Table("ListingSettings", 1)]
    [Singleton]
    public sealed class ListingSettings : IValidatableRow, ISetValidatableRow
    {
        /// <summary>The durations a seller may choose (ms), shortest first; the client sends the index.</summary>
        public int[] DurationsMs { get; set; } = Array.Empty<int>();

        /// <summary>Active auction listings one character may have (<c>ListingCap</c>); stalls count their own (WP-M4-8).</summary>
        [Range(1, ReliableMessages.MaxMyListings)] public int MaxActivePerCharacter { get; set; }

        /// <summary>Highest unit price (<c>ListingPriceInvalid</c>); the total is also capped by <c>EconomySettings.maxGold</c>.</summary>
        [Range(1, 9_000_000_000_000_000)] public long MaxUnitPrice { get; set; }

        /// <summary>Rows of one search page, cheapest first.</summary>
        [Range(1, ReliableMessages.MaxListingRows)] public int PageSize { get; set; }

        /// <summary>Item net ids one search may name (the client turns the typed name into the items whose names match).</summary>
        [Range(1, ReliableMessages.MaxListingItemFilter)] public int SearchItemIdsMax { get; set; }

        /// <summary>Token bucket of the search requests per character (spec 7.6; the search runs SQL off the tick).</summary>
        [Range(1, 1_000)] public int SearchesPerPeriod { get; set; }

        [Range(100, 3_600_000)] public int SearchPeriodMs { get; set; }

        [Range(1, 1_000)] public int SearchBurst { get; set; }

        /// <summary>Token bucket of the create, buy, cancel and my-listings requests per character (spec 7.6).</summary>
        [Range(1, 1_000)] public int WritesPerPeriod { get; set; }

        [Range(100, 3_600_000)] public int WritePeriodMs { get; set; }

        [Range(1, 1_000)] public int WriteBurst { get; set; }

        /// <summary>A session's listing requests waiting for the next tick (bounds a flooding client; the buckets refuse earlier).</summary>
        [Range(1, 256)] public int MaxQueuedRequests { get; set; }

        /// <summary>Listings the expiry job starts closing per tick at most (the rest wait for the next tick).</summary>
        [Range(1, 10_000)] public int ExpiryBatch { get; set; }

        /// <summary>Owner history entries a bought instance keeps (spec 4.1 <c>ownerHistory</c>; the oldest drop first).</summary>
        [Range(1, 64)] public int OwnerHistoryMax { get; set; }

        /// <summary>The duration at <paramref name="index"/>, or 0 when there is none.</summary>
        public int DurationAt(int index) => index >= 0 && index < DurationsMs.Length ? DurationsMs[index] : 0;

        public void Validate(RowErrors errors)
        {
            if (DurationsMs.Length == 0 || DurationsMs.Length > ReliableMessages.MaxListingDurations)
                errors.Add(nameof(DurationsMs), $"needs 1 to {ReliableMessages.MaxListingDurations} durations");
            for (int i = 0; i < DurationsMs.Length; i++)
            {
                if (DurationsMs[i] < 60_000)
                    errors.Add(nameof(DurationsMs), $"{DurationsMs[i]} is shorter than a minute");
                else if (i > 0 && DurationsMs[i] <= DurationsMs[i - 1])
                    errors.Add(nameof(DurationsMs), "must be ascending, each once");
            }
            if (SearchBurst < SearchesPerPeriod)
                errors.Add(nameof(SearchBurst), "must be at least SearchesPerPeriod");
            if (WriteBurst < WritesPerPeriod)
                errors.Add(nameof(WriteBurst), "must be at least WritesPerPeriod");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (set.TryGet(typeof(EconomySettings), out var table) && table.Rows.Count == 1
                && table.Rows[0] is EconomySettings economy && MaxUnitPrice > economy.MaxGold)
                errors.Add(nameof(MaxUnitPrice), $"{MaxUnitPrice} is above EconomySettings.maxGold {economy.MaxGold}");
        }

        public static ListingSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(ListingSettings), out var table) && table.Rows.Count == 1 ? (ListingSettings)table.Rows[0] : null;
    }
}
