using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Chat (R-6.5-01, spec 9.12; backlog I follow-up): the line length, how many lines the client keeps and the server's
    /// queue bound. Read by the server and the client. Version 2 (WP-M4-2): the rate limit moved to one
    /// <see cref="ChatChannelDef"/> row per channel. Values in data/tables/ChatSettings.json are placeholders.
    /// </summary>
    [Table("ChatSettings", 2)]
    [Singleton]
    public sealed class ChatSettings
    {
        /// <summary>Longest line in characters (code points), at most the protocol cap <see cref="ChatText.MaxTextChars"/>.</summary>
        [Range(1, ChatText.MaxTextChars)] public int MaxChars { get; set; }

        /// <summary>Lines the client's chat panel keeps; older ones are dropped.</summary>
        [Range(1, 1_000)] public int HistoryLines { get; set; }

        /// <summary>
        /// Lines one session may have waiting for the next tick (R-2.5-03 reliable backlog). The network thread drops and
        /// counts any line past it, so a flooding client cannot queue work faster than the world does it.
        /// </summary>
        [Range(1, 256)] public int MaxQueuedLines { get; set; }
    }
}
