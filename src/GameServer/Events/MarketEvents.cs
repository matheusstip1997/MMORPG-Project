namespace GameServer.Events;

// Listing events (spec 4.5, 8.1; M4 decision 6; WP-M4-7, stalls WP-M4-8). Published only after the change is in the database
// (rule 5: commit, then events). Kind is the listing's kind (1 auction, 2 stall; Persistence/ListingKind) and StallId its stall
// (WP-M4-8, the shops row id; null for the auction), so a stall refreshes its buyers and closes when empty.

/// <summary>A listing committed (spec 8.1 <c>ListingCreated</c>): the units are in escrow and the listing fee was destroyed.</summary>
public readonly record struct ListingCreated(long ListingId, long SellerId, short Kind, ushort ItemNetId, int Count, long UnitPrice,
    long Fee, long ExpiresUtcMs, string? StallId = null) : IGameEvent;

/// <summary>
/// A purchase committed (spec 8.1 <c>ListingSold</c>): <see cref="Count"/> units to the buyer (by mail when
/// <see cref="ToMail"/>), <see cref="Price"/> paid, <see cref="Tax"/> of it destroyed, the rest mailed to the seller.
/// </summary>
public readonly record struct ListingSold(long ListingId, long SellerId, long BuyerId, short Kind, ushort ItemNetId, int Count,
    long Price, long Tax, bool ToMail, int CountLeft, string? StallId = null) : IGameEvent;

/// <summary>A listing was cancelled by its seller (spec 8.1 <c>ListingCancelled</c>): the units left went back by mail.</summary>
public readonly record struct ListingCancelled(long ListingId, long SellerId, short Kind, ushort ItemNetId, int CountReturned,
    string? StallId = null) : IGameEvent;

/// <summary>A listing ran out (spec 8.1 <c>ListingExpired</c>): the units left went back by mail.</summary>
public readonly record struct ListingExpired(long ListingId, long SellerId, short Kind, ushort ItemNetId, int CountReturned,
    string? StallId = null) : IGameEvent;
