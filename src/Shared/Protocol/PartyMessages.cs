using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>What a <see cref="PartyRequest"/> asks for (spec 6.5 "M4 party", WP-M4-2). On the wire: append only.</summary>
    public enum PartyAction : byte
    {
        None = 0,
        /// <summary>Invite a player (by entity id, or by display name when the entity id is 0). Leader only in a party.</summary>
        Invite = 1,
        /// <summary>Accept the pending invite named by <see cref="PartyRequest.InviteId"/>.</summary>
        Accept = 2,
        /// <summary>Decline the pending invite named by <see cref="PartyRequest.InviteId"/>.</summary>
        Decline = 3,
        /// <summary>Leave the own party.</summary>
        Leave = 4,
        /// <summary>Remove a member (by its entity id, or by display name when it is 0). Leader only.</summary>
        Kick = 5,
        /// <summary>Make another member the leader (by its entity id, or by display name when it is 0). Leader only.</summary>
        PassLead = 6,
    }

    /// <summary>
    /// Why the server refused a <see cref="PartyRequest"/>, or what became of an invite (spec 9.7 reason codes, WP-M4-2). Append
    /// only; <see cref="ReliableMessages.LastPartyReason"/> is the highest. The names are <c>ReasonCodeStrings</c> rows.
    /// </summary>
    public enum PartyReason : byte
    {
        None = 0,
        /// <summary>The party already has <c>PartySettings.maxMembers</c> members.</summary>
        PartyFull = 1,
        /// <summary>Only the leader invites, kicks and passes the lead.</summary>
        PartyNotLeader = 2,
        /// <summary>The target (or, for an accept, the requester) is already in a party.</summary>
        PartyAlreadyIn = 3,
        /// <summary>The invite timed out (<c>PartySettings.inviteTimeoutMs</c>).</summary>
        PartyInviteExpired = 4,
        /// <summary>No such party or invite (any more): the requester is not in a party, or the inviter left.</summary>
        PartyNotFound = 5,
        /// <summary>No online player with that name or entity, or the named player is not a member.</summary>
        PartyTargetNotFound = 6,
        /// <summary>The target is the requester.</summary>
        PartySelf = 7,
        /// <summary>The target already has a pending invite (one at a time per player).</summary>
        PartyInvitePending = 8,
        /// <summary>To the inviter: the invitee declined.</summary>
        PartyDeclined = 9,
    }

    /// <summary>What changed in a party (the <c>PartyChanged</c> event, spec 8.1; the cause of a <see cref="PartyState"/>). Append only.</summary>
    public enum PartyChangeKind : byte
    {
        /// <summary>Nothing changed: entering the world, or the periodic refresh.</summary>
        None = 0,
        /// <summary>The party was formed (the first accept).</summary>
        Formed = 1,
        Joined = 2,
        Left = 3,
        Kicked = 4,
        LeaderChanged = 5,
        /// <summary>The party fell below two members and dissolved.</summary>
        Disbanded = 6,
    }

    /// <summary>
    /// Client to server, reliable (spec 6.5, 9.12; WP-M4-2, protocol 41): a party action. The target is
    /// <see cref="TargetEntityId"/> (an invite: an entity the requester sees; a kick or pass lead: a member's entity, wherever it
    /// is), else
    /// <see cref="TargetName"/> (an online player's display name). No persistent character id ever reaches a client (spec 6.2:
    /// a disguised member keeps it across sessions); a member keeps its entity while it lingers. <see cref="RequestId"/> counts
    /// per connection. Capped by
    /// its <c>ReliableMessageCaps</c> row. MemoryPack after the id byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class PartyRequest
    {
        public uint RequestId { get; set; }

        public PartyAction Action { get; set; }

        /// <summary>The invite an accept or a decline answers (<see cref="PartyInvite.InviteId"/>); 0 otherwise.</summary>
        public uint InviteId { get; set; }

        public uint TargetEntityId { get; set; }

        public string TargetName { get; set; } = string.Empty;
    }

    /// <summary>Server to client, reliable (WP-M4-2, protocol 41): the answer to a <see cref="PartyRequest"/>, or an invite's fate to the inviter.</summary>
    [MemoryPackable]
    public sealed partial class PartyResult
    {
        /// <summary>The request answered; 0 for a notice to the inviter (declined, expired).</summary>
        public uint RequestId { get; set; }

        public PartyAction Action { get; set; }

        public PartyReason Reason { get; set; }
    }

    /// <summary>One member of a <see cref="PartyState"/>.</summary>
    [MemoryPackable]
    public sealed partial class PartyMemberState
    {
        /// <summary>The member's entity in the world (kept while it lingers); the key of the member on the client.</summary>
        public uint EntityId { get; set; }

        /// <summary>The name the recipient sees for the member (<c>DisplayIdentity</c>, architecture rule 8).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The <c>ClassDef.netId</c>.</summary>
        public ushort ClassNetId { get; set; }

        public ushort Level { get; set; }

        /// <summary>False while the member lingers after a disconnect (R-2.1-08).</summary>
        public bool Online { get; set; }

        /// <summary>HP in whole percent of the maximum, 0 when dead.</summary>
        public byte HpPercent { get; set; }

        public int XCm { get; set; }

        public int YCm { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (spec 6.5, 9.3 <c>party_frame</c>; WP-M4-2, protocol 41): the recipient's party, every
    /// member listed in join order (the recipient included). Sent on every change and every <c>PartySettings.stateIntervalMs</c>
    /// while in a party; <see cref="PartyId"/> 0 with no members when the recipient left or the party dissolved. Party members
    /// always see each other (R-2.4-03), so the positions are no hidden entity (rule 9).
    /// </summary>
    [MemoryPackable]
    public sealed partial class PartyState
    {
        /// <summary>Counts per connection; a newer state replaces an older one.</summary>
        public uint Version { get; set; }

        public long PartyId { get; set; }

        /// <summary>The leader's entity.</summary>
        public uint LeaderEntityId { get; set; }

        /// <summary>What caused this state (<see cref="PartyChangeKind.None"/> for a refresh).</summary>
        public PartyChangeKind Cause { get; set; }

        /// <summary>The entity the cause is about (0 once it left the world); its name as the recipient sees it in <see cref="CauseName"/>.</summary>
        public uint CauseEntityId { get; set; }

        public string CauseName { get; set; } = string.Empty;

        public PartyMemberState[] Members { get; set; } = Array.Empty<PartyMemberState>();
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-2, protocol 41): an invite to the recipient, answered with a <see cref="PartyRequest"/>
    /// accept or decline naming <see cref="InviteId"/> within <see cref="TimeoutMs"/>.
    /// </summary>
    [MemoryPackable]
    public sealed partial class PartyInvite
    {
        public uint InviteId { get; set; }

        public uint InviterEntityId { get; set; }

        /// <summary>The inviter's name as the recipient sees it (<c>DisplayIdentity</c>).</summary>
        public string InviterName { get; set; } = string.Empty;

        public uint TimeoutMs { get; set; }
    }

    public static partial class ReliableMessages
    {
        /// <summary>Members a <see cref="PartyState"/> carries at most (the protocol cap; <c>PartySettings.maxMembers</c> is at most this).</summary>
        public const int MaxPartyMembers = 8;

        public const PartyAction LastPartyAction = PartyAction.PassLead;
        public const PartyReason LastPartyReason = PartyReason.PartyDeclined;
        public const PartyChangeKind LastPartyChangeKind = PartyChangeKind.Disbanded;

        public static bool TryRead(ReadOnlySpan<byte> message, out PartyRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.PartyRequest, out PartyRequest? value) || value == null
                || value.Action == PartyAction.None || value.Action > LastPartyAction || !IsCleanName(value.TargetName, true))
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out PartyResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.PartyResult, out PartyResult? value) || value == null
                || value.Action == PartyAction.None || value.Action > LastPartyAction || value.Reason > LastPartyReason)
                return false;
            result = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out PartyState state)
        {
            state = null!;
            if (!TryBody(message, ReliableMessageId.PartyState, out PartyState? value) || value == null || value.Cause > LastPartyChangeKind
                || !IsCleanName(value.CauseName, true) || value.Members == null || value.Members.Length > MaxPartyMembers
                || (value.PartyId == 0) != (value.Members.Length == 0))
                return false;
            foreach (var member in value.Members)
            {
                if (member == null || member.EntityId == 0 || member.HpPercent > 100 || !IsCleanName(member.Name, false))
                    return false;
            }
            state = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out PartyInvite invite)
        {
            invite = null!;
            if (!TryBody(message, ReliableMessageId.PartyInvite, out PartyInvite? value) || value == null || value.InviteId == 0
                || !IsCleanName(value.InviterName, false))
                return false;
            invite = value;
            return true;
        }

        /// <summary>A display name on the wire: at most <see cref="ChatText.MaxNameChars"/>, no control characters, not padded.</summary>
        private static bool IsCleanName(string? name, bool emptyAllowed)
        {
            if (name == null)
                return false;
            if (name.Length == 0)
                return emptyAllowed;
            return ChatText.Check(name, ChatText.MaxNameChars, out var clean) == ChatReason.None && clean.Length == name.Length;
        }
    }
}
