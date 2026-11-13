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
    /// <summary>
    /// A fee's destroyed share (spec 4.2 R-4.2-04/05, WP-M4-1): gold removed by <c>FeeService.Apply</c> inside the operation
    /// that owes it (a listing, a sale, a contract), a gold sink.
    /// </summary>
    Fee = 20,
    /// <summary>Attachments of a system mail claimed into the bag (spec 4.5 M4 mail, WP-M4-3; class 1): a move, not a faucet (an admin compensation claims under <see cref="Admin"/>).</summary>
    MailClaim = 21,
    /// <summary>An open mail expired and its attachments were destroyed (spec 4.5, WP-M4-3): a sink counted from <c>MailExpired</c>, never an inventory operation.</summary>
    MailExpired = 22,
    /// <summary>
    /// Items and gold moved between two characters by a trade (spec 4.5, WP-M4-4): never created or destroyed (gold flow
    /// <c>move</c>, audit kinds <c>TransferOut</c>/<c>TransferIn</c>).
    /// </summary>
    Trade = 23,
    /// <summary>Items and gold moved from a character into escrow (spec 4.5, 5.4, WP-M4-4): a move, not a sink.</summary>
    EscrowHold = 24,
    /// <summary>Items and gold moved out of escrow to a character (WP-M4-4): a move, not a faucet.</summary>
    EscrowRelease = 25,
    /// <summary>
    /// Worn armor of the old tier taken off when the character's tier changed (R-3.5-08, M4 decision 9b; WP-M4-13): into the
    /// bag (audit <c>Unequip</c>), or out to the character's own mail when the bag is full (audit <c>TransferOut</c>, a move).
    /// </summary>
    ArmorTierChange = 26,
    /// <summary>
    /// A death moved the Normal bag's units into a loot bag at the death spot (spec 4.3 R-4.3-03, WP-M4-9; class 1, audit kind
    /// <c>TransferOut</c>): a move, not a sink. Nothing is destroyed until the bag expires.
    /// </summary>
    DeathDrop = 28,
    /// <summary>A loot bag expired and what was left was destroyed (R-4.3-03, WP-M4-9): a sink counted from <c>LootBagExpired</c>, never an inventory operation.</summary>
    BagExpired = 29,
    /// <summary>
    /// Units taken from a loot bag into a bag (WP-M4-9; audit kind <c>TransferIn</c>): the owner's own, or stolen by another (R-4.4-01).
    /// A move, not a faucet.
    /// </summary>
    LootBagTake = 30,
}

/// <summary>Items came into a character's bag (spec 8.1 <c>ItemCreated</c>).</summary>
public readonly record struct ItemCreated(long CharacterId, ushort ItemNetId, byte Rarity, int Count, InventoryReason Reason)
    : IGameEvent;

/// <summary>Items left a character's bag and the game (spec 8.1 <c>ItemDestroyed</c>).</summary>
public readonly record struct ItemDestroyed(long CharacterId, ushort ItemNetId, byte Rarity, int Count, InventoryReason Reason)
    : IGameEvent;

/// <summary>
/// Items moved from one owner to another without being created or destroyed (spec 8.1 <c>ItemTransferred</c>; WP-M4-4: a
/// trade, an escrow hold or release). <see cref="FromCharacterId"/> or <see cref="ToCharacterId"/> is 0 for escrow. One per
/// item and operation, after the commit; <see cref="InstanceId"/> 0 for a stack.
/// </summary>
public readonly record struct ItemTransferred(long FromCharacterId, long ToCharacterId, ushort ItemNetId, byte Rarity, int Count,
    long InstanceId, InventoryReason Reason, string OpKey) : IGameEvent;

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

/// <summary>
/// A party loot roll (spec 8.1 <c>LootRolled</c>, R-4.3-02; WP-M4-6): a member's pickup of <see cref="Units"/> units of the drop
/// <see cref="DropKey"/> was rolled among <see cref="Candidates"/> (character ids, join order) with the first unit's
/// <see cref="WeightsPpm"/>; each candidate won <see cref="Won"/> units, of which <see cref="Mailed"/> went by mail (the bag was
/// full). <see cref="Seed"/> with the debts reproduces the roll (rule 6: logged). Published when the roll is applied; the
/// winners' saves announce their parts later. The actor is the picker.
/// </summary>
public readonly record struct LootRolled(long PartyId, string DropKey, uint DropId, ushort ItemNetId, byte Rarity, int Units,
    ulong Seed, long PickerCharacterId, long[] Candidates, long[] WeightsPpm, int[] Won, int[] Mailed) : IGameEvent;

/// <summary>Nobody took a ground drop within <c>bagExpiryMs</c>; it is gone (backlog G).</summary>
public readonly record struct LootDropExpired(uint DropId, ushort ItemNetId, int Count) : IGameEvent;
