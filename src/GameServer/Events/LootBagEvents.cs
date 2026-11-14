using GameServer.Persistence;

namespace GameServer.Events;

// Death loot bag and stolen mark events (spec 4.3 R-4.3-03, 4.4 R-4.4-01, 8.1; WP-M4-9). Published by the world only after the
// change is in the database (rule 5: commit, then events). Item ids are ItemNetIds (ItemDef.netId); a bag is named by its id
// (the death's operation key, unique for ever) and the entity id it had on this server run.

/// <summary>One stack an event names: the item, its rarity and the units.</summary>
public readonly record struct LootBagItemCount(ushort ItemNetId, byte Rarity, int Count);

/// <summary>
/// A player's death dropped the Normal bag into a loot bag (spec 8.1 <c>LootBagCreated</c>, 8.2 the death drop): committed with
/// the units that left the character. <see cref="Value"/> is the units at <c>PriceBase</c> (0 for an item without a price).
/// </summary>
public readonly record struct LootBagCreated(string BagId, uint BagEntityId, long OwnerCharacterId, string RegionId,
    IReadOnlyList<LootBagItemCount> Items, int Units, long Value) : IGameEvent;

/// <summary>
/// A take from a loot bag committed (spec 8.1 <c>LootBagPickedUp</c>): by the owner, by a party member for the owner (the units
/// went to the owner by mail), or stolen (R-4.4-01). <see cref="Units"/> went to the taker's bag, <see cref="Mailed"/> by mail.
/// TODO(M5): honor for a take (lowered) and for a party member's collection (raised, R-6.1-03), with its caps.
/// </summary>
public readonly record struct LootBagPickedUp(string BagId, uint BagEntityId, long CharacterId, long OwnerCharacterId,
    LootBagTakeOutcome Outcome, IReadOnlyList<LootBagItemCount> Items, int Units, int Mailed) : IGameEvent
{
    public bool Stolen => Outcome == LootBagTakeOutcome.Stolen;
}

/// <summary>
/// An unclaimed bag expired and what was left was destroyed (spec 8.1 <c>LootBagExpired</c>; R-4.3-03: a sink, which
/// <c>EconomyLedger</c> counts under <see cref="InventoryReason.BagExpired"/>). Published once per bag by the world whose
/// expiry job closed it.
/// </summary>
public readonly record struct LootBagExpired(string BagId, uint BagEntityId, long OwnerCharacterId, IReadOnlyList<LootBagItemCount> Items,
    int Units, long Value) : IGameEvent;

/// <summary>
/// A non-owner's take set the stolen mark on units (spec 8.1 <c>ItemStolen</c>, R-4.4-01): who holds them, whom they were taken
/// from and when. One event per stack row of the take. Consumed by telemetry now; honor, achievements and returns are M5.
/// </summary>
public readonly record struct ItemStolen(long CharacterId, long OriginalOwnerId, ushort ItemNetId, byte Rarity, int Count, long AtUtcMs,
    string BagId) : IGameEvent;

/// <summary>
/// A death in a drop region dropped nothing because the world held <c>BagExpiry.maxBagsPerWorld</c> bags (user answer
/// 2026-11-13): the character keeps its Normal bag. Telemetry only.
/// </summary>
public readonly record struct LootBagNotDropped(long CharacterId, int UnitsKept) : IGameEvent;
