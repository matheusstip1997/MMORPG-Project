using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>What a <see cref="SocialRequest"/> asks for (spec 6.5 "M4 social", WP-M4-16). On the wire: append only.</summary>
    public enum SocialAction : byte
    {
        None = 0,
        /// <summary>
        /// Ask an online player (by display name) to be friends; when that player already asked the requester, this accepts
        /// their request instead (a friendship is mutual).
        /// </summary>
        AddFriend = 1,
        /// <summary>Accept the incoming request named by <see cref="SocialRequest.Ref"/>.</summary>
        AcceptFriend = 2,
        /// <summary>Decline the incoming request named by <see cref="SocialRequest.Ref"/>.</summary>
        DeclineFriend = 3,
        /// <summary>End a friendship (by <see cref="SocialRequest.Ref"/>, else by the name in the own list); for both sides.</summary>
        RemoveFriend = 4,
        /// <summary>Block an online player (by display name): their lines, whispers and invites never reach the requester.</summary>
        Block = 5,
        /// <summary>Mute an online player (by display name): their chat lines never reach the requester.</summary>
        Mute = 6,
        /// <summary>Lift a mute or a block (by <see cref="SocialRequest.Ref"/>, else by the name in the own list).</summary>
        Unblock = 7,
    }

    /// <summary>
    /// Why the server refused a <see cref="SocialRequest"/> (spec 9.1 reason codes, WP-M4-16). Append only;
    /// <see cref="ReliableMessages.LastSocialReason"/> is the highest. The names are <c>ReasonCodeStrings</c> rows.
    /// </summary>
    public enum SocialReason : byte
    {
        None = 0,
        /// <summary>No online player shows that name to the requester (also what a player who blocks the requester looks like).</summary>
        SocialTargetNotFound = 1,
        /// <summary>The target is the requester.</summary>
        SocialSelf = 2,
        /// <summary>The requester already has <c>SocialSettings.maxFriends</c> friends.</summary>
        SocialFriendCap = 3,
        /// <summary>The other player already has <c>SocialSettings.maxFriends</c> friends.</summary>
        SocialTargetFriendCap = 4,
        /// <summary>The requester already mutes or blocks <c>SocialSettings.maxBlocks</c> players.</summary>
        SocialBlockCap = 5,
        /// <summary>The two are friends already.</summary>
        SocialAlreadyFriends = 6,
        /// <summary>A request between the two is already waiting, or the target has <c>SocialSettings.maxIncomingRequests</c> waiting.</summary>
        SocialRequestPending = 7,
        /// <summary>The request timed out (<c>SocialSettings.requestTimeoutMs</c>) or its sender left.</summary>
        SocialRequestExpired = 8,
        /// <summary>No such entry in the own lists (a remove, an unblock, an accept or a decline).</summary>
        SocialEntryNotFound = 9,
        /// <summary>Too many social requests: <c>SocialSettings.requestsPerPeriod</c> per <c>periodMs</c>, up to <c>burst</c> at once.</summary>
        SocialRateLimited = 10,
        /// <summary>The social lists are not loaded (no <c>SocialSettings</c>, or the character's lists failed to load).</summary>
        SocialUnavailable = 11,
        /// <summary>A friend request to a player the requester blocks: lift the block first.</summary>
        SocialTargetBlocked = 12,
    }

    /// <summary>What a <see cref="SocialResult"/> tells besides a refusal (WP-M4-16). Append only.</summary>
    public enum SocialNotice : byte
    {
        /// <summary>A plain answer: done (reason None) or refused.</summary>
        None = 0,
        /// <summary>The answer to an add: a request now waits for the other player.</summary>
        RequestSent = 1,
        /// <summary>A notice (request id 0): <see cref="SocialResult.Name"/> asks the recipient to be friends.</summary>
        RequestReceived = 2,
        /// <summary>A notice: <see cref="SocialResult.Name"/> declined the recipient's request.</summary>
        RequestDeclined = 3,
        /// <summary>A notice: the recipient's request to <see cref="SocialResult.Name"/> timed out.</summary>
        RequestExpired = 4,
        /// <summary>The two are now friends (the answer to an accept, or a notice to the one who asked).</summary>
        FriendAdded = 5,
    }

    /// <summary>The kind of an entry in the block list (WP-M4-16; the <c>blocks.kind</c> column). Append only.</summary>
    public enum SocialBlockKind : byte
    {
        None = 0,
        /// <summary>Only the target's chat lines are hidden.</summary>
        Mute = 1,
        /// <summary>The target's chat lines are hidden and their whispers, party, trade and friend requests refused.</summary>
        Block = 2,
    }

    /// <summary>
    /// Client to server, reliable (spec 6.5, 9.12; WP-M4-16, protocol 52): one social action. The target is an entry of the
    /// requester's own lists by <see cref="Ref"/> (a per-session key the server gave in a <see cref="SocialState"/>), else
    /// <see cref="Name"/>: an online player's display name for an add, a block or a mute, a name in the own list for a remove
    /// or an unblock. No persistent character id ever reaches a client (spec 6.2). <see cref="RequestId"/> counts per
    /// connection. Capped by its <c>ReliableMessageCaps</c> row. MemoryPack after the id byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class SocialRequest
    {
        public uint RequestId { get; set; }

        public SocialAction Action { get; set; }

        public uint Ref { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-16, protocol 52): the answer to a <see cref="SocialRequest"/> (after the save that holds
    /// it committed, for an accepted change), or a notice with request id 0 (<see cref="SocialNotice"/>).
    /// </summary>
    [MemoryPackable]
    public sealed partial class SocialResult
    {
        /// <summary>The request answered; 0 for a notice.</summary>
        public uint RequestId { get; set; }

        public SocialAction Action { get; set; }

        public SocialReason Reason { get; set; }

        public SocialNotice Notice { get; set; }

        /// <summary>The other player's name as the recipient sees it (<c>DisplayIdentity</c>), or empty.</summary>
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>One friend of a <see cref="SocialState"/>.</summary>
    [MemoryPackable]
    public sealed partial class SocialFriendEntry
    {
        /// <summary>The per-session key of the entry (never a character id).</summary>
        public uint Ref { get; set; }

        /// <summary>The name the recipient saw for the friend (<c>DisplayIdentity</c>) when the friendship formed.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The friend is connected to the world (a lingering, disconnected character is offline). Nothing else is sent.</summary>
        public bool Online { get; set; }
    }

    /// <summary>One friend request waiting for the recipient's answer.</summary>
    [MemoryPackable]
    public sealed partial class SocialIncomingEntry
    {
        public uint Ref { get; set; }

        /// <summary>The asking player's name as the recipient sees it (<c>DisplayIdentity</c>).</summary>
        public string Name { get; set; } = string.Empty;

        public uint ExpiresInMs { get; set; }
    }

    /// <summary>One muted or blocked player.</summary>
    [MemoryPackable]
    public sealed partial class SocialBlockEntry
    {
        public uint Ref { get; set; }

        /// <summary>The name the recipient saw for the player (<c>DisplayIdentity</c>) when it muted or blocked them.</summary>
        public string Name { get; set; } = string.Empty;

        public SocialBlockKind Kind { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (spec 6.5, 9.3 <c>social_window</c>; WP-M4-16, protocol 52): the recipient's whole friends
    /// list, incoming requests and block list. Sent when the player enters the world and on every change, a friend going
    /// online or offline included.
    /// </summary>
    [MemoryPackable]
    public sealed partial class SocialState
    {
        /// <summary>Counts per connection; a newer state replaces an older one.</summary>
        public uint Version { get; set; }

        public SocialFriendEntry[] Friends { get; set; } = Array.Empty<SocialFriendEntry>();

        public SocialIncomingEntry[] Incoming { get; set; } = Array.Empty<SocialIncomingEntry>();

        public SocialBlockEntry[] Blocks { get; set; } = Array.Empty<SocialBlockEntry>();
    }

    public static partial class ReliableMessages
    {
        /// <summary>Friends a <see cref="SocialState"/> carries at most (the protocol cap; <c>SocialSettings.maxFriends</c> is at most this).</summary>
        public const int MaxSocialFriends = 200;

        /// <summary>Incoming requests a <see cref="SocialState"/> carries at most (<c>SocialSettings.maxIncomingRequests</c> is at most this).</summary>
        public const int MaxSocialIncoming = 32;

        /// <summary>Muted and blocked players a <see cref="SocialState"/> carries at most (<c>SocialSettings.maxBlocks</c> is at most this).</summary>
        public const int MaxSocialBlocks = 200;

        public const SocialAction LastSocialAction = SocialAction.Unblock;
        public const SocialReason LastSocialReason = SocialReason.SocialTargetBlocked;
        public const SocialNotice LastSocialNotice = SocialNotice.FriendAdded;
        public const SocialBlockKind LastSocialBlockKind = SocialBlockKind.Block;

        public static bool TryRead(ReadOnlySpan<byte> message, out SocialRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.SocialRequest, out SocialRequest? value) || value == null
                || value.Action == SocialAction.None || value.Action > LastSocialAction || !IsCleanName(value.Name, true))
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out SocialResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.SocialResult, out SocialResult? value) || value == null
                || value.Action > LastSocialAction || value.Reason > LastSocialReason || value.Notice > LastSocialNotice
                || !IsCleanName(value.Name, true))
                return false;
            result = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out SocialState state)
        {
            state = null!;
            if (!TryBody(message, ReliableMessageId.SocialState, out SocialState? value) || value == null || value.Friends == null
                || value.Incoming == null || value.Blocks == null || value.Friends.Length > MaxSocialFriends
                || value.Incoming.Length > MaxSocialIncoming || value.Blocks.Length > MaxSocialBlocks)
                return false;
            foreach (var friend in value.Friends)
            {
                if (friend == null || friend.Ref == 0 || !IsCleanName(friend.Name, false))
                    return false;
            }
            foreach (var incoming in value.Incoming)
            {
                if (incoming == null || incoming.Ref == 0 || !IsCleanName(incoming.Name, false))
                    return false;
            }
            foreach (var block in value.Blocks)
            {
                if (block == null || block.Ref == 0 || block.Kind == SocialBlockKind.None || block.Kind > LastSocialBlockKind
                    || !IsCleanName(block.Name, false))
                    return false;
            }
            state = value;
            return true;
        }
    }
}
