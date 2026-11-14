namespace GameServer.Events;

// Stall events (spec 4.5, 8.1; M4 decision 7; WP-M4-8). Published only after the change is in the database or decided on the
// world thread (rule 5): StallOpened after the owner's save with the stall's row and its first listing committed; StallClosed
// when the stall left the world (its close is written off the tick right after, idempotent). The listings of a stall publish
// the listing events of WP-M4-7 with their StallId.

/// <summary>A stall opened (spec 8.1 <c>StallOpened</c>) on the stall place <see cref="Place"/> (its ordinal; the marker <see cref="PlaceMarkerId"/>) until <see cref="ClosesUtcMs"/>.</summary>
public readonly record struct StallOpened(string ShopId, long OwnerId, int Place, string PlaceMarkerId, long ClosesUtcMs) : IGameEvent;

/// <summary>
/// A stall closed (spec 8.1 <c>StallClosed</c>): <see cref="Reason"/> is the <c>ShopCloseReason</c> (1 sold out, 2 by its owner,
/// 3 its time, 4 stale at world start); its listings left go back by mail through the listing model.
/// </summary>
public readonly record struct StallClosed(string ShopId, long OwnerId, int Place, string PlaceMarkerId, short Reason) : IGameEvent;
