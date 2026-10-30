using System;
using System.Buffers;
using MemoryPack;
using Shared.Simulation.Stats;

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
        /// <summary>The instance id of an equipment, mold or tool instance (count 1); 0 for a stackable (protocol 16).</summary>
        public long InstanceId { get; set; }
        /// <summary>Non-tradable (R-4.1-01; carried, inert until trade, R-4.1-04; protocol 16).</summary>
        public bool NonTradable { get; set; }
        /// <summary>Units in this slot are reserved by a craft: they cannot be moved, sold or used (spec 4.1; protocol 27).</summary>
        public bool Reserved { get; set; }
        /// <summary>An instance's enchant level (protocol 27; 0 for a stackable).</summary>
        public byte Enchant { get; set; }
        /// <summary>A crafted instance's quality, 0 when not crafted (protocol 27).</summary>
        public ushort Quality { get; set; }
        /// <summary>
        /// A crafted instance's crafter (character id), 0 when not crafted (protocol 27). Never shown as a name: a name reaches
        /// clients only as an <c>EntityIdentity</c> through <c>DisplayIdentity</c> (architecture rule 8).
        /// </summary>
        public long CrafterId { get; set; }
        /// <summary>Made with a stolen input (R-4.4-03; protocol 27).</summary>
        public bool Tainted { get; set; }
        /// <summary>Socketed gems as <c>ItemDef.netId</c>s, at most <c>ItemDef.MaxGemSlots</c> (protocol 27).</summary>
        public ushort[] Gems { get; set; } = Array.Empty<ushort>();
        /// <summary>A crafted instance's stats snapshot, which replaces the item's own list (protocol 27); empty otherwise.</summary>
        public CraftedStatEntry[] CraftedStats { get; set; } = Array.Empty<CraftedStatEntry>();
    }

    /// <summary>One line of a crafted stats snapshot in <see cref="InventorySlotEntry"/> (protocol 27).</summary>
    [MemoryPackable]
    public sealed partial class CraftedStatEntry
    {
        /// <summary>The stat's index in <c>StatDef</c> file order (as in <c>OwnStats</c>).</summary>
        public byte Stat { get; set; }
        public int Flat { get; set; }
        /// <summary>Whole percents of the stat.</summary>
        public int Percent { get; set; }
    }

    /// <summary>One container other than the Normal bag in <see cref="InventoryContents"/> (protocol 16).</summary>
    [MemoryPackable]
    public sealed partial class InventoryContainerEntry
    {
        /// <summary><c>ContainerDef.code</c>.</summary>
        public byte Code { get; set; }
        public byte SlotCount { get; set; }
        public InventorySlotEntry[] Slots { get; set; } = Array.Empty<InventorySlotEntry>();
    }

    /// <summary>
    /// Server to client, reliable: the containers as committed to the database (backlog G), sent when the player enters the
    /// world and after every save that changed them. <see cref="SlotCount"/> and <see cref="Slots"/> are the Normal bag (as in
    /// M1); <see cref="Containers"/> every other container, the Equipped one included (spec 4.1 M2 item model, protocol 16;
    /// an equipped item sits at its lowest equipment slot). <see cref="Revision"/> counts the messages of one connection.
    /// </summary>
    [MemoryPackable]
    public sealed partial class InventoryContents
    {
        public uint Revision { get; set; }
        public byte SlotCount { get; set; }
        public InventorySlotEntry[] Slots { get; set; } = Array.Empty<InventorySlotEntry>();
        public InventoryContainerEntry[] Containers { get; set; } = Array.Empty<InventoryContainerEntry>();
    }

    /// <summary>
    /// Framing of MemoryPack reliable messages: one <see cref="ReliableMessageId"/> byte, then the MemoryPack body. Decoders
    /// read through <see cref="ReliableMessageReader"/> (the id picks the type, never the sender; the size is capped and
    /// <see cref="MemoryPackGuard"/> checks the whole body first, R-2.5-07), then check values and counts, and return false
    /// instead of throwing (R-2.5-04).
    /// </summary>
    public static partial class ReliableMessages
    {
        /// <summary>Most slots an <see cref="InventoryContents"/> may carry (<c>ContainerDef.MaxSlots</c>).</summary>
        public const int MaxInventorySlots = 200;

        /// <summary>Most containers besides the Normal bag an <see cref="InventoryContents"/> may carry (<c>ContainerDef.MaxContainers</c>).</summary>
        public const int MaxInventoryContainers = 16;

        /// <summary>Most gems a slot of an <see cref="InventoryContents"/> may carry (<c>ItemDef.MaxGemSlots</c>).</summary>
        public const int MaxGems = Shared.Data.Tables.ItemDef.MaxGemSlots;

        /// <summary>Most crafted stat lines a slot may carry (<c>ItemProperties.MaxCraftedStats</c>).</summary>
        public const int MaxCraftedStats = Shared.Simulation.Items.ItemProperties.MaxCraftedStats;

        public static ReliableMessageId PeekId(ReadOnlySpan<byte> message) =>
            message.Length == 0 ? ReliableMessageId.None : (ReliableMessageId)message[0];

        public static byte[] Write<T>(ReliableMessageId id, T message)
        {
            var buffer = new ArrayBufferWriter<byte>(32);
            buffer.GetSpan(1)[0] = (byte)id;
            buffer.Advance(1);
            MemoryPackSerializer.Serialize(buffer, message, MemoryPackSerializerOptions.Utf8);
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
            if (!TryBody(message, ReliableMessageId.InventoryContents, out InventoryContents? value) || value?.Containers == null
                || !ValidSlots(value.Slots, value.SlotCount) || value.Containers.Length > MaxInventoryContainers)
                return false;
            for (int i = 0; i < value.Containers.Length; i++)
            {
                var container = value.Containers[i];
                if (container == null || container.Code == 0 || !ValidSlots(container.Slots, container.SlotCount))
                    return false;
                for (int j = 0; j < i; j++)
                {
                    if (value.Containers[j].Code == container.Code)
                        return false;
                }
            }
            contents = value;
            return true;
        }

        /// <summary>Slots in range and each once, real items, and an instance (non-zero id) is one item.</summary>
        private static bool ValidSlots(InventorySlotEntry[]? slots, byte slotCount)
        {
            if (slots == null || slots.Length > MaxInventorySlots || slots.Length > slotCount)
                return false;
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot == null || slot.Slot >= slotCount || slot.ItemNetId == 0 || slot.Count == 0
                    || (slot.InstanceId != 0 && slot.Count != 1) || slot.InstanceId < 0 || slot.CrafterId < 0
                    || slot.Gems == null || slot.Gems.Length > MaxGems || slot.CraftedStats == null
                    || slot.CraftedStats.Length > MaxCraftedStats)
                    return false;
                foreach (var line in slot.CraftedStats)
                {
                    if (line == null)
                        return false;
                }
                for (int j = 0; j < i; j++)
                {
                    if (slots[j].Slot == slot.Slot)
                        return false;
                }
            }
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
                || value.Reason > InventoryMoveReason.ItemReserved || value.Outcome > InventoryMoveOutcome.Swapped
                || (value.Reason == InventoryMoveReason.None) == (value.Outcome == InventoryMoveOutcome.None))
                return false;
            result = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ProgressionState state)
        {
            state = null!;
            if (!TryBody(message, ReliableMessageId.ProgressionState, out ProgressionState? value) || value == null
                || value.Level < 1 || value.Exp < 0 || value.ExpToNext < 0 || value.Rank > Rank.SS || value.SkillPoints < 0
                || value.BandPercent < 0 || value.Cause > LastProgressionCause || value.AttributePoints < 0 || value.StrPlaced < 0
                || value.DexPlaced < 0 || value.StaPlaced < 0 || value.IntPlaced < 0)
                return false;
            state = value;
            return true;
        }

        private static bool TryBody<T>(ReadOnlySpan<byte> message, ReliableMessageId id, out T? value) where T : class
        {
            value = null;
            return ReliableMessageReader.TryRead(message, id, ref value);
        }
    }
}
