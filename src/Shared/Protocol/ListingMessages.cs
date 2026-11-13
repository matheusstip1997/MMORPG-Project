using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>What a <see cref="ListingResult"/> answers (spec 4.5 "M4 listings", M4 decision 6; WP-M4-7). Append only.</summary>
    public enum ListingAction : byte
    {
        None = 0,
        /// <summary>A <see cref="ListingCreateRequest"/>.</summary>
        Create = 1,
        /// <summary>A <see cref="ListingBuyRequest"/>.</summary>
        Buy = 2,
        /// <summary>A <see cref="ListingCancelRequest"/>.</summary>
        Cancel = 3,
    }

    /// <summary>
    /// Why the server refused a listing request (spec 4.5, 9.7 reason codes; R-4.5-01, M4 decision 6; WP-M4-7). Append only;
    /// <see cref="ReliableMessages.LastListingReason"/> is the highest. Names shared with other enums share their
    /// <c>ReasonCodeStrings</c> row (<c>NotEnoughGold</c>, <c>BagFull</c>, <c>MailboxFull</c>, <c>OutOfRange</c>, ...).
    /// </summary>
    public enum ListingReason : byte
    {
        None = 0,
        /// <summary>No active listing with that id (sold out, cancelled, or never there).</summary>
        ListingNotFound = 1,
        /// <summary>The listing passed its expiry; its item is on the way back to the seller.</summary>
        ListingExpired = 2,
        /// <summary>A seller cannot buy their own listing.</summary>
        ListingOwn = 3,
        /// <summary>The seller has <c>ListingSettings.maxActivePerCharacter</c> active listings.</summary>
        ListingCap = 4,
        /// <summary>The instance's own non-tradable flag is set (R-4.1-04).</summary>
        ListingNotTradable = 5,
        /// <summary>Sell-only drops are sold at outposts, never listed (R-4.5-01).</summary>
        ListingSellOnly = 6,
        /// <summary>Quest items cannot be listed (R-4.5-01).</summary>
        ListingQuestItem = 7,
        /// <summary>Stolen or tainted items cannot be listed (R-4.5-01, R-4.4-03).</summary>
        ListingStolen = 8,
        /// <summary>Equipped gear cannot be listed.</summary>
        ListingEquipped = 9,
        /// <summary>The units are reserved by something else (a trade, a forge session).</summary>
        ListingReserved = 10,
        /// <summary>The unit price is 0, above <c>ListingSettings.maxUnitPrice</c>, or the total is above the gold cap.</summary>
        ListingPriceInvalid = 11,
        /// <summary>The duration is not one of <c>ListingSettings.durationsMs</c>.</summary>
        ListingDurationInvalid = 12,
        /// <summary>The count is 0, more than the slot holds, more than is left, or an instance count other than 1.</summary>
        ListingCountInvalid = 13,
        /// <summary>The listing was sold out before this request.</summary>
        ListingSold = 14,
        /// <summary>The listing or the slot is not what the player saw (another price, another item in the slot).</summary>
        ListingChanged = 15,
        /// <summary>Another purchase or the cancel of this listing is saving; try again in a moment.</summary>
        ListingBusy = 16,
        /// <summary>The gold does not cover the price or the listing fee.</summary>
        NotEnoughGold = 17,
        /// <summary>Reserved for a bag that cannot take a purchase; today a purchase that does not fit arrives by mail.</summary>
        BagFull = 18,
        /// <summary>The seller's mailbox is full: a listing is a new mail source (spec 4.5, user answer 2026-11-09).</summary>
        MailboxFull = 19,
        /// <summary>More listing requests than <c>ListingSettings</c> allows per period (spec 7.6 rate limits).</summary>
        ListingRateLimited = 20,
        /// <summary>The auctioneer is not in reach (the outpost pattern, M4 decision 6).</summary>
        OutOfRange = 21,
        /// <summary>No auctioneer with that entity id is visible.</summary>
        NpcNotFound = 22,
        /// <summary>That NPC does not run an auction, or this world has no auction.</summary>
        InteractionUnavailable = 23,
        /// <summary>The character's inventory or mailbox is not loaded (a request before enter world).</summary>
        InventoryUnavailable = 24,
        /// <summary>A listing never interrupts combat (M4 plan risk "souls-like feel").</summary>
        InCombat = 25,
        /// <summary>The search could not run (the database); try again.</summary>
        ListingSearchFailed = 26,
    }

    /// <summary>
    /// One listing as a client sees it: no seller (user answer 2026-11-13: rule 8 shows names only through
    /// <c>DisplayIdentity</c>, which needs an entity in view; TODO(open) a rule for showing sellers). <see cref="Item"/> is the
    /// listed unit as an inventory slot entry (<c>Slot</c> 0, <c>Count</c> the units left) for the tooltip.
    /// </summary>
    [MemoryPackable]
    public sealed partial class ListingEntry
    {
        public long ListingId { get; set; }

        public long UnitPrice { get; set; }

        public long ExpiresUtcMs { get; set; }

        public InventorySlotEntry Item { get; set; } = new InventorySlotEntry();
    }

    /// <summary>
    /// Client to server, reliable (spec 4.5, 9.13; WP-M4-7, protocol 46): one page of active auction listings, cheapest unit
    /// price first, from the auctioneer <see cref="NpcEntityId"/>. Every filter is optional (0 or empty: any). The name filter is
    /// <see cref="ItemNetIds"/>: the client matches the typed text against its own item names and sends the items that match,
    /// so the server never searches text. The page starts after (<see cref="AfterUnitPrice"/>, <see cref="AfterListingId"/>),
    /// the last row of the previous page (keyset pagination; 0 and 0 for the first page). Answered by a
    /// <see cref="ListingSearchResponse"/> with the same <see cref="RequestId"/>, after an SQL read off the tick.
    /// </summary>
    [MemoryPackable]
    public sealed partial class ListingSearchRequest
    {
        public uint RequestId { get; set; }

        public uint NpcEntityId { get; set; }

        public ushort[] ItemNetIds { get; set; } = Array.Empty<ushort>();

        /// <summary><c>ItemCategory</c> + 1; 0 for any category.</summary>
        public byte Category { get; set; }

        /// <summary>Lowest and highest <c>RarityDef.tier</c>; 0 and 0 for any rarity.</summary>
        public byte RarityMin { get; set; }

        public byte RarityMax { get; set; }

        /// <summary>Lowest and highest <c>ItemDef.minLevel</c>; 0 and 0 for any level.</summary>
        public ushort LevelMin { get; set; }

        public ushort LevelMax { get; set; }

        /// <summary>The item's job (<c>JobDef.netId</c> of <c>ItemDef.jobId</c>); 0 for any job.</summary>
        public ushort JobNetId { get; set; }

        /// <summary>The armour tier filter (WP-M4-13, decision 9a; tier + 1); 0 for any. TODO(WP-M4-13): read once its field merges.</summary>
        public byte ArmorTier { get; set; }

        public long AfterUnitPrice { get; set; }

        public long AfterListingId { get; set; }
    }

    /// <summary>Server to client, reliable (WP-M4-7, protocol 46): one search page; <see cref="HasMore"/> when a next page exists.</summary>
    [MemoryPackable]
    public sealed partial class ListingSearchResponse
    {
        public uint RequestId { get; set; }

        public ListingReason Reason { get; set; }

        public ListingEntry[] Rows { get; set; } = Array.Empty<ListingEntry>();

        public bool HasMore { get; set; }
    }

    /// <summary>
    /// Client to server, reliable (WP-M4-7, protocol 46): list <see cref="Count"/> units of (<see cref="Container"/>,
    /// <see cref="Slot"/>) at <see cref="UnitPrice"/> each for <c>ListingSettings.durationsMs[DurationIndex]</c>. The listing fee
    /// (<c>FeeRates</c> <c>listing</c> of the total) is paid now and never returned; the units go into escrow. Answered by a
    /// <see cref="ListingResult"/> once the seller's save committed (rule 5).
    /// </summary>
    [MemoryPackable]
    public sealed partial class ListingCreateRequest
    {
        public uint RequestId { get; set; }

        public uint NpcEntityId { get; set; }

        /// <summary>The <c>ContainerDef.code</c> of the units (the Normal bag is the <c>normalBag</c> row's code).</summary>
        public byte Container { get; set; }

        public byte Slot { get; set; }

        public ushort Count { get; set; }

        public long UnitPrice { get; set; }

        public byte DurationIndex { get; set; }
    }

    /// <summary>
    /// Client to server, reliable (WP-M4-7, protocol 46): buy <see cref="Count"/> units of listing <see cref="ListingId"/> at the
    /// <see cref="UnitPrice"/> the player saw (any count up to what is left; user answer 2026-11-13). The units go into the bag,
    /// or by mail when they do not fit; the proceeds minus the sale tax go to the seller by mail. Answered by a
    /// <see cref="ListingResult"/> once the buyer's save committed (rule 5).
    /// </summary>
    [MemoryPackable]
    public sealed partial class ListingBuyRequest
    {
        public uint RequestId { get; set; }

        public uint NpcEntityId { get; set; }

        public long ListingId { get; set; }

        public ushort Count { get; set; }

        public long UnitPrice { get; set; }
    }

    /// <summary>
    /// Client to server, reliable (WP-M4-7, protocol 46): cancel an own listing; the units left come back by mail (the fee is
    /// kept). Answered by a <see cref="ListingResult"/> once the release committed (rule 5).
    /// </summary>
    [MemoryPackable]
    public sealed partial class ListingCancelRequest
    {
        public uint RequestId { get; set; }

        public uint NpcEntityId { get; set; }

        public long ListingId { get; set; }
    }

    /// <summary>Client to server, reliable (WP-M4-7, protocol 46): the player's active listings, answered by a <see cref="MyListings"/>.</summary>
    [MemoryPackable]
    public sealed partial class MyListingsRequest
    {
        public uint RequestId { get; set; }

        public uint NpcEntityId { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-7, protocol 46): the player's active listings (<see cref="RequestId"/> 0 when pushed on
    /// opening the auction) and the cap.
    /// </summary>
    [MemoryPackable]
    public sealed partial class MyListings
    {
        public uint RequestId { get; set; }

        public ListingReason Reason { get; set; }

        public ushort Cap { get; set; }

        public ListingEntry[] Rows { get; set; } = Array.Empty<ListingEntry>();
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-7, protocol 46): the answer to a create, buy or cancel. A refusal comes at once; an
    /// applied request only once its save or release committed (rule 5). <see cref="Price"/> is the gold the buyer paid,
    /// <see cref="Tax"/> the sale tax destroyed out of it, <see cref="Fee"/> the listing fee; <see cref="ToMail"/> when a
    /// purchase did not fit the bag and arrives by mail. Append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class ListingResult
    {
        public uint RequestId { get; set; }

        public ListingAction Action { get; set; }

        public ListingReason Reason { get; set; }

        public long ListingId { get; set; }

        public ushort Count { get; set; }

        public long Price { get; set; }

        public long Tax { get; set; }

        public long Fee { get; set; }

        public bool ToMail { get; set; }
    }

    public static partial class ReliableMessages
    {
        public const ListingAction LastListingAction = ListingAction.Cancel;
        public const ListingReason LastListingReason = ListingReason.ListingSearchFailed;

        /// <summary>Rows a search page carries at most (<c>ListingSettings.pageSize</c> is not larger).</summary>
        public const int MaxListingRows = 50;

        /// <summary>Active listings one character may have at most (<c>ListingSettings.maxActivePerCharacter</c> is not larger).</summary>
        public const int MaxMyListings = 32;

        /// <summary>Item net ids one search may name (<c>ListingSettings.searchItemIdsMax</c> is not larger).</summary>
        public const int MaxListingItemFilter = 32;

        /// <summary>Durations a seller may choose from (<c>ListingSettings.durationsMs</c>).</summary>
        public const int MaxListingDurations = 8;

        public static bool TryRead(ReadOnlySpan<byte> message, out ListingSearchRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.ListingSearchRequest, out ListingSearchRequest? value) || value?.ItemNetIds == null
                || value.ItemNetIds.Length > MaxListingItemFilter || value.AfterUnitPrice < 0 || value.AfterListingId < 0)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ListingSearchResponse response)
        {
            response = null!;
            if (!TryBody(message, ReliableMessageId.ListingSearchResponse, out ListingSearchResponse? value) || value == null
                || value.Reason > LastListingReason || !ValidListingRows(value.Rows, MaxListingRows))
                return false;
            response = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ListingCreateRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.ListingCreateRequest, out ListingCreateRequest? value) || value == null
                || value.UnitPrice < 0)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ListingBuyRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.ListingBuyRequest, out ListingBuyRequest? value) || value == null
                || value.UnitPrice < 0 || value.ListingId < 0)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ListingCancelRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.ListingCancelRequest, out ListingCancelRequest? value) || value == null
                || value.ListingId < 0)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out MyListingsRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.MyListingsRequest, out MyListingsRequest? value) || value == null)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out MyListings listings)
        {
            listings = null!;
            if (!TryBody(message, ReliableMessageId.MyListings, out MyListings? value) || value == null
                || value.Reason > LastListingReason || !ValidListingRows(value.Rows, MaxMyListings))
                return false;
            listings = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out ListingResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.ListingResult, out ListingResult? value) || value == null
                || value.Action > LastListingAction || value.Reason > LastListingReason)
                return false;
            result = value;
            return true;
        }

        /// <summary>At most <paramref name="max"/> rows, each a real item with units left (an instance one unit).</summary>
        private static bool ValidListingRows(ListingEntry[]? rows, int max)
        {
            if (rows == null || rows.Length > max)
                return false;
            foreach (var row in rows)
            {
                var item = row?.Item;
                if (item == null || row!.ListingId <= 0 || row.UnitPrice <= 0 || item.ItemNetId == 0 || item.Count == 0
                    || (item.InstanceId != 0 && item.Count != 1) || item.InstanceId < 0 || item.Gems == null
                    || item.Gems.Length > MaxGems || item.CraftedStats == null || item.CraftedStats.Length > MaxCraftedStats
                    || Array.IndexOf(item.CraftedStats, null) >= 0)
                    return false;
            }
            return true;
        }
    }
}
