using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>
    /// Why the server refused a <see cref="HarvestRequest"/>, or why a started harvest yielded nothing (spec 5.2, WP-M3-5;
    /// spec 9.7 reason codes: the client shows the <c>ReasonCodeStrings</c> row of the member's name). Append only.
    /// </summary>
    public enum HarvestReason : byte
    {
        None = 0,
        /// <summary>The character's inventory is not loaded on this world (yet), or the world has no resource nodes.</summary>
        InventoryUnavailable = 1,
        /// <summary>The entity is not a resource node.</summary>
        NodeNotFound = 2,
        /// <summary>The node is depleted (or another harvester depleted it first: the lower entity id wins a tick).</summary>
        NodeDepleted = 3,
        /// <summary>The character is farther from the node than <c>NodeDef.reachCm</c>.</summary>
        OutOfRange = 4,
        /// <summary>The character does not hold the node's job (R-5.2-01 "only players with the job").</summary>
        JobRequired = 5,
        /// <summary>No tool of the node's type in the Safe bag's equipment area (R-5.2-02).</summary>
        ToolRequired = 6,
        /// <summary>The best tool of the node's type is below <c>NodeDef.minToolTier</c> (R-5.2-02 "tool tier gates node tier").</summary>
        ToolTierTooLow = 7,
        /// <summary>No room for the yield in the receiving bag (before the channel, and again at its end).</summary>
        BagFull = 8,
        /// <summary>Another action runs (or a press started one this tick), a use or harvest is in progress, or the character is flinched, staggered or stunned.</summary>
        ActionBusy = 9,
        /// <summary>The channel broke before its end: damage, a movement input, a dodge, a flinch, a stagger, a stun or death.</summary>
        HarvestInterrupted = 10,
        /// <summary>The character is dead.</summary>
        HarvestWhileDead = 11,
    }

    /// <summary>
    /// Client to server, reliable (spec 5.2, 9.13; WP-M3-5, protocol 30): harvest the resource node
    /// <see cref="NodeEntityId"/> in reach. The server checks the job, the tool and the reach, starts the node's harvest
    /// action (a channel), and at its effect point rolls the yield and adds it through <c>InventoryService</c> (architecture
    /// rules 1 and 4). <see cref="RequestId"/> is the client's idempotency key within one connection: a repeat gets the
    /// first answer and harvests nothing more. MemoryPack after the <see cref="ReliableMessageId"/> byte; append fields
    /// only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class HarvestRequest
    {
        public uint RequestId { get; set; }
        public uint NodeEntityId { get; set; }
    }

    /// <summary>
    /// Server to client, reliable: the answer to a <see cref="HarvestRequest"/>. A refusal (<see cref="Reason"/> not None)
    /// comes at once, or when the started channel breaks (<c>HarvestInterrupted</c>, <c>NodeDepleted</c>) or the yield does
    /// not fit at its end (<c>BagFull</c>). A harvest is answered only once the save holding its items committed (rule 5),
    /// right before the <see cref="InventoryContents"/> that shows it, with the item and count added.
    /// </summary>
    [MemoryPackable]
    public sealed partial class HarvestResult
    {
        public uint RequestId { get; set; }
        public uint NodeEntityId { get; set; }
        public ushort ItemNetId { get; set; }
        public ushort Count { get; set; }
        public HarvestReason Reason { get; set; }
    }

    public static partial class ReliableMessages
    {
        public const HarvestReason LastHarvestReason = HarvestReason.HarvestWhileDead;

        public static bool TryRead(ReadOnlySpan<byte> message, out HarvestRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.HarvestRequest, out HarvestRequest? value) || value == null)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out HarvestResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.HarvestResult, out HarvestResult? value) || value == null
                || value.Reason > LastHarvestReason)
                return false;
            result = value;
            return true;
        }
    }
}
