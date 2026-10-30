namespace GameServer.Events;

// Outpost and economy events (spec 4.2, 8.1; WP-M2-4). Gold events and ItemSold are published by InventoryService only
// after the change is in the database (rule 5: commit, then events); the actor is the character. Demand events are
// published by DemandService on each control step. Item ids are ItemDef.netId, NPC ids NpcDef.netId, monster ids
// MonsterDef.netId.

/// <summary>
/// A character's first interaction with an outpost NPC in a visit (no interaction with that NPC for
/// <c>EconomySettings.visitGapMs</c> before it): an achievement counter of WP-M2-3 ("outposts visited", R-3.6-04).
/// </summary>
public readonly record struct OutpostVisited(long CharacterId, uint NpcEntityId, int NpcDefId) : IGameEvent;

/// <summary>Sell-only items were sold at an outpost for <see cref="Gold"/> gold (spec 8.1 <c>ItemSold</c>, R-4.2-01).</summary>
public readonly record struct ItemSold(long CharacterId, ushort ItemNetId, byte Rarity, int Count, long Gold, int NpcDefId)
    : IGameEvent;

/// <summary>A character's gold changed by <see cref="Delta"/> to <see cref="Balance"/> (spec 8.1 <c>GoldChanged</c>).</summary>
public readonly record struct GoldChanged(long CharacterId, long Delta, long Balance, InventoryReason Reason) : IGameEvent;

/// <summary>Gold left the game (spec 8.1 <c>GoldDestroyed</c>, R-4.2-04 sinks; the SinkLedger counts it by reason).</summary>
public readonly record struct GoldDestroyed(long CharacterId, long Amount, InventoryReason Reason) : IGameEvent;

/// <summary>What a demand counter counts. Stored in <c>demand_state.key_kind</c> and telemetry: append only.</summary>
public enum DemandKeyKind : byte
{
    /// <summary>Kills of one monster type in one region (R-4.2-03).</summary>
    Kills = 0,
    /// <summary>Units of one item sold at outposts (R-4.2-02).</summary>
    Sales = 1,
    /// <summary>Harvests of one resource node type in one region (R-5.2-03, WP-M3-4).</summary>
    Harvests = 2,
}

/// <summary>A kill counter after a demand control step (spec 8.1 <c>DemandUpdated</c>, R-4.2-03).</summary>
public readonly record struct DemandUpdated(int MonsterNetId, byte RegionIndex, long DecayedMilli, long RatioPermille,
    int SpawnDelayPercent, int RewardPercent) : IGameEvent;

/// <summary>
/// A harvest counter after a demand control step (spec 8.1 <c>NodeDemandUpdated</c>, R-5.2-03; WP-M3-4): the node type's
/// (<c>NodeDef.netId</c>) decayed harvests in the region and its respawn delay scale.
/// </summary>
public readonly record struct NodeDemandUpdated(int NodeNetId, byte RegionIndex, long DecayedMilli, long RatioPermille,
    int SpawnDelayPercent) : IGameEvent;

/// <summary>An item's dynamic sell multiplier after a demand control step (spec 8.1 <c>PriceChanged</c>, R-4.2-02).</summary>
public readonly record struct PriceChanged(int ItemNetId, long DecayedMilli, int PricePercent) : IGameEvent;
