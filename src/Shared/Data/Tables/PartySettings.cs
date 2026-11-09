using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Parties (spec 6.5 "M4 party", R-6.5-01; M4 decision 1; WP-M4-2): the size, the invite timeout, the exp share of a
    /// kill (R-3.6-03: the members within <see cref="ExpShareRangeCm"/> of the kill, the base split by damage contribution
    /// with <see cref="ExpFloorPercent"/> per member), the refresh of the party frame, and the per-monster damage ledger the
    /// split reads. A disconnected member stays a member while its character lingers (R-2.1-08) and leaves when the
    /// character leaves the world, so the linger needs no number here. Read by the server and the bots. Every number is a
    /// placeholder (balance is deferred).
    /// TODO(open) M4 decision 14: the party's unlock level (an <c>UnlockSchedule</c> row, WP-M4-11); the party works at any
    /// level in M4 until then.
    /// </summary>
    [Table("PartySettings", 1)]
    [Singleton]
    public sealed class PartySettings
    {
        /// <summary>Members a party holds at most (M4 decision 1: 6, the raid size of spec 9).</summary>
        [Range(2, ReliableMessages.MaxPartyMembers)] public int MaxMembers { get; set; }

        /// <summary>How long an invite waits for an answer.</summary>
        [Range(1_000, 600_000)] public int InviteTimeoutMs { get; set; }

        /// <summary>A member shares a kill's exp only within this distance of the kill (M4 decision 1: the interest radius).</summary>
        [Range(100, 100_000)] public int ExpShareRangeCm { get; set; }

        /// <summary>
        /// The least share of a kill's base exp a member in range gets, in whole percent, whatever damage it dealt; the
        /// shares are then scaled to the base (<c>PartyExpSplit</c>).
        /// </summary>
        [Range(0, 100)] public int ExpFloorPercent { get; set; }

        /// <summary>How often a member's party frame is refreshed (HP and position) when nothing changed.</summary>
        [Range(100, 60_000)] public int StateIntervalMs { get; set; }

        /// <summary>A monster's damage ledger is forgotten this long after the last hit by a player (a monster that never died).</summary>
        [Range(1_000, 3_600_000)] public int ContributionForgetMs { get; set; }

        /// <summary>Characters a monster's damage ledger keeps at most; damage by more attackers is not counted.</summary>
        [Range(ReliableMessages.MaxPartyMembers, 256)] public int MaxContributors { get; set; }

        /// <summary>Party requests one session may have waiting for the next tick (R-2.5-03 reliable backlog), added to the request queue.</summary>
        [Range(1, 64)] public int MaxQueuedRequests { get; set; }

        public static PartySettings? Find(ConfigSet set) =>
            set.TryGet(typeof(PartySettings), out var table) && table.Rows.Count == 1 ? (PartySettings)table.Rows[0] : null;
    }
}
