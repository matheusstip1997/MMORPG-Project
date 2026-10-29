using System;
using MemoryPack;
using Shared.Data.Tables;

namespace Shared.Protocol
{
    /// <summary>
    /// Why the server refused a change to the Bystial book (spec 14.2, R-14.2-04/06; the client shows the
    /// <c>ReasonCodeStrings</c> text of the member name). Append only, never renumber.
    /// </summary>
    public enum BookReason : byte
    {
        None = 0,
        /// <summary>The server holds no book for this character (a world without the Bystial tables).</summary>
        BookUnavailable = 1,
        /// <summary>Placing and swapping only out of combat and not during an action (R-14.2-06, the blocks of R-3.5-04).</summary>
        InCombat = 2,
        /// <summary>No card of that monster type yet (R-14.2-02), or no such monster type.</summary>
        CardNotEarned = 3,
        /// <summary>That card is already in a slot (one card per monster type, R-14.2-04).</summary>
        AlreadyPlaced = 4,
        /// <summary>The slot is not unlocked at the character's rank (<c>BystialSlots</c>), or no slot is free.</summary>
        NoFreeBookSlot = 5,
        /// <summary>Taking out a card that is not in a slot.</summary>
        CardNotPlaced = 6,
    }

    /// <summary>Why a <see cref="BookState"/> was sent (the client toasts new and evolved cards). Append only.</summary>
    public enum BookCause : byte
    {
        /// <summary>Entering the world (or a reconnect): the book as it is, no toast.</summary>
        Initial = 0,
        /// <summary>A kill count toward a card changed.</summary>
        Kills = 1,
        /// <summary>Cards were placed, swapped or taken out (sent after the save committed, rule 5).</summary>
        Changed = 2,
        /// <summary>A first card of a monster type (R-14.2-02).</summary>
        CardEarned = 3,
        /// <summary>A card reached a higher tier.</summary>
        CardEvolved = 4,
    }

    /// <summary>Wire limits of the book messages; the Bystial tables are validated against them.</summary>
    public static class BookLimits
    {
        /// <summary>Most book slots at any rank (<c>BystialSlots.slots</c>).</summary>
        public const int MaxSlots = 16;

        /// <summary>Most monster types with cards (<c>BystialCardDef</c> monsters) a <see cref="BookState"/> may list.</summary>
        public const int MaxCards = 256;

        /// <summary><see cref="BookCard.Slot"/> and <see cref="BookChangeRequest.Slot"/>: not in a slot (a request: take it out).</summary>
        public const byte NoSlot = 255;

        /// <summary><see cref="BookChangeRequest.Slot"/>: the first free unlocked slot.</summary>
        public const byte AnyFreeSlot = 254;

        public const BookReason LastReason = BookReason.CardNotPlaced;
        public const BookCause LastCause = BookCause.CardEvolved;
    }

    /// <summary>
    /// One monster type of the book (spec 14.2): its card's tier (none before the first milestone), its slot, its kill count
    /// (R-14.2-01, the same counter the achievements read) and the count the next tier needs (0 at the top tier).
    /// </summary>
    [MemoryPackable]
    public sealed partial class BookCard
    {
        /// <summary><c>MonsterDef.netId</c>.</summary>
        public ushort MonsterNetId { get; set; }

        public BystialTier Tier { get; set; }

        /// <summary>The book slot it is placed in, or <see cref="BookLimits.NoSlot"/>.</summary>
        public byte Slot { get; set; }

        public long Kills { get; set; }

        /// <summary>Kills the next tier needs; 0 when the card is at its top tier.</summary>
        public long NextTierKills { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (spec 14.2, 9.4; WP-M2-8): the observer's Bystial book, the collection with the counters
    /// toward the next tier (every carded monster type with a kill or a card, by net id) and the slots its rank unlocks. Sent
    /// at enter world and whenever it changed (a placement only after its save committed, rule 5), never in the hot
    /// snapshot. A new card reaches the client this way, not through a hot message. MemoryPack after the
    /// <see cref="ReliableMessageId"/> byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class BookState
    {
        /// <summary>Counts up with every state sent to this connection.</summary>
        public uint Version { get; set; }

        public BookCause Cause { get; set; }

        /// <summary>Slots unlocked at the character's rank (<c>BystialSlots</c>, R-14.2-04).</summary>
        public byte SlotCount { get; set; }

        public BookCard[] Cards { get; set; } = Array.Empty<BookCard>();
    }

    /// <summary>
    /// Client to server, reliable (R-14.2-04/06): put the card of <see cref="MonsterNetId"/> into <see cref="Slot"/> (an
    /// occupied slot swaps: its card goes back to the collection), into the first free slot
    /// (<see cref="BookLimits.AnyFreeSlot"/>), or take it out (<see cref="BookLimits.NoSlot"/>). The client only names a
    /// card and a slot; the server applies it through <c>BystialService</c> (architecture rules 1 and 4).
    /// <see cref="RequestId"/> is the client's idempotency key within one connection: a repeat gets the first answer and
    /// changes nothing. MemoryPack after the <see cref="ReliableMessageId"/> byte; append fields only.
    /// </summary>
    [MemoryPackable]
    public sealed partial class BookChangeRequest
    {
        public uint RequestId { get; set; }
        public ushort MonsterNetId { get; set; }
        public byte Slot { get; set; }
    }

    /// <summary>
    /// Server to client, reliable: the answer to a <see cref="BookChangeRequest"/>. A refusal comes at once with its reason;
    /// an accepted change only once its save committed (rule 5), right before the <see cref="BookState"/> that shows it.
    /// </summary>
    [MemoryPackable]
    public sealed partial class BookChangeResult
    {
        public uint RequestId { get; set; }
        public BookReason Reason { get; set; }
    }

    public static partial class ReliableMessages
    {
        public static bool TryRead(ReadOnlySpan<byte> message, out BookState state)
        {
            state = null!;
            if (!TryBody(message, ReliableMessageId.BookState, out BookState? value) || value?.Cards == null
                || value.Cause > BookLimits.LastCause || value.SlotCount > BookLimits.MaxSlots || value.Cards.Length > BookLimits.MaxCards)
                return false;
            for (int i = 0; i < value.Cards.Length; i++)
            {
                var card = value.Cards[i];
                if (card == null || card.MonsterNetId == 0 || card.Tier > BystialTiers.Last || card.Kills < 0 || card.NextTierKills < 0
                    || (card.Slot != BookLimits.NoSlot && (card.Slot >= value.SlotCount || card.Tier == BystialTier.None)))
                    return false;
                for (int j = 0; j < i; j++)
                {
                    if (value.Cards[j].MonsterNetId == card.MonsterNetId
                        || (card.Slot != BookLimits.NoSlot && value.Cards[j].Slot == card.Slot))
                        return false;
                }
            }
            state = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out BookChangeRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.BookChangeRequest, out BookChangeRequest? value) || value == null)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out BookChangeResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.BookChangeResult, out BookChangeResult? value) || value == null
                || value.Reason > BookLimits.LastReason)
                return false;
            result = value;
            return true;
        }
    }
}
