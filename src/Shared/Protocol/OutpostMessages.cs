using System;
using MemoryPack;
using Shared.Data.Tables;

namespace Shared.Protocol
{
    /// <summary>
    /// Why the server refused an outpost or vendor request (spec 4.2, 9.7 reason codes; the client shows the
    /// <c>ReasonCodeStrings</c> text). Append only, never renumber; <see cref="OutpostMessages.LastReason"/> is the highest.
    /// </summary>
    public enum OutpostReason : byte
    {
        None = 0,
        /// <summary>No such NPC, or the player cannot see it.</summary>
        NpcNotFound = 1,
        /// <summary>The player is farther from the NPC than its <c>NpcDef.interactRangeCm</c>.</summary>
        OutOfRange = 2,
        /// <summary>The NPC does not offer this interaction, or nothing on the server handles it yet.</summary>
        InteractionUnavailable = 3,
        /// <summary>Not a sell-only item, or a stolen one (R-4.1-01, R-4.4-02).</summary>
        NotSellable = 4,
        /// <summary>The slot is empty or holds fewer units than asked for.</summary>
        NothingToSell = 5,
        /// <summary>The price changed since the quote the player confirmed (user decision 2026-10-27): ask for a new quote.</summary>
        QuoteExpired = 6,
        NotEnoughGold = 7,
        /// <summary>This vendor does not sell that item.</summary>
        NotInStock = 8,
        BagFull = 9,
        /// <summary>The gold would pass <c>EconomySettings.maxGold</c>.</summary>
        GoldCapReached = 10,
        /// <summary>The server holds no bag for this character (a world without the item tables).</summary>
        InventoryUnavailable = 11,
        /// <summary>An achievement requirement of the character's current rank is not met yet (rank-up, R-3.6-04; protocol 18).</summary>
        RankRequirementsNotMet = 12,
        /// <summary>The character is already at the top rank, SS (rank-up, R-3.6-04).</summary>
        RankMaxed = 13,
        /// <summary>A newer quest board replaced the one the run was built from (WP-M2-6, protocol 22): pick again.</summary>
        BoardExpired = 14,
        /// <summary>The character already has a run that is not claimed (<c>QuestCaps.maxActiveRuns</c>).</summary>
        RunAlreadyActive = 15,
        /// <summary>No step, or more steps than <c>QuestCaps.maxStepsPerRun</c>, or an offer named twice.</summary>
        TooManySteps = 16,
        /// <summary>The board has no offer in that slot.</summary>
        OfferNotFound = 17,
        /// <summary>The character has no run to deliver to, forfeit or claim.</summary>
        NoActiveRun = 18,
        /// <summary>The run has no step with that index.</summary>
        StepNotFound = 19,
        /// <summary>The step is done or forfeited, or (deliver) it is a kill step.</summary>
        StepNotActive = 20,
        /// <summary>The bag holds none of the items the step asks for.</summary>
        NothingToDeliver = 21,
        /// <summary>A step is still active: every step must be done or forfeited before the claim (R-5.5-03).</summary>
        RunNotComplete = 22,
        /// <summary>Units the request needs are reserved by a craft (spec 4.1 reservations, WP-M3-2; protocol 26).</summary>
        ItemReserved = 23,
    }

    /// <summary>
    /// Client to server, reliable (spec 4.2, WP-M2-4): interact with an NPC (open the outpost sale, the vendor, a rank-up,
    /// the quest board). The server checks the NPC, its interactions and the range, then runs the handler registered for
    /// <see cref="Kind"/>. <see cref="RequestId"/> counts per connection. MemoryPack after the id byte; append fields only.
    /// </summary>
    [MemoryPackable]
    public sealed partial class OutpostInteractRequest
    {
        public uint RequestId { get; set; }
        public uint NpcEntityId { get; set; }
        public NpcInteraction Kind { get; set; }
    }

    /// <summary>Server to client, reliable: the answer to an <see cref="OutpostInteractRequest"/> (None = go ahead).</summary>
    [MemoryPackable]
    public sealed partial class OutpostInteractResult
    {
        public uint RequestId { get; set; }
        public uint NpcEntityId { get; set; }
        public NpcInteraction Kind { get; set; }
        public OutpostReason Reason { get; set; }
    }

    /// <summary>Client to server, reliable: the price of selling <see cref="Count"/> units from a bag slot (R-4.2-02 quote).</summary>
    [MemoryPackable]
    public sealed partial class SellQuoteRequest
    {
        public uint RequestId { get; set; }
        public uint NpcEntityId { get; set; }
        public byte Slot { get; set; }
        public ushort Count { get; set; }
    }

    /// <summary>
    /// Server to client, reliable: the quote, answered in the same tick. Units are priced one after the other (R-4.2-02), so
    /// <see cref="FirstUnit"/> and <see cref="LastUnit"/> show how far the price falls over this sale.
    /// </summary>
    [MemoryPackable]
    public sealed partial class SellQuoteResult
    {
        public uint RequestId { get; set; }
        public OutpostReason Reason { get; set; }
        public byte Slot { get; set; }
        public ushort Count { get; set; }
        public long Total { get; set; }
        public int FirstUnit { get; set; }
        public int LastUnit { get; set; }
    }

    /// <summary>
    /// Client to server, reliable: sell <see cref="Count"/> units from a bag slot for at least <see cref="QuotedTotal"/>
    /// gold (the quote the player confirmed). A class 1 operation (R-7.1-01): answered once it committed.
    /// </summary>
    [MemoryPackable]
    public sealed partial class SellRequest
    {
        public uint RequestId { get; set; }
        public uint NpcEntityId { get; set; }
        public byte Slot { get; set; }
        public ushort Count { get; set; }
        public long QuotedTotal { get; set; }
    }

    /// <summary>Server to client, reliable: a refused sale at once, a sale once it committed (rule 5).</summary>
    [MemoryPackable]
    public sealed partial class SellResult
    {
        public uint RequestId { get; set; }
        public OutpostReason Reason { get; set; }
        public ushort Count { get; set; }
        public long Gold { get; set; }
    }

    /// <summary>
    /// Client to server, reliable: buy <see cref="Count"/> units of an item a vendor sells for <see cref="QuotedTotal"/>
    /// gold (its fixed price times the count, R-4.2-01). A class 1 operation: answered once it committed.
    /// </summary>
    [MemoryPackable]
    public sealed partial class VendorBuyRequest
    {
        public uint RequestId { get; set; }
        public uint NpcEntityId { get; set; }
        public ushort ItemNetId { get; set; }
        public ushort Count { get; set; }
        public long QuotedTotal { get; set; }
    }

    /// <summary>Server to client, reliable: a refused purchase at once, a purchase once it committed.</summary>
    [MemoryPackable]
    public sealed partial class VendorBuyResult
    {
        public uint RequestId { get; set; }
        public OutpostReason Reason { get; set; }
        public ushort ItemNetId { get; set; }
        public ushort Count { get; set; }
        public long GoldSpent { get; set; }
    }

    /// <summary>
    /// Server to client, reliable: the character's gold as committed to the database, sent when the player enters the
    /// world and with every <see cref="InventoryContents"/>.
    /// </summary>
    [MemoryPackable]
    public sealed partial class GoldBalance
    {
        public long Gold { get; set; }
    }

    /// <summary>Bounds of the outpost messages.</summary>
    public static class OutpostMessages
    {
        public const OutpostReason LastReason = OutpostReason.ItemReserved;
        public const NpcInteraction LastInteraction = NpcInteraction.JewelerBench;
    }

    public static partial class ReliableMessages
    {
        public static bool TryRead(ReadOnlySpan<byte> message, out OutpostInteractRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.OutpostInteractRequest, out OutpostInteractRequest? value) || value == null
                || value.Kind > OutpostMessages.LastInteraction)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out OutpostInteractResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.OutpostInteractResult, out OutpostInteractResult? value) || value == null
                || value.Kind > OutpostMessages.LastInteraction || value.Reason > OutpostMessages.LastReason)
                return false;
            result = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out SellQuoteRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.SellQuoteRequest, out SellQuoteRequest? value) || value == null)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out SellQuoteResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.SellQuoteResult, out SellQuoteResult? value) || value == null
                || value.Reason > OutpostMessages.LastReason || value.Total < 0)
                return false;
            result = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out SellRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.SellRequest, out SellRequest? value) || value == null)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out SellResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.SellResult, out SellResult? value) || value == null
                || value.Reason > OutpostMessages.LastReason || value.Gold < 0)
                return false;
            result = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out VendorBuyRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.VendorBuyRequest, out VendorBuyRequest? value) || value == null)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out VendorBuyResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.VendorBuyResult, out VendorBuyResult? value) || value == null
                || value.Reason > OutpostMessages.LastReason || value.GoldSpent < 0)
                return false;
            result = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out GoldBalance balance)
        {
            balance = null!;
            if (!TryBody(message, ReliableMessageId.GoldBalance, out GoldBalance? value) || value == null || value.Gold < 0)
                return false;
            balance = value;
            return true;
        }
    }
}
