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
    /// <summary>The class's starter weapon, equipped at the first world entry (spec 4.1 M2 item model, WP-M2-1).</summary>
    StarterGear = 4,
    /// <summary>Sell-only items sold at an outpost (R-4.2-01, class 1): the items are destroyed, gold is created (the faucet).</summary>
    OutpostSale = 5,
    /// <summary>A purchase from an NPC vendor (R-4.2-01, class 1): gold is destroyed (a sink), the item is created.</summary>
    VendorPurchase = 6,
    /// <summary>A quest item a monster dropped straight into the Quest bag for an active return step (spec 5.5, WP-M2-6).</summary>
    QuestDrop = 7,
    /// <summary>Items handed in for a deliver or return step at an outpost (R-5.5-02): destroyed, an item sink.</summary>
    QuestDelivery = 8,
    /// <summary>The starter items of a job pick (R-5.3-01 starter molds, WP-M3-1), granted once per job in the pick's save.</summary>
    JobStart = 9,
    /// <summary>A consumable used (spec 3.4 consumable use, WP-M3-3; class 2): one unit destroyed at the use action's effect point, an item sink.</summary>
    ItemUsed = 10,
    /// <summary>A resource node harvested (spec 5.2, WP-M3-5; class 2): the yield added at the harvest action's effect point, an item faucet.</summary>
    Harvest = 11,
    /// <summary>A refine recipe made a material (spec 5.3 R-5.3-06, WP-M3-6); its inputs are consumed under the same reason.</summary>
    Refine = 12,
    /// <summary>A mold craft or a mold recipe made an item (spec 5.3 R-5.3-01..03, WP-M3-6); its inputs are consumed under the same reason.</summary>
    Craft = 13,
    /// <summary>A tier conversion succeeded (R-5.2-04, WP-M3-6): the inputs consumed, one higher-tier item made.</summary>
    Convert = 14,
    /// <summary>A tier conversion failed (R-5.2-04): the inputs are consumed and nothing is made (an item sink).</summary>
    ConvertFailed = 15,
    /// <summary>An enchant attempt consumed its stones (R-5.3-04, WP-M3-8; always, whatever the outcome: an item sink) and changed the item's level in place.</summary>
    Enchant = 16,
    /// <summary>A gem socketed into an item at the jeweler's bench (R-5.3-05, WP-M3-9): the gem leaves the bag into the item.</summary>
    GemSocket = 17,
    /// <summary>A failed socket (M3 decision 11): the gem is consumed and the item untouched (an item sink).</summary>
    GemSocketFailed = 18,
    /// <summary>A gem removed by a Jeweler (WP-M3-9): the gem back in the bag, the removal cost consumed (a small sink).</summary>
    GemRemove = 19,
}

/// <summary>Items came into a character's bag (spec 8.1 <c>ItemCreated</c>).</summary>
public readonly record struct ItemCreated(long CharacterId, ushort ItemNetId, byte Rarity, int Count, InventoryReason Reason)
    : IGameEvent;

/// <summary>Items left a character's bag and the game (spec 8.1 <c>ItemDestroyed</c>).</summary>
public readonly record struct ItemDestroyed(long CharacterId, ushort ItemNetId, byte Rarity, int Count, InventoryReason Reason)
    : IGameEvent;

/// <summary>
/// What a character has equipped in <see cref="Slot"/> (<c>EquipSlotDef.code</c>) changed, as committed (spec 8.1, WP-M2-1). One per
/// slot whose committed occupant changed, published after the save commits like the other item events; the stats rebuild
/// their gear modifiers from <c>IEquipmentStatSource</c> (WP-M2-2). The actor is the character.
/// </summary>
public readonly record struct EquipmentChanged(long CharacterId, byte Slot) : IGameEvent;

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
