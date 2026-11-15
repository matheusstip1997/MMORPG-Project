using Shared.Protocol;

namespace Shared.Simulation.Social
{
    /// <summary>
    /// The friends, mute and block rules (spec 6.5 "M4 social", R-6.5-01; M4 decision 16, answered with the WP-M4-16 plan on
    /// 2026-11-15): pure functions the server's <c>SocialService</c> runs and the tests pin. A friendship is mutual (a request the
    /// other accepts); a mute hides the target's chat lines only; a block hides them too and refuses the target's whispers,
    /// party, trade and friend requests, looking to the target like "no such player"; block replaces mute and both persist.
    /// </summary>
    public static class SocialRules
    {
        /// <summary>A muted or blocked sender's chat lines never reach the player who muted or blocked them (every channel).</summary>
        public static bool HidesChat(SocialBlockKind kind) => kind == SocialBlockKind.Mute || kind == SocialBlockKind.Block;

        /// <summary>A blocked player's whispers, party, trade and friend requests to the blocker are refused; a mute refuses none.</summary>
        public static bool RefusesRequests(SocialBlockKind kind) => kind == SocialBlockKind.Block;

        /// <summary>
        /// A friend request from a requester to a target, the requester's and the target's friends counted with the friendships
        /// waiting for their save. <paramref name="ownEntry"/> is how the requester mutes or blocks the target,
        /// <paramref name="theirEntry"/> how the target mutes or blocks the requester.
        /// </summary>
        public static SocialReason CheckRequest(SocialBlockKind ownEntry, SocialBlockKind theirEntry, bool alreadyFriends,
            bool requestWaiting, int ownFriends, int targetFriends, int targetIncoming, int maxFriends, int maxIncoming)
        {
            if (ownEntry != SocialBlockKind.None)
                return SocialReason.SocialTargetBlocked;
            if (RefusesRequests(theirEntry))
                return SocialReason.SocialTargetNotFound; // a block is never revealed (WP-M4-16 plan, answer 2)
            if (alreadyFriends)
                return SocialReason.SocialAlreadyFriends;
            if (requestWaiting)
                return SocialReason.SocialRequestPending;
            if (ownFriends >= maxFriends)
                return SocialReason.SocialFriendCap;
            if (targetFriends >= maxFriends)
                return SocialReason.SocialTargetFriendCap;
            if (targetIncoming >= maxIncoming)
                return SocialReason.SocialRequestPending;
            return SocialReason.None;
        }

        /// <summary>An accept, both sides' friends counted with the friendships waiting for their save (the caps may have filled since).</summary>
        public static SocialReason CheckAccept(int ownFriends, int requesterFriends, int maxFriends)
        {
            if (ownFriends >= maxFriends)
                return SocialReason.SocialFriendCap;
            if (requesterFriends >= maxFriends)
                return SocialReason.SocialTargetFriendCap;
            return SocialReason.None;
        }

        /// <summary>
        /// A mute or a block of a target the player now mutes or blocks with <paramref name="current"/> (None for nothing).
        /// <paramref name="changes"/> is false when the target already has <paramref name="wanted"/> (nothing to write).
        /// A new entry needs room under <paramref name="maxBlocks"/>; changing the kind of an entry does not.
        /// </summary>
        public static SocialReason CheckBlock(SocialBlockKind current, SocialBlockKind wanted, int entries, int maxBlocks, out bool changes)
        {
            changes = current != wanted;
            if (!changes)
                return SocialReason.None;
            if (current == SocialBlockKind.None && entries >= maxBlocks)
            {
                changes = false;
                return SocialReason.SocialBlockCap;
            }
            return SocialReason.None;
        }
    }
}
