namespace GameServer.Persistence;

/// <summary>Where a listing sells (<c>listings.kind</c>). Stored in the database: append only, never renumber.</summary>
public enum ListingKind : short
{
    /// <summary>The auction house (WP-M4-7).</summary>
    Auction = 1,
    /// <summary>A shop stall in a city (WP-M4-8).</summary>
    Stall = 2,
}

/// <summary>A listing's state (<c>listings.state</c>). Stored in the database: append only, never renumber.</summary>
public enum ListingState : short
{
    Active = 1,
    /// <summary>Every unit was bought.</summary>
    SoldOut = 2,
    /// <summary>The seller cancelled; the units left went back by mail.</summary>
    Cancelled = 3,
    /// <summary>The duration ran out; the units left went back by mail.</summary>
    Expired = 4,
}

/// <summary>
/// One listing (<c>listings</c>, spec 4.5, WP-M4-7): the units left of one bag slot held in escrow <see cref="EscrowId"/> at
/// <see cref="UnitPrice"/> each. The item columns are copied from the tables at the create, so the search reads one table.
/// <see cref="JobNetId"/> is the item's job (0 none) and <see cref="ArmorTier"/> its armour tier + 1 (0 none; WP-M4-13).
/// </summary>
public sealed record ListingRow(long ListingId, string CreateOpKey, ListingKind Kind, string? StallId, long SellerId, string EscrowId,
    string DefId, int ItemNetId, short Category, short Rarity, int MinLevel, short JobNetId, short ArmorTier, long UnitPrice,
    int CountInitial, int CountLeft, long FeePaid, long CreatedUtcMs, long ExpiresUtcMs);

/// <summary>
/// A purchase carried by the buyer's save: the listing row moves from (<see cref="ExpectedEscrowId"/>,
/// <see cref="ExpectedCountLeft"/>) to (<see cref="NewEscrowId"/>, <see cref="NewCountLeft"/>), sold out at 0, guarded by the
/// values the world saw, and a <c>listing_sales</c> row records it under the buy's operation key.
/// </summary>
public sealed record ListingSaleWrite(string OpKey, long ListingId, long BuyerId, int Count, long UnitPrice, long Price, long Tax,
    bool ToMail, string ExpectedEscrowId, int ExpectedCountLeft, string NewEscrowId, int NewCountLeft, long AtUtcMs);

/// <summary>
/// The listing part of a character save (WP-M4-7): listings this character created (with their escrow hold and listing fee)
/// and purchases it made (with the escrow release and the proceeds mail), written in the same transaction.
/// </summary>
public sealed record ListingBatch(IReadOnlyList<ListingRow> Creates, IReadOnlyList<ListingSaleWrite> Sales)
{
    public bool IsEmpty => Creates.Count == 0 && Sales.Count == 0;
}

/// <summary>
/// Hands a character's pending listing writes to the save being issued and learns how that save ended. Implemented by
/// <c>Market/ListingService</c>; called on the world thread by <see cref="SaveScheduler"/>.
/// </summary>
public interface IListingBatchSource
{
    ListingBatch? TakeBatch(long characterId, Guid saveId);

    void Committed(long characterId, Guid saveId);
}

/// <summary>A purchase's guarded update found the listing changed (sold, cancelled, expired elsewhere): the save rolls back.</summary>
public sealed class ListingConflictException(long listingId) : Exception($"listing {listingId} changed under a purchase")
{
    public long ListingId { get; } = listingId;
}

/// <summary>
/// One search (spec 4.5, M4 decision 6): active auction listings not past their expiry, filtered, cheapest unit price first,
/// after the keyset cursor (<see cref="AfterUnitPrice"/>, <see cref="AfterListingId"/>), at most <see cref="Limit"/> rows. A
/// filter left empty or 0 matches everything; <see cref="ItemNetIds"/> is the name filter the client resolved.
/// </summary>
public sealed record ListingQuery(IReadOnlyList<int> ItemNetIds, short? Category, short RarityMin, short RarityMax, int LevelMin,
    int LevelMax, short JobNetId, short ArmorTier, long AfterUnitPrice, long AfterListingId, int Limit, long NowUtcMs);

/// <summary>One search result: the listing and its held unit (for the tooltip: flags and an instance's properties).</summary>
public sealed record ListingSearchRow(long ListingId, long UnitPrice, int CountLeft, long ExpiresUtcMs, string DefId, short Rarity,
    bool Stolen, long InstanceId, bool NonTradable, string? PropertiesJson);
