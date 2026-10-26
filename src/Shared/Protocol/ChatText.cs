namespace Shared.Protocol
{
    /// <summary>
    /// Why the server refused a chat line (spec 9.1: every failure has a reason; the client shows the text from
    /// <c>ReasonCodeStrings</c>). Append only, never renumber; <see cref="ChatCodec.LastReason"/> is the highest.
    /// </summary>
    public enum ChatReason : byte
    {
        None = 0,
        /// <summary>Nothing but white space.</summary>
        ChatEmpty = 1,
        /// <summary>More characters than <c>ChatSettings.maxChars</c>.</summary>
        ChatTooLong = 2,
        /// <summary>Control characters (line breaks included) or a broken surrogate pair.</summary>
        ChatInvalidText = 3,
        /// <summary>The character sent more lines than <c>ChatSettings</c> allows (R-6.5-01 rate limits).</summary>
        ChatRateLimited = 4,
    }

    /// <summary>
    /// The text rule for chat lines, shared so the client can check a line before sending it; the server decides
    /// (architecture rule 1). A character is a Unicode code point, so a surrogate pair counts once.
    /// TODO(open): the word filter (R-6.5-01 "filter") and whether invisible formatting characters (bidi overrides) are
    /// allowed are not decided.
    /// </summary>
    public static class ChatText
    {
        /// <summary>Protocol cap on a line, in characters; <c>ChatSettings.maxChars</c> is at most this.</summary>
        public const int MaxTextChars = 255;

        /// <summary>Protocol cap on a sender name, in characters (<c>CharacterCreationConfig.nameMaxLength</c> is at most 64).</summary>
        public const int MaxNameChars = 64;

        /// <summary>
        /// Trims white space at both ends and checks the rest: <see cref="ChatReason.None"/> with the trimmed text in
        /// <paramref name="clean"/>, or the refusal. Control characters (line breaks included) and lone surrogates are
        /// invalid. Bytes that are not valid UTF-8 never get this far: the reader refuses them.
        /// </summary>
        public static ChatReason Check(string? text, int maxChars, out string clean)
        {
            clean = string.Empty;
            if (text == null)
                return ChatReason.ChatEmpty;
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
                return ChatReason.ChatEmpty;

            int chars = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 == trimmed.Length || !char.IsLowSurrogate(trimmed[i + 1]))
                        return ChatReason.ChatInvalidText;
                    i++;
                }
                else if (char.IsLowSurrogate(c) || char.IsControl(c))
                {
                    return ChatReason.ChatInvalidText;
                }
                chars++;
            }
            if (chars > maxChars)
                return ChatReason.ChatTooLong;
            clean = trimmed;
            return ChatReason.None;
        }
    }
}
