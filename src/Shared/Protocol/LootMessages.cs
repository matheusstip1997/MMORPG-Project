using System;
using System.Buffers;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>
    /// Why the server refused a pickup (spec 9.7 reason codes; the client shows the <c>ReasonCodeStrings</c> text).
    /// Append only, never renumber.
    /// </summary>
    public enum PickupReason : byte
    {
        None = 0,
        /// <summary>The drop is gone (taken, expired) or the player cannot see it.</summary>
        LootNotFound = 1,
        /// <summary>Farther than <c>LootSettings.pickupRangeCm</c>.</summary>
        OutOfRange = 2,
        /// <summary>Locked to its owner for <c>partyLootLockMs</c> (R-4.3-02; M1: the killer).</summary>
        LootLocked = 3,
        /// <summary>Not one unit fits in the Normal bag (M1: no mail overflow yet).</summary>
        BagFull = 4,
        /// <summary>Dead players cannot pick up.</summary>
        PickupWhileDead = 5,
    }

    /// <summary>
    /// Client to server, reliable (backlog G): pick up the ground drop <see cref="DropEntityId"/>. The server checks
    /// visibility, range, the lock and bag space (architecture rule 1); the client only names the drop.
    /// MemoryPack after the <see cref="ReliableMessageId"/> byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class PickupRequest
    {
        public uint DropEntityId { get; set; }
    }

    /// <summary>
    /// Server to client, reliable: the answer to a <see cref="PickupRequest"/>. <see cref="Reason"/> None means the server
    /// took <see cref="Taken"/> units off the ground; they show in the next <see cref="InventoryContents"/>, which is sent
    /// once they are in the database (rule 5: commit before confirming).
    /// </summary>
    [MemoryPackable]
    public sealed partial class PickupResult
    {
        public uint DropEntityId { get; set; }
        public PickupReason Reason { get; set; }
        public ushort Taken { get; set; }
    }

    /// <summary>One occupied bag slot in <see cref="InventoryContents"/>.</summary>
    [MemoryPackable]
    public sealed partial class InventorySlotEntry
    {
        public byte Slot { get; set; }
        /// <summary><c>ItemDef.netId</c>.</summary>
        public ushort ItemNetId { get; set; }
        /// <summary><c>RarityDef.tier</c>.</summary>
        public byte Rarity { get; set; }
        public bool Stolen { get; set; }
        public ushort Count { get; set; }
    }

    /// <summary>
    /// Server to client, reliable: the Normal bag as committed to the database (backlog G), sent when the player enters
    /// the world and after every save that changed it. <see cref="Revision"/> counts the messages of one connection.
    /// </summary>
    [MemoryPackable]
    public sealed partial class InventoryContents
    {
        public uint Revision { get; set; }
        public byte SlotCount { get; set; }
        public InventorySlotEntry[] Slots { get; set; } = Array.Empty<InventorySlotEntry>();
    }

    /// <summary>
    /// Framing of MemoryPack reliable messages: one <see cref="ReliableMessageId"/> byte, then the MemoryPack body. Decoders
    /// pick the type from the id (never a client-chosen type), are bounded by the transport's reliable size cap, check
    /// counts, and return false instead of throwing (R-2.5-04).
    /// </summary>
    public static class ReliableMessages
    {
        /// <summary>Most slots an <see cref="InventoryContents"/> may carry (<c>ContainerDef.MaxSlots</c>).</summary>
        public const int MaxInventorySlots = 200;

        public static ReliableMessageId PeekId(ReadOnlySpan<byte> message) =>
            message.Length == 0 ? ReliableMessageId.None : (ReliableMessageId)message[0];

        public static byte[] Write<T>(ReliableMessageId id, T message)
        {
            var buffer = new ArrayBufferWriter<byte>(32);
            buffer.GetSpan(1)[0] = (byte)id;
            buffer.Advance(1);
            MemoryPackSerializer.Serialize(buffer, message);
            return buffer.WrittenSpan.ToArray();
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out PickupRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.PickupRequest, out PickupRequest? value) || value == null)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out PickupResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.PickupResult, out PickupResult? value) || value == null
                || value.Reason > PickupReason.PickupWhileDead)
                return false;
            result = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out InventoryContents contents)
        {
            contents = null!;
            if (!TryBody(message, ReliableMessageId.InventoryContents, out InventoryContents? value) || value?.Slots == null
                || value.Slots.Length > MaxInventorySlots || value.Slots.Length > value.SlotCount)
                return false;
            foreach (var slot in value.Slots)
            {
                if (slot == null || slot.Slot >= value.SlotCount || slot.ItemNetId == 0 || slot.Count == 0)
                    return false;
            }
            contents = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out InventoryMoveRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.InventoryMoveRequest, out InventoryMoveRequest? value) || value == null)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out InventoryMoveResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.InventoryMoveResult, out InventoryMoveResult? value) || value == null
                || value.Reason > InventoryMoveReason.InventoryUnavailable || value.Outcome > InventoryMoveOutcome.Swapped
                || (value.Reason == InventoryMoveReason.None) == (value.Outcome == InventoryMoveOutcome.None))
                return false;
            result = value;
            return true;
        }

        private static bool TryBody<T>(ReadOnlySpan<byte> message, ReliableMessageId id, out T? value) where T : class
        {
            value = null;
            if (message.Length < 2 || message[0] != (byte)id)
                return false;
            try
            {
                value = MemoryPackSerializer.Deserialize<T>(message.Slice(1));
                return true;
            }
            catch (MemoryPackSerializationException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
            catch (OutOfMemoryException)
            {
                return false;
            }
        }
    }
}
