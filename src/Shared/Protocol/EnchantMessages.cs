using System;
using MemoryPack;
using Shared.Data.Tables;

namespace Shared.Protocol
{
    /// <summary>
    /// Why the server refused an enchant attempt (spec 5.3 M3 implementation, spec 9.7 reason codes; WP-M3-8). Append only;
    /// <see cref="ReliableMessages.LastEnchantReason"/> is the highest. Names shared with other enums share their
    /// <c>ReasonCodeStrings</c> row (<c>StationOutOfReach</c>, <c>InCombat</c>, <c>WrongStation</c>, <c>JobRequired</c>,
    /// <c>MaterialsMissing</c>, <c>ItemReserved</c>).
    /// </summary>
    public enum EnchantReason : byte
    {
        None = 0,
        /// <summary>The enchant tables or the character's inventory are not loaded on this world (yet).</summary>
        EnchantUnavailable = 1,
        /// <summary>The station is not visible, offers no crafting, or is out of interaction range.</summary>
        StationOutOfReach = 2,
        /// <summary>In combat or during an action (spec 5.3 M3 decision 7: crafting happens out of combat).</summary>
        InCombat = 3,
        /// <summary>The item's job enchants at the other station (user answer, WP-M3-8 plan).</summary>
        WrongStation = 4,
        /// <summary>The slot holds no equipment instance (or another one than the request names), or is not the Safe bag's equipment area or Equipped.</summary>
        NotEnchantable = 5,
        /// <summary>The character does not hold the item's job (R-5.3-04 "done by the item's job").</summary>
        JobRequired = 6,
        /// <summary>The item is at +9 already (R-5.3-04: never above +9).</summary>
        EnchantMaxed = 7,
        /// <summary>The Normal bag holds fewer free enchant stones than the attempt consumes.</summary>
        MaterialsMissing = 8,
        /// <summary>The item or the stones are held by another reservation (a forge session, an enchant waiting for its save).</summary>
        ItemReserved = 9,
    }

    /// <summary>
    /// Client to server, reliable (spec 5.3 R-5.3-04, 9.13; WP-M3-8, protocol 33): enchant the equipment instance
    /// <see cref="InstanceId"/> in slot <see cref="Slot"/> of container <see cref="Container"/> (the Safe bag's equipment
    /// area or Equipped) at the station <see cref="StationEntityId"/>. The server picks the stones from the Normal bag.
    /// Answered by an <see cref="EnchantResult"/> with the same <see cref="RequestId"/>: a refusal at once, the result only
    /// once its save committed (rule 5). Capped by its <c>ReliableMessageCaps</c> row.
    /// </summary>
    [MemoryPackable]
    public sealed partial class EnchantRequest
    {
        public uint RequestId { get; set; }
        public uint StationEntityId { get; set; }
        public byte Container { get; set; }
        public byte Slot { get; set; }
        public long InstanceId { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M3-8, protocol 33): the answer to an <see cref="EnchantRequest"/>. A result comes only
    /// once its save committed (rule 5), right before the <c>InventoryContents</c> that shows the new level. The stones are
    /// gone whatever the outcome (R-5.3-04). MemoryPack after the id byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class EnchantResult
    {
        public uint RequestId { get; set; }
        public EnchantReason Reason { get; set; }
        public long InstanceId { get; set; }
        public ushort ItemNetId { get; set; }
        public byte FromLevel { get; set; }
        public byte ToLevel { get; set; }
        /// <summary>The enchant stones consumed (0 for a refusal).</summary>
        public byte StonesUsed { get; set; }
    }

    public static partial class ReliableMessages
    {
        public const EnchantReason LastEnchantReason = EnchantReason.ItemReserved;

        public static bool TryRead(ReadOnlySpan<byte> message, out EnchantRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.EnchantRequest, out EnchantRequest? value) || value == null || value.InstanceId <= 0)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out EnchantResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.EnchantResult, out EnchantResult? value) || value == null
                || value.Reason > LastEnchantReason || value.FromLevel > EnchantLimits.MaxLevel || value.ToLevel > EnchantLimits.MaxLevel)
                return false;
            result = value;
            return true;
        }
    }
}
