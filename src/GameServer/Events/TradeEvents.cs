namespace GameServer.Events;

// Trade and escrow events (spec 4.5, 8.1; WP-M4-4). Published only after the change is in the database (rule 5: commit, then
// events), except TradeCancelled, which changes nothing persistent.

/// <summary>A unit or gold amount moved by a trade: the item's netId, rarity, count and instance id (0 for a stack).</summary>
public readonly record struct TradedItem(ushort ItemNetId, byte Rarity, int Count, long InstanceId);

/// <summary>
/// A trade committed (spec 8.1 <c>TradeCompleted</c>): both characters' bags and gold changed in one transaction.
/// <see cref="CharacterA"/> invited.
/// </summary>
public readonly record struct TradeCompleted(long CharacterA, long CharacterB, IReadOnlyList<TradedItem> ItemsAtoB,
    IReadOnlyList<TradedItem> ItemsBtoA, long GoldAtoB, long GoldBtoA, string OpKey) : IGameEvent;

/// <summary>Why an open trade ended without a swap. Stored in telemetry: append only.</summary>
public enum TradeEndReason : byte
{
    Cancelled = 0,
    Declined = 1,
    TooFar = 2,
    Dead = 3,
    InCombat = 4,
    Disconnected = 5,
    Timeout = 6,
}

/// <summary>An open trade (or invite) ended without a swap (spec 8.1 <c>TradeCancelled</c>); nothing moved.</summary>
public readonly record struct TradeCancelled(long CharacterA, long CharacterB, TradeEndReason Reason) : IGameEvent;

/// <summary>An escrow hold committed (WP-M4-4): the items and gold left the owner into escrow (one transaction).</summary>
public readonly record struct EscrowHeld(string EscrowId, long OwnerCharacterId, short HolderKind, string HolderId, long Gold,
    int Units) : IGameEvent;

/// <summary>An escrow release committed (WP-M4-4): to a character (<see cref="ToMail"/> false) or a mail.</summary>
public readonly record struct EscrowReleased(string EscrowId, long OwnerCharacterId, bool ToMail, long ToCharacterId) : IGameEvent;
