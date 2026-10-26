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
    /// Client to server, reliable (backlog G, WP16 inventory window): move the stack in <see cref="FromSlot"/> of the
    /// Normal bag onto <see cref="ToSlot"/> (move, merge onto the same key, or swap). The client only names slots; the server
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
