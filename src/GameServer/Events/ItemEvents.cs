namespace GameServer.Events;

// Item and loot events (spec 4.1, 4.3, 8.1). Item events are published by InventoryService only after the change is in
// the database (rule 5: commit, then events); the actor is the character. Loot events are published by LootService on
// the world thread. Item ids are ItemDef.netId, rarities RarityDef.tier.

/// <summary>Why an inventory operation happened. Stored in the database (<c>inventory_ops.reason</c> by name) and telemetry: append only.</summary>
public enum InventoryReason : byte
{
    None = 0,
    /// <summary>A ground drop picked up (R-4.3-01, backlog G).</summary>
    LootPickup = 1,
    /// <summary>A player rearranged their own bag (class 2 "moves between own bags").</summary>
    PlayerMove = 2,
    /// <summary>Admin or test tooling (spec 7.4 restore). TODO(M2): the admin panel operations.</summary>
    Admin = 3,
}

/// <summary>Items came into a character's bag (spec 8.1 <c>ItemCreated</c>).</summary>
public readonly record struct ItemCreated(long CharacterId, ushort ItemNetId, byte Rarity, int Count, InventoryReason Reason)
    : IGameEvent;

/// <summary>Items left a character's bag and the game (spec 8.1 <c>ItemDestroyed</c>).</summary>
public readonly record struct ItemDestroyed(long CharacterId, ushort ItemNetId, byte Rarity, int Count, InventoryReason Reason)
    : IGameEvent;

/// <summary>
/// A monster's loot landed on the ground as a drop entity (spec 8.1 <c>LootDropped</c>). <see cref="KillEventId"/> is the
/// <c>Killed</c> event it came from; <see cref="OwnerCharacterId"/> may pick it up first (R-4.3-02 lock, M1: the killer).
/// </summary>
public readonly record struct LootDropped(uint DropId, long KillEventId, int MonsterDefId, ushort ItemNetId, byte Rarity,
    int Count, long OwnerCharacterId) : IGameEvent;

/// <summary>A character took units of a ground drop (the items reach the bag in the next class 2 batch). The actor is the character.</summary>
public readonly record struct LootPickedUp(uint DropId, long CharacterId, ushort ItemNetId, int Count, int CountLeft) : IGameEvent;

/// <summary>Nobody took a ground drop within <c>bagExpiryMs</c>; it is gone (backlog G).</summary>
public readonly record struct LootDropExpired(uint DropId, ushort ItemNetId, int Count) : IGameEvent;
