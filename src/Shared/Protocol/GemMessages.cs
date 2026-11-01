using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>What a <see cref="GemRequest"/> does at the jeweler's bench (R-5.3-05; WP-M3-9). Append only.</summary>
    public enum GemOp : byte
    {
        None = 0,
        /// <summary>Socket a cut gem from the Normal bag into a free gem slot (a failure consumes the gem).</summary>
        Socket = 1,
        /// <summary>Take a socketed gem out, intact, for the removal cost (<c>GemSocketOdds</c>).</summary>
        Remove = 2,
    }

    /// <summary>
    /// Why the server refused a gem request (spec 5.3 M3 implementation WP-M3-9, spec 9.7 reason codes). Append only;
    /// <see cref="ReliableMessages.LastGemReason"/> is the highest. Names shared with other enums share their
    /// <c>ReasonCodeStrings</c> row (<c>StationOutOfReach</c>, <c>InCombat</c>, <c>JobRequired</c>, <c>JobLevelTooLow</c>,
    /// <c>MaterialsMissing</c>, <c>ItemReserved</c>, <c>BagFull</c>).
    /// </summary>
    public enum GemReason : byte
    {
        None = 0,
        /// <summary>The character's inventory or the gem tables are not there (a request before enter world).</summary>
        GemUnavailable = 1,
        /// <summary>The character's previous gem operation is still saving.</summary>
        GemBusy = 2,
        StationOutOfReach = 3,
        /// <summary>In combat or during an action (dead included).</summary>
        InCombat = 4,
        /// <summary>Socketing and removal are Jeweler services (R-5.3-05).</summary>
        JobRequired = 5,
        /// <summary>The gem's tier needs a higher Jeweler level (<c>GemSocketOdds.minJobLevel</c>).</summary>
        JobLevelTooLow = 6,
        /// <summary>The target is not an equipment instance with gem slots in the Safe bag equipment area or Equipped.</summary>
        NotSocketable = 7,
        /// <summary>The named Normal-bag stack is not a cut gem (<c>GemDef</c>).</summary>
        NotAGem = 8,
        /// <summary>Every gem slot of the item is taken (<c>ItemDef.gemSlots</c>).</summary>
        NoGemSlot = 9,
        /// <summary>The item has no gem at the named index.</summary>
        NoSuchGem = 10,
        /// <summary>The gem or the removal cost is not in the Normal bag.</summary>
        MaterialsMissing = 11,
        ItemReserved = 12,
        /// <summary>The removed gem would not fit the Normal bag.</summary>
        BagFull = 13,
    }

    /// <summary>
    /// Client to server, reliable (spec 5.3, 9.13; WP-M3-9, protocol 35): at the jeweler's bench <see cref="StationEntityId"/>,
    /// socket the cut gem in Normal-bag slot <see cref="GemSlot"/> into the equipment instance <see cref="InstanceId"/> at
    /// (<see cref="Container"/>, <see cref="Slot"/>) (the Safe bag equipment area or Equipped), or remove its gem at
    /// <see cref="GemIndex"/>. Answered by a <see cref="GemResult"/> with the same <see cref="RequestId"/>: a refusal at once,
    /// an applied operation only once its save committed (rule 5). Capped by its <c>ReliableMessageCaps</c> row.
    /// </summary>
    [MemoryPackable]
    public sealed partial class GemRequest
    {
        public uint RequestId { get; set; }

        public uint StationEntityId { get; set; }

        public GemOp Op { get; set; }

        public byte Container { get; set; }

        public byte Slot { get; set; }

        /// <summary>The target's instance id, so a request names exactly the item the player saw.</summary>
        public long InstanceId { get; set; }

        /// <summary>Socket: the Normal-bag slot of the gem stack.</summary>
        public byte GemSlot { get; set; }

        /// <summary>Remove: the index in the item's gem list.</summary>
        public byte GemIndex { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M3-9, protocol 35): the answer to a <see cref="GemRequest"/>. A refusal comes at once;
    /// a socket (success, or the gem lost: <see cref="Success"/> false) or a removal only once its save committed (rule 5),
    /// right before the <c>InventoryContents</c> that shows it. MemoryPack after the id byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class GemResult
    {
        public uint RequestId { get; set; }

        public GemReason Reason { get; set; }

        public GemOp Op { get; set; }

        /// <summary>Socket: the gem is in the item. False: the gem was lost and the item is untouched (M3 decision 11).</summary>
        public bool Success { get; set; }

        public ushort GemItemNetId { get; set; }

        public long InstanceId { get; set; }

        /// <summary>Remove: the cost consumed (<c>GemSocketOdds.removalCostItemId</c> and its count after the job tree).</summary>
        public ushort CostItemNetId { get; set; }

        public ushort CostCount { get; set; }
    }

    public static partial class ReliableMessages
    {
        public const GemOp LastGemOp = GemOp.Remove;
        public const GemReason LastGemReason = GemReason.BagFull;

        public static bool TryRead(ReadOnlySpan<byte> message, out GemRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.GemRequest, out GemRequest? value) || value == null
                || value.Op == GemOp.None || value.Op > LastGemOp || value.InstanceId <= 0)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out GemResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.GemResult, out GemResult? value) || value == null
                || value.Reason > LastGemReason || value.Op > LastGemOp)
                return false;
            result = value;
            return true;
        }
    }
}
