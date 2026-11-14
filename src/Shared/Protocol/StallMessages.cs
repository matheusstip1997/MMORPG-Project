using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>What a <see cref="StallRequest"/> asks (spec 4.5 "M4 stalls", M4 decision 7; WP-M4-8). Append only.</summary>
    public enum StallAction : byte
    {
        None = 0,
        /// <summary>Open a stall on a free place with its first listing (one bag slot's units at a unit price).</summary>
        Open = 1,
        /// <summary>The owner adds a listing to the open stall (up to <c>StallSettings.listingsPerStall</c> in all).</summary>
        Add = 2,
        /// <summary>The owner cancels one listing of the stall; its units return by mail, the fee is kept.</summary>
        Cancel = 3,
        /// <summary>The owner closes the stall: every listing is cancelled, the units return by mail.</summary>
        Close = 4,
        /// <summary>Look at a stall: its listings come as a <see cref="StallState"/>, and again on every change while in reach.</summary>
        Browse = 5,
        /// <summary>Stop looking at a stall (the window closed).</summary>
        Leave = 6,
        /// <summary>Buy units of one of the stall's listings (through the shared listing model, WP-M4-7).</summary>
        Buy = 7,
    }

    /// <summary>
    /// Why the server refused a stall request (spec 4.5, 9.7 reason codes; M4 decision 7; WP-M4-8). Append only;
    /// <see cref="ReliableMessages.LastStallReason"/> is the highest. Names shared with other enums share their
    /// <c>ReasonCodeStrings</c> row (<c>OutOfRange</c>, <c>InCombat</c>, ...). <see cref="StallListingRefused"/> says the listing
    /// model refused: the reason is in <see cref="StallResult.ListingReason"/>.
    /// </summary>
    public enum StallReason : byte
    {
        None = 0,
        /// <summary>Another stall stands on that place, or is opening there.</summary>
        StallPlaceTaken = 1,
        /// <summary>One stall per character (M4 decision 7): the player has one open, or opening.</summary>
        StallOwnAlready = 2,
        /// <summary>Only the stall's owner may add, cancel or close.</summary>
        StallNotYours = 3,
        /// <summary>The stall closed (its time, sold out, or by its owner) or is closing.</summary>
        StallClosed = 4,
        /// <summary>No stall place or stall with that number is in view.</summary>
        StallNotFound = 5,
        /// <summary>The stall holds <c>StallSettings.listingsPerStall</c> listings.</summary>
        StallFull = 6,
        /// <summary>The stall place is not within <c>StallSettings.interactRangeCm</c>.</summary>
        OutOfRange = 7,
        /// <summary>A stall request never interrupts combat or an action under way.</summary>
        InCombat = 8,
        /// <summary>More stall requests than <c>StallSettings</c> allows per period (spec 7.6 rate limits).</summary>
        StallRateLimited = 9,
        /// <summary>This world has no stalls (no stall table, listings or mail).</summary>
        InteractionUnavailable = 10,
        /// <summary>The character's inventory or mailbox is not loaded (a request before enter world).</summary>
        InventoryUnavailable = 11,
        /// <summary>The stall's opening or another of the owner's requests is saving; try again in a moment.</summary>
        StallBusy = 12,
        /// <summary>The listing model refused (see <see cref="StallResult.ListingReason"/>).</summary>
        StallListingRefused = 13,
    }

    /// <summary>
    /// Client to server, reliable (spec 4.5 "M4 stalls", 9.13; WP-M4-8): one stall request, always in reach of the stall place
    /// (<c>StallSettings.interactRangeCm</c>). <see cref="Place"/> names the place of an <see cref="StallAction.Open"/> (its ordinal,
    /// <c>Shared.World.StallPlaces</c>); every other action names the stall by <see cref="StallEntityId"/>. Open and Add list
    /// <see cref="Count"/> units of the bag slot (<see cref="Container"/>, <see cref="Slot"/>) at <see cref="UnitPrice"/>; Cancel and
    /// Buy name <see cref="ListingId"/> (a buy of <see cref="Count"/> units at the <see cref="UnitPrice"/> the player saw). Answered
    /// by a <see cref="StallResult"/> with the same <see cref="RequestId"/>: a refusal at once, an applied request once its save
    /// or release committed (rule 5); Browse is answered by a <see cref="StallState"/>.
    /// </summary>
    [MemoryPackable]
    public sealed partial class StallRequest
    {
        public uint RequestId { get; set; }

        public StallAction Action { get; set; }

        public uint StallEntityId { get; set; }

        public byte Place { get; set; }

        /// <summary>The <c>ContainerDef.code</c> of the units (the Normal bag is the <c>normalBag</c> row's code).</summary>
        public byte Container { get; set; }

        public byte Slot { get; set; }

        public ushort Count { get; set; }

        public long UnitPrice { get; set; }

        public long ListingId { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-8): a stall as its viewer sees it, sent on Browse and again on every change while the
    /// viewer looks at it, and to the owner after each of their changes. <see cref="Own"/> when the viewer owns it;
    /// <see cref="Open"/> false once it closed (the last state its viewers get). <see cref="Rows"/> are its listings on sale,
    /// oldest first, never with the seller (the owner's name comes only as the stall entity's <c>EntityIdentity</c>, rule 8).
    /// </summary>
    [MemoryPackable]
    public sealed partial class StallState
    {
        public uint StallEntityId { get; set; }

        public byte Place { get; set; }

        public bool Own { get; set; }

        public bool Open { get; set; }

        public long ClosesUtcMs { get; set; }

        public ushort Cap { get; set; }

        public ListingEntry[] Rows { get; set; } = Array.Empty<ListingEntry>();
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-8): the answer to a <see cref="StallRequest"/>. A refusal comes at once; an applied open,
    /// add, cancel or buy once its save or release committed (rule 5), a close once the stall closed. For a buy,
    /// <see cref="Price"/> is the gold paid, <see cref="Tax"/> the sale tax destroyed out of it and <see cref="ToMail"/> when the
    /// units did not fit the bag; <see cref="Fee"/> the listing fee of an open or add. Append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class StallResult
    {
        public uint RequestId { get; set; }

        public StallAction Action { get; set; }

        public StallReason Reason { get; set; }

        public ListingReason ListingReason { get; set; }

        public uint StallEntityId { get; set; }

        public long ListingId { get; set; }

        public ushort Count { get; set; }

        public long Price { get; set; }

        public long Tax { get; set; }

        public long Fee { get; set; }

        public bool ToMail { get; set; }
    }

    public static partial class ReliableMessages
    {
        public const StallAction LastStallAction = StallAction.Buy;
        public const StallReason LastStallReason = StallReason.StallListingRefused;

        /// <summary>Rows of one <see cref="StallState"/> (<c>StallSettings.listingsPerStall</c> is not larger).</summary>
        public const int MaxStallRows = 32;

        public static bool TryRead(ReadOnlySpan<byte> message, out StallRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.StallRequest, out StallRequest? value) || value == null
                || value.Action == StallAction.None || value.Action > LastStallAction || value.UnitPrice < 0 || value.ListingId < 0)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out StallState state)
        {
            state = null!;
            if (!TryBody(message, ReliableMessageId.StallState, out StallState? value) || value == null || value.StallEntityId == 0
                || !ValidListingRows(value.Rows, MaxStallRows))
                return false;
            state = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out StallResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.StallResult, out StallResult? value) || value == null
                || value.Action > LastStallAction || value.Reason > LastStallReason || value.ListingReason > LastListingReason)
                return false;
            result = value;
            return true;
        }
    }
}
