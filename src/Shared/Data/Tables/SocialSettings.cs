using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Friends, mute and block (spec 6.5 "M4 social", R-6.5-01; M4 decision 16; WP-M4-16): the caps of the lists, how long a
    /// friend request waits, and the rate limit of the social requests (spec 7.5). Read by the server (<c>SocialService</c>).
    /// Every number is a placeholder (balance is deferred).
    /// </summary>
    [Table("SocialSettings", 1)]
    [Singleton]
    public sealed class SocialSettings : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Friends a character has at most (both sides are checked when a request is accepted).</summary>
        [Range(1, ReliableMessages.MaxSocialFriends)] public int MaxFriends { get; set; }

        /// <summary>Players a character mutes or blocks at most, the two kinds together.</summary>
        [Range(1, ReliableMessages.MaxSocialBlocks)] public int MaxBlocks { get; set; }

        /// <summary>Friend requests waiting for one character's answer at most; past it a new request is refused <c>SocialRequestPending</c>.</summary>
        [Range(1, ReliableMessages.MaxSocialIncoming)] public int MaxIncomingRequests { get; set; }

        /// <summary>How long a friend request waits for an answer (it lives in memory: it also ends when either side leaves the world).</summary>
        [Range(1_000, 3_600_000)] public int RequestTimeoutMs { get; set; }

        /// <summary>Social requests one session may have waiting for the next tick (R-2.5-03 reliable backlog), added to the request queue.</summary>
        [Range(1, 64)] public int MaxQueuedRequests { get; set; }

        /// <summary>
        /// Token bucket of the social requests per character (spec 7.5 rate limits, as the party's): this many per
        /// <see cref="PeriodMs"/>, up to <see cref="Burst"/> at once; past it a request is refused <c>SocialRateLimited</c>.
        /// </summary>
        [Range(1, 1_000)] public int RequestsPerPeriod { get; set; }

        [Range(100, 3_600_000)] public int PeriodMs { get; set; }

        [Range(1, 1_000)] public int Burst { get; set; }

        public void Validate(RowErrors errors)
        {
            if (Burst < RequestsPerPeriod)
                errors.Add(nameof(Burst), "must be at least RequestsPerPeriod");
        }

        /// <summary>
        /// The whole lists must fit one <see cref="SocialState"/> within <c>NetSecurity.maxReliableMessageBytes</c>, every name at
        /// its longest: a character name (<c>CharacterCreationConfig.nameMaxLength</c>, else the protocol cap) of 4 UTF-8 bytes per
        /// character.
        /// </summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (!set.TryGet(typeof(NetSecurity), out var net) || net.Rows.Count != 1)
                return;
            int nameChars = NameChars(set);
            int limit = ((NetSecurity)net.Rows[0]).MaxReliableMessageBytes;
            int needed = StateBytes(MaxFriends, MaxIncomingRequests, MaxBlocks, nameChars);
            if (needed > limit)
                errors.Add(nameof(MaxFriends), $"the full lists need up to {needed} bytes in one SocialState, more than NetSecurity.maxReliableMessageBytes ({limit}): lower maxFriends, maxIncomingRequests or maxBlocks");
        }

        /// <summary>The longest name a list holds: <c>CharacterCreationConfig.nameMaxLength</c>, else <see cref="ChatText.MaxNameChars"/>.</summary>
        public static int NameChars(ConfigSet set) =>
            set.TryGet(typeof(CharacterCreationConfig), out var creation) && creation.Rows.Count == 1
                ? System.Math.Min(((CharacterCreationConfig)creation.Rows[0]).NameMaxLength, ChatText.MaxNameChars)
                : ChatText.MaxNameChars;

        /// <summary>
        /// The largest <see cref="SocialState"/> with these caps: id, object header, version, three array headers, and per entry an
        /// object header, the ref, a name of up to <paramref name="nameChars"/> characters of 4 UTF-8 bytes with its two string
        /// headers, and its last field (at most 4 bytes).
        /// </summary>
        public static int StateBytes(int friends, int incoming, int blocks, int nameChars) =>
            1 + 1 + 4 + 3 * 4 + (friends + incoming + blocks) * (1 + 4 + 8 + 4 * nameChars + 4);

        public static SocialSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(SocialSettings), out var table) && table.Rows.Count == 1 ? (SocialSettings)table.Rows[0] : null;
    }
}
