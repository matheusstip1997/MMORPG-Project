using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Local chat (R-6.5-01, spec 9.12; backlog I follow-up): the line length, the per-character rate limit the server
    /// applies (a token bucket: <see cref="Burst"/> lines at once, then <see cref="MessagesPerPeriod"/> per
    /// <see cref="PeriodMs"/>), and how many lines the client keeps. Read by the server and the client. Values in
    /// data/tables/ChatSettings.json are placeholders.
    /// </summary>
    [Table("ChatSettings", 1)]
    [Singleton]
    public sealed class ChatSettings : IValidatableRow
    {
        /// <summary>Longest line in characters (code points), at most the protocol cap <see cref="ChatText.MaxTextChars"/>.</summary>
        [Range(1, ChatText.MaxTextChars)] public int MaxChars { get; set; }

        /// <summary>Lines a character may send per <see cref="PeriodMs"/> once its burst is used up.</summary>
        [Range(1, 1_000)] public int MessagesPerPeriod { get; set; }

        [Range(100, 3_600_000)] public int PeriodMs { get; set; }

        /// <summary>Lines a character may send at once after being quiet (the bucket's capacity).</summary>
        [Range(1, 1_000)] public int Burst { get; set; }

        /// <summary>Lines the client's chat panel keeps; older ones are dropped.</summary>
        [Range(1, 1_000)] public int HistoryLines { get; set; }

        /// <summary>
        /// Lines one session may have waiting for the next tick (R-2.5-03 reliable backlog). The network thread drops and
        /// counts any line past it, so a flooding client cannot queue work faster than the world does it.
        /// </summary>
        [Range(1, 256)] public int MaxQueuedLines { get; set; }

        public void Validate(RowErrors errors)
        {
            // A bucket smaller than one period's refill could never reach the configured rate.
            if (Burst < MessagesPerPeriod)
                errors.Add(nameof(Burst), "must be at least MessagesPerPeriod");
        }
    }
}
