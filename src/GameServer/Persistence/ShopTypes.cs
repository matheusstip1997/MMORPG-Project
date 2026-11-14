namespace GameServer.Persistence;

/// <summary>Why a stall closed (<c>shops.close_reason</c>). Stored in the database: append only, never renumber.</summary>
public enum ShopCloseReason : short
{
    None = 0,
    /// <summary>Its last listing sold or was cancelled (user answer 2026-11-13): the place is free again.</summary>
    SoldOut = 1,
    /// <summary>Its owner closed it; the listings left went back by mail.</summary>
    Owner = 2,
    /// <summary>Its time ran out; its listings expired with it and went back by mail.</summary>
    Time = 3,
    /// <summary>Found at world start past its time or with no listing on sale.</summary>
    Stale = 4,
}

/// <summary>
/// One open stall (<c>shops</c>, spec 4.5, WP-M4-8): the stall <see cref="ShopId"/> of <see cref="OwnerId"/> on the stall marker
/// <see cref="PlaceMarkerId"/>, open from <see cref="OpenedUtcMs"/> until <see cref="ClosesUtcMs"/>. <see cref="OwnerName"/> is
/// the owner's character name at the open (shown only through <c>DisplayIdentity</c>).
/// </summary>
public sealed record ShopRow(string ShopId, long OwnerId, string PlaceMarkerId, string OwnerName, long OpenedUtcMs, long ClosesUtcMs);

/// <summary>The stall part of a character save (WP-M4-8): the stalls this character opened, written with their first listing.</summary>
public sealed record ShopBatch(IReadOnlyList<ShopRow> Opens)
{
    public bool IsEmpty => Opens.Count == 0;
}

/// <summary>
/// Hands a character's pending stall opens to the save being issued and learns how that save ended. Implemented by
/// <c>Market/StallService</c>; called on the world thread by <see cref="SaveScheduler"/>.
/// </summary>
public interface IShopBatchSource
{
    ShopBatch? TakeBatch(long characterId, Guid saveId);

    void Committed(long characterId, Guid saveId);
}

/// <summary>Writes a stall's close on its own transaction, off the tick thread (<see cref="ShopRepository"/>; a fake in tests).</summary>
public interface IShopStore
{
    /// <summary>Closes the open stall <paramref name="shopId"/>; false (nothing written) when it is not open (closed before: idempotent).</summary>
    Task<bool> CloseAsync(string shopId, ShopCloseReason reason, long atUtcMs, CancellationToken ct);
}
