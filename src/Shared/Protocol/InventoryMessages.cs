using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>
    /// Why the server refused a bag move (spec 9.7 reason codes; the client shows the <c>ReasonCodeStrings</c> text).
    /// Append only, never renumber.
    /// </summary>
    public enum InventoryMoveReason : byte
    {
        None = 0,
        /// <summary>A slot outside the bag, or the same slot twice.</summary>
        ItemSlotInvalid = 1,
        /// <summary>Nothing in the source slot (the client's view was out of date).</summary>
        ItemSlotEmpty = 2,
        /// <summary>The target holds a full stack of the same item.</summary>
        ItemStackFull = 3,
        /// <summary>The server holds no bag for this character (a world without the item tables).</summary>
        InventoryUnavailable = 4,
        /// <summary>Gear changes only out of combat (spec 12.7 combat state) and not during an action (spec 4.1 M2 item model).</summary>
        InCombat = 5,
        /// <summary>The target container does not take that category (R-4.1-02), or the item does not fit that equipment slot.</summary>
        ItemNotAccepted = 6,
        /// <summary>Below the item's <c>ItemDef.minLevel</c>.</summary>
        LevelTooLow = 7,
        /// <summary>The weapon type is not in the class line's list (<c>ClassGearDef.weaponTypes</c>, R-3.5-07).</summary>
        WeaponNotUsable = 8,
        /// <summary>No free slot for the gear an equip displaces (the same code as the pickup refusal).</summary>
        BagFull = 9,
    }

    /// <summary>What an accepted move did (the shared <c>Bag.TryMove</c> result). Append only.</summary>
    public enum InventoryMoveOutcome : byte
    {
        None = 0,
        /// <summary>Into an empty slot.</summary>
        Moved = 1,
        /// <summary>Onto a stack with the same key; what did not fit stayed in the source slot.</summary>
        Merged = 2,
        /// <summary>Two different stacks traded places.</summary>
        Swapped = 3,
    }

    /// <summary>
    /// Client to server, reliable (backlog G, WP16 inventory window; containers WP-M2-1): move the stack in
    /// <see cref="FromSlot"/> of container <see cref="FromContainer"/> onto <see cref="ToSlot"/> of <see cref="ToContainer"/>
    /// (<c>ContainerDef.code</c>s): move, merge onto the same key, or swap. Into the Equipped container it is an equip, out
    /// of it an unequip (spec 4.1 M2 item model). The client only names slots; the server
    /// applies it through <c>InventoryService</c> (architecture rules 1 and 4). <see cref="RequestId"/> is the client's
    /// idempotency key within one connection: a repeat gets the first answer and changes nothing. MemoryPack after the
    /// <see cref="ReliableMessageId"/> byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class InventoryMoveRequest
    {
        public uint RequestId { get; set; }
        public byte FromSlot { get; set; }
        public byte ToSlot { get; set; }
        /// <summary><c>ContainerDef.code</c> of the source (protocol 16).</summary>
        public byte FromContainer { get; set; }
        /// <summary><c>ContainerDef.code</c> of the target (protocol 16).</summary>
        public byte ToContainer { get; set; }
        /// <summary>
        /// Units to move (protocol 24, WP-M2-11 stack splitting, spec 9.13): 0 or the whole stack moves all of it as before;
        /// fewer split the stack into an empty slot, or onto the same stack key when they all fit.
        /// </summary>
        public ushort Count { get; set; }
    }

    /// <summary>
    /// Server to client, reliable: the answer to an <see cref="InventoryMoveRequest"/>. A refusal (<see cref="Reason"/> not
    /// None) is sent at once. An accepted move is answered only once the save holding it committed (rule 5), right before
    /// the <see cref="InventoryContents"/> that shows it.
    /// </summary>
    [MemoryPackable]
    public sealed partial class InventoryMoveResult
    {
        public uint RequestId { get; set; }
        public InventoryMoveReason Reason { get; set; }
        public InventoryMoveOutcome Outcome { get; set; }
    }
}
