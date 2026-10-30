using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>
    /// Why the server refused a <see cref="UseItemRequest"/>, or why a started use did not use its item (spec 3.4 "Consumable
    /// use", WP-M3-3; spec 9.7 reason codes). Append only.
    /// </summary>
    public enum UseItemReason : byte
    {
        None = 0,
        /// <summary>The character's inventory is not loaded on this world (yet).</summary>
        InventoryUnavailable = 1,
        /// <summary>No such container or slot.</summary>
        ItemSlotInvalid = 2,
        /// <summary>The slot is empty (or no longer holds the item when the action reaches its effect point).</summary>
        ItemSlotEmpty = 3,
        /// <summary>The item is not a consumable with a <c>ConsumableDef</c> row.</summary>
        ItemNotUsable = 4,
        /// <summary>The units are reserved by another operation (crafting, a contract; WP-M3-2's reservations).</summary>
        ItemReserved = 5,
        /// <summary>Another use was asked for and has not reached its effect point yet.</summary>
        UseInProgress = 6,
        /// <summary>The consumable's cooldown (<c>ConsumableDef.cooldownMs</c>) has not run out.</summary>
        OnCooldown = 7,
        /// <summary>The potion's toxicity would pass the cap (R-3.4-03).</summary>
        TooToxic = 8,
        /// <summary>The food's satiety would pass the cap (R-3.4-03: "you cannot overeat").</summary>
        TooFull = 9,
        /// <summary>Another action runs (or a press started one this tick), the character is flinched, staggered or stunned.</summary>
        ActionBusy = 10,
        /// <summary>The use action was cancelled (a dodge) or interrupted (flinch, stagger, stun, death) before its effect point.</summary>
        UseInterrupted = 11,
        /// <summary>The character is dead.</summary>
        UseWhileDead = 12,
    }

    /// <summary>
    /// Client to server, reliable (spec 3.4 "Consumable use", 9.13; WP-M3-3, protocol 29): use one unit of the consumable in
    /// <see cref="Slot"/> of container <see cref="Container"/> (<c>ContainerDef.code</c>; 0 is the Normal bag). The server
    /// starts the item's use action (drinking, eating) and, at the action's effect point, removes the unit and applies its
    /// effects through <c>InventoryService</c> and the status system (architecture rules 1 and 4). <see cref="RequestId"/> is
    /// the client's idempotency key within one connection: a repeat gets the first answer and uses nothing more. MemoryPack
    /// after the <see cref="ReliableMessageId"/> byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class UseItemRequest
    {
        public uint RequestId { get; set; }
        public byte Container { get; set; }
        public byte Slot { get; set; }
    }

    /// <summary>
    /// Server to client, reliable: the answer to a <see cref="UseItemRequest"/>. A refusal (<see cref="Reason"/> not None)
    /// comes at once, or when the started action is interrupted before its effect point (<c>UseInterrupted</c>). A used item
    /// is answered only once the save holding its removal committed (rule 5), right before the <see cref="InventoryContents"/>
    /// that shows it. <see cref="ItemNetId"/> is the item used or asked for (0 when the slot held none).
    /// </summary>
    [MemoryPackable]
    public sealed partial class UseItemResult
    {
        public uint RequestId { get; set; }
        public ushort ItemNetId { get; set; }
        public UseItemReason Reason { get; set; }
    }

    public static partial class ReliableMessages
    {
        public const UseItemReason LastUseItemReason = UseItemReason.UseWhileDead;

        public static bool TryRead(ReadOnlySpan<byte> message, out UseItemRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.UseItemRequest, out UseItemRequest? value) || value == null)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out UseItemResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.UseItemResult, out UseItemResult? value) || value == null
                || value.Reason > LastUseItemReason)
                return false;
            result = value;
            return true;
        }
    }
}
