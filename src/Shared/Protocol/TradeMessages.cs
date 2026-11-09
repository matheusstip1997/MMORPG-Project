using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>What a <see cref="TradeRequest"/> does (spec 4.5 "M4 trade window", M4 decision 5; WP-M4-4). Append only.</summary>
    public enum TradeAction : byte
    {
        None = 0,
        /// <summary>Invite the player <see cref="TradeRequest.TargetEntityId"/> (in reach, alive, out of combat).</summary>
        Invite = 1,
        /// <summary>Accept the open invite: the window opens for both.</summary>
        Accept = 2,
        /// <summary>Decline the open invite.</summary>
        Decline = 3,
        /// <summary>Offer <see cref="TradeRequest.Count"/> units of (<see cref="TradeRequest.Container"/>, <see cref="TradeRequest.Slot"/>).</summary>
        Add = 4,
        /// <summary>Take back own offer <see cref="TradeRequest.OfferIndex"/>.</summary>
        Remove = 5,
        /// <summary>Offer <see cref="TradeRequest.Gold"/> gold (0 takes it back).</summary>
        SetGold = 6,
        /// <summary>Lock own side at <see cref="TradeRequest.Revision"/> (the offers as the player saw them).</summary>
        Lock = 7,
        /// <summary>Confirm at <see cref="TradeRequest.Revision"/>, after both sides locked.</summary>
        Confirm = 8,
        /// <summary>Cancel the open trade or withdraw an invite.</summary>
        Cancel = 9,
    }

    /// <summary>Where a trade stands for the receiving player (<see cref="TradeState"/>). Append only.</summary>
    public enum TradePhase : byte
    {
        None = 0,
        /// <summary>This player invited the partner and waits for an answer.</summary>
        Inviting = 1,
        /// <summary>The partner invited this player (the invite prompt).</summary>
        Invited = 2,
        /// <summary>The window is open: offers, locks and confirms.</summary>
        Open = 3,
        /// <summary>Both confirmed: the swap is applied and saving (one transaction over both characters).</summary>
        Settling = 4,
        /// <summary>The swap committed.</summary>
        Done = 5,
        /// <summary>The trade ended without a swap (<see cref="TradeState.Reason"/> says why).</summary>
        Cancelled = 6,
    }

    /// <summary>
    /// Why the server refused a trade request or ended a trade (spec 4.5, 9.7 reason codes; R-4.1-04, M4 decision 5). Append only;
    /// <see cref="ReliableMessages.LastTradeReason"/> is the highest. The names are the <c>ReasonCodeStrings</c> codes.
    /// </summary>
    public enum TradeReason : byte
    {
        None = 0,
        /// <summary>The players are farther apart than <c>TradeSettings.rangeCm</c>.</summary>
        TradeTooFar = 1,
        /// <summary>One of them is in another trade or invite.</summary>
        TradeBusy = 2,
        /// <summary>The instance's own non-tradable flag is set.</summary>
        TradeNotTradable = 3,
        /// <summary>Equipped gear cannot be offered.</summary>
        TradeEquipped = 4,
        /// <summary>The units are reserved by something else (a forge session, an enchant).</summary>
        TradeReserved = 5,
        /// <summary>The offers changed since the player looked (a stale revision), or an offered unit is gone.</summary>
        TradeChanged = 6,
        /// <summary>The other player cancelled, or there is no open trade.</summary>
        TradeCancelled = 7,
        /// <summary>The invite, the lock or the confirm time ran out.</summary>
        TradeTimeout = 8,
        /// <summary>One of them is in combat or busy with an action.</summary>
        TradeInCombat = 9,
        /// <summary>One of them is dead.</summary>
        TradeDead = 10,
        /// <summary>A player cannot trade with themself.</summary>
        TradeSelf = 11,
        /// <summary>The invited player declined.</summary>
        TradeDeclined = 12,
        /// <summary>The gold is above <c>TradeSettings.maxGoldPerTrade</c>, or a side would pass <c>EconomySettings.maxGold</c>.</summary>
        TradeGoldCap = 13,
        /// <summary>A side's bag cannot take what it receives.</summary>
        TradeBagFull = 14,
        /// <summary>Sell-only drops cannot be traded (R-4.1-01).</summary>
        TradeSellOnly = 15,
        /// <summary>Quest items cannot be traded.</summary>
        TradeQuestItem = 16,
        /// <summary>Stolen or tainted items cannot be traded (R-4.1-04).</summary>
        TradeStolen = 17,
        /// <summary>A trade of this player or the other ended moments ago (<c>TradeSettings.cancelCooldownMs</c>).</summary>
        TradeCooldown = 18,
        /// <summary>Every offer slot of the side is used (<c>TradeSettings.maxSlots</c>).</summary>
        TradeSlotsFull = 19,
        /// <summary>No player of that name in sight (<c>/trade &lt;name&gt;</c>), or the target is not a player.</summary>
        TradeTargetNotFound = 20,
        /// <summary>Nothing would move.</summary>
        TradeEmpty = 21,
        /// <summary>Trading is not available (the character is not in the world yet, or its save is faulted).</summary>
        TradeUnavailable = 22,
        /// <summary>The gold offered is more than the player holds.</summary>
        TradeNotEnoughGold = 23,
    }

    /// <summary>
    /// Client to server, reliable (WP-M4-4, protocol 43): one trade action. Answered by a <see cref="TradeResult"/> with the same
    /// <see cref="RequestId"/>; every change of the trade also sends both players a <see cref="TradeState"/>. Capped by its
    /// <c>ReliableMessageCaps</c> row.
    /// </summary>
    [MemoryPackable]
    public sealed partial class TradeRequest
    {
        public uint RequestId { get; set; }

        public TradeAction Action { get; set; }

        /// <summary>Invite: the player entity to invite.</summary>
        public uint TargetEntityId { get; set; }

        /// <summary>Add: the container code of the offered units.</summary>
        public byte Container { get; set; }

        /// <summary>Add: the slot of the offered units.</summary>
        public ushort Slot { get; set; }

        /// <summary>Add: how many units (1 for an instance).</summary>
        public ushort Count { get; set; }

        /// <summary>Remove: the index in the own offer list.</summary>
        public byte OfferIndex { get; set; }

        /// <summary>SetGold: the gold offered.</summary>
        public long Gold { get; set; }

        /// <summary>Lock and Confirm: the <see cref="TradeState.Revision"/> the player saw.</summary>
        public uint Revision { get; set; }
    }

    /// <summary>One offered item line in a <see cref="TradeState"/>.</summary>
    [MemoryPackable]
    public sealed partial class TradeOffer
    {
        public ushort ItemNetId { get; set; }

        public byte Rarity { get; set; }

        public ushort Count { get; set; }

        /// <summary>Own side only: where the units lie (0 for the partner's).</summary>
        public byte Container { get; set; }

        /// <summary>Own side only: the slot.</summary>
        public ushort Slot { get; set; }

        /// <summary>An instance's enchant level (0 for a stack).</summary>
        public byte Enchant { get; set; }

        /// <summary>A crafted instance's quality (0 when none).</summary>
        public ushort Quality { get; set; }

        /// <summary>Gems socketed in an instance.</summary>
        public byte GemCount { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-4, protocol 43): the trade as this player sees it, sent on every change (and every
    /// <c>TradeSettings.stateResendMs</c> while both sides are locked, for the countdown). The partner is named through
    /// <c>DisplayIdentity</c> (rule 8). Phase <see cref="TradePhase.Done"/> comes only after the swap committed (rule 5).
    /// </summary>
    [MemoryPackable]
    public sealed partial class TradeState
    {
        public uint TradeId { get; set; }

        /// <summary>Bumped by every offer change; a lock or confirm must name the current one.</summary>
        public uint Revision { get; set; }

        public TradePhase Phase { get; set; }

        public uint PartnerEntityId { get; set; }

        public string PartnerName { get; set; } = string.Empty;

        public TradeOffer[] OwnOffer { get; set; } = Array.Empty<TradeOffer>();

        public TradeOffer[] PartnerOffer { get; set; } = Array.Empty<TradeOffer>();

        public long OwnGold { get; set; }

        public long PartnerGold { get; set; }

        public bool OwnLocked { get; set; }

        public bool PartnerLocked { get; set; }

        public bool OwnConfirmed { get; set; }

        public bool PartnerConfirmed { get; set; }

        /// <summary>Time left of the current timeout (invite, lock or confirm) in ms; 0 when none runs.</summary>
        public int MsLeft { get; set; }

        /// <summary>Why the trade ended (<see cref="TradePhase.Cancelled"/>), else <see cref="TradeReason.None"/>.</summary>
        public TradeReason Reason { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-4, protocol 43): the answer to a <see cref="TradeRequest"/>. A refusal comes at once; a
    /// confirm that completes the swap is answered only once both characters' saves committed (rule 5).
    /// </summary>
    [MemoryPackable]
    public sealed partial class TradeResult
    {
        public uint RequestId { get; set; }

        public TradeAction Action { get; set; }

        public TradeReason Reason { get; set; }
    }

    public static partial class ReliableMessages
    {
        public const TradeAction LastTradeAction = TradeAction.Cancel;
        public const TradePhase LastTradePhase = TradePhase.Cancelled;
        public const TradeReason LastTradeReason = TradeReason.TradeNotEnoughGold;

        /// <summary>Offer lines per side on the wire (<c>TradeSettings.maxSlots</c> is at most this).</summary>
        public const int MaxTradeOffers = 12;

        public static bool TryRead(ReadOnlySpan<byte> message, out TradeRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.TradeRequest, out TradeRequest? value) || value == null
                || value.Action == TradeAction.None || value.Action > LastTradeAction || value.Gold < 0)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out TradeState state)
        {
            state = null!;
            if (!TryBody(message, ReliableMessageId.TradeState, out TradeState? value) || value == null
                || value.Phase > LastTradePhase || value.Reason > LastTradeReason || value.PartnerName == null
                || value.OwnOffer == null || value.PartnerOffer == null
                || value.OwnOffer.Length > MaxTradeOffers || value.PartnerOffer.Length > MaxTradeOffers
                || Array.IndexOf(value.OwnOffer, null) >= 0 || Array.IndexOf(value.PartnerOffer, null) >= 0)
                return false;
            state = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out TradeResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.TradeResult, out TradeResult? value) || value == null
                || value.Action > LastTradeAction || value.Reason > LastTradeReason)
                return false;
            result = value;
            return true;
        }
    }
}
