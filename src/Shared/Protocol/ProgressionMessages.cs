using System;
using MemoryPack;
using Shared.Simulation.Stats;

namespace Shared.Protocol
{
    /// <summary>Why a <see cref="ProgressionState"/> was sent (the client toasts level-ups and rank-ups). Append only.</summary>
    public enum ProgressionCause : byte
    {
        /// <summary>Entering the world (or a reconnect): the state as it is, no toast.</summary>
        Initial = 0,
        /// <summary>Exp changed without a level-up.</summary>
        Exp = 1,
        /// <summary>One or more levels gained (several in one kill are allowed).</summary>
        LevelUp = 2,
        /// <summary>A rank-up committed (R-3.6-04; sent only after its save, rule 5).</summary>
        RankUp = 3,
        /// <summary>The skill tree spent or gave back points (R-3.5-04, WP-M2-5; sent only after the save committed).</summary>
        SkillPoints = 4,
        /// <summary>An attribute point was placed, or a respec gave the placed points back (R-3.4-08, WP-M2-13; after the commit).</summary>
        AttributePoints = 5,
    }

    /// <summary>
    /// Server to client, reliable (spec 3.6, 9.4; WP-M2-3): the observer's own level, exp, rank and skill points for the exp
    /// bar, the resource panel line and the toasts, plus the band multiplier of R-3.6-04 (the <c>exp_note</c>). Sent at
    /// enter world and whenever it changed, never in the hot snapshot. MemoryPack after the <see cref="ReliableMessageId"/>
    /// byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class ProgressionState
    {
        public int Level { get; set; }

        /// <summary>Exp into the current level.</summary>
        public long Exp { get; set; }

        /// <summary>Exp the current level needs; 0 at the level cap.</summary>
        public long ExpToNext { get; set; }

        public Rank Rank { get; set; }

        /// <summary>Unspent skill points (R-3.5-04; the skill tree spends them, WP-M2-5).</summary>
        public int SkillPoints { get; set; }

        /// <summary>The band modifier of R-3.6-03 for the character's level and rank, in percent (100 = x1.0).</summary>
        public int BandPercent { get; set; }

        public ProgressionCause Cause { get; set; }

        /// <summary>Unspent free attribute points (R-3.4-08, WP-M2-13, protocol 25; committed values, like the placed ones).</summary>
        public int AttributePoints { get; set; }

        /// <summary>Points placed on STR (R-3.4-08), on top of the class growth.</summary>
        public int StrPlaced { get; set; }

        public int DexPlaced { get; set; }

        public int StaPlaced { get; set; }

        public int IntPlaced { get; set; }

        /// <summary>Points placed on attribute <paramref name="attribute"/> (<c>Attributes.Str</c> ... <c>Attributes.Int</c>); 0 for another value.</summary>
        public int Placed(int attribute)
        {
            switch (attribute)
            {
                case Attributes.Str: return StrPlaced;
                case Attributes.Dex: return DexPlaced;
                case Attributes.Sta: return StaPlaced;
                case Attributes.Int: return IntPlaced;
                default: return 0;
            }
        }
    }

    /// <summary>Why the server refused an <see cref="AttributeSpendRequest"/> (R-3.4-08; spec 9.7 reason codes). Append only.</summary>
    public enum AttributeReason : byte
    {
        None = 0,
        /// <summary>The character's progression is not loaded on this world (yet).</summary>
        AttributeUnavailable = 1,
        /// <summary>Not STR, DEX, STA or INT.</summary>
        UnknownAttribute = 2,
        /// <summary>In combat or during an action (user decision 2026-10-29, like evolve and respec).</summary>
        AttributeBlockedCombat = 3,
        /// <summary>No unspent attribute point.</summary>
        NoAttributePoints = 4,
        /// <summary>The attribute's base with its placed points is at its <c>StatDef</c> max (the only cap in M2).</summary>
        AttributeAtMax = 5,
    }

    /// <summary>
    /// Client to server, reliable (R-3.4-08, WP-M2-13): place one unspent attribute point on <see cref="Attribute"/>
    /// (<c>Attributes.Str</c> 0, Dex 1, Sta 2, Int 3). The client only names the attribute; <c>ProgressionStore</c> applies it
    /// (architecture rules 1 and 4). <see cref="RequestId"/> is the client's idempotency key within one connection: a repeat
    /// gets the first answer and changes nothing. MemoryPack after the <see cref="ReliableMessageId"/> byte; append fields only.
    /// </summary>
    [MemoryPackable]
    public sealed partial class AttributeSpendRequest
    {
        public uint RequestId { get; set; }
        public byte Attribute { get; set; }
    }

    /// <summary>
    /// Server to client, reliable: the answer to an <see cref="AttributeSpendRequest"/>. A refusal comes at once with its
    /// reason; a placed point only once its save committed (rule 5), right before the <see cref="ProgressionState"/> that shows it.
    /// </summary>
    [MemoryPackable]
    public sealed partial class AttributeSpendResult
    {
        public uint RequestId { get; set; }
        public byte Attribute { get; set; }
        public AttributeReason Reason { get; set; }
    }

    public static partial class ReliableMessages
    {
        public const ProgressionCause LastProgressionCause = ProgressionCause.AttributePoints;
        public const AttributeReason LastAttributeReason = AttributeReason.AttributeAtMax;

        public static bool TryRead(ReadOnlySpan<byte> message, out AttributeSpendRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.AttributeSpendRequest, out AttributeSpendRequest? value) || value == null
                || value.Attribute >= Attributes.Count)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out AttributeSpendResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.AttributeSpendResult, out AttributeSpendResult? value) || value == null
                || value.Attribute >= Attributes.Count || value.Reason > LastAttributeReason)
                return false;
            result = value;
            return true;
        }
    }
}
