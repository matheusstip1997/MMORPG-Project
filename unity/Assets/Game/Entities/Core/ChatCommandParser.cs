#nullable enable
using System;

namespace Game.Gameplay.Entities
{
    /// <summary>The player commands a chat line may start with (spec 9.12; WP-M4-2). Append only: WP-M4-4 adds <c>/trade</c>.</summary>
    public enum ChatCommandKind : byte
    {
        None = 0,
        /// <summary><c>/invite &lt;name&gt;</c>: a party invite by display name.</summary>
        Invite = 1,
        /// <summary><c>/leave</c>: leave the party.</summary>
        Leave = 2,
        /// <summary><c>/kick &lt;name&gt;</c>: remove a member (the leader only; the server decides).</summary>
        Kick = 3,
        /// <summary><c>/lead &lt;name&gt;</c>: pass the lead to a member.</summary>
        Lead = 4,
        /// <summary><c>/w &lt;name&gt; &lt;text&gt;</c>: a whisper.</summary>
        Whisper = 5,
        /// <summary><c>/p &lt;text&gt;</c>: a party line.</summary>
        Party = 6,
    }

    /// <summary>A parsed player command: what it is, the name it names (if any) and the text it carries (if any).</summary>
    public readonly struct ChatCommand
    {
        public ChatCommand(ChatCommandKind kind, string name, string text)
        {
            Kind = kind;
            Name = name;
            Text = text;
        }

        public ChatCommandKind Kind { get; }

        public string Name { get; }

        public string Text { get; }
    }

    /// <summary>
    /// The client's player commands in chat (spec 9.12, 6.5; WP-M4-2), engine-free so the HUD and the bots read lines the same
    /// way: a line whose first word is one of <see cref="Commands"/> (any case) is a player command, read before anything is
    /// sent; every other line, <c>/</c> lines included, is sent as typed (the dev console reads its own commands on the server,
    /// spec 11.7; none of these names is one of its commands). The table is the one place a command is added (WP-M4-4 appends
    /// <c>/trade &lt;name&gt;</c>). A command with the wrong arguments is <see cref="ChatCommandParse.Usage"/>; the HUD shows
    /// the usage line. Display names are one word (character names have no spaces), so a name is the first argument.
    /// </summary>
    public static class ChatCommandParser
    {
        /// <summary>One command: its word (without the slash), what it is, whether it names a player, whether text follows.</summary>
        public readonly struct Spec
        {
            public Spec(string word, ChatCommandKind kind, bool takesName, bool takesText)
            {
                Word = word;
                Kind = kind;
                TakesName = takesName;
                TakesText = takesText;
            }

            public string Word { get; }

            public ChatCommandKind Kind { get; }

            public bool TakesName { get; }

            public bool TakesText { get; }
        }

        /// <summary>The player commands, in the order they are matched. Append only.</summary>
        public static readonly Spec[] Commands =
        {
            new Spec("invite", ChatCommandKind.Invite, takesName: true, takesText: false),
            new Spec("leave", ChatCommandKind.Leave, takesName: false, takesText: false),
            new Spec("kick", ChatCommandKind.Kick, takesName: true, takesText: false),
            new Spec("lead", ChatCommandKind.Lead, takesName: true, takesText: false),
            new Spec("w", ChatCommandKind.Whisper, takesName: true, takesText: true),
            new Spec("p", ChatCommandKind.Party, takesName: false, takesText: true),
        };

        /// <summary>
        /// Reads <paramref name="line"/> (already trimmed and checked by <c>ChatText</c>): <see cref="ChatCommandParse.NotACommand"/>
        /// for a line to send as typed, <see cref="ChatCommandParse.Command"/> with the command, or
        /// <see cref="ChatCommandParse.Usage"/> for a player command with missing or extra arguments.
        /// </summary>
        public static ChatCommandParse TryParse(string line, out ChatCommand command)
        {
            command = default;
            if (string.IsNullOrEmpty(line) || line[0] != '/')
                return ChatCommandParse.NotACommand;
            int wordEnd = IndexOfSpace(line, 1);
            string word = line.Substring(1, wordEnd - 1);
            Spec? spec = null;
            foreach (var candidate in Commands)
            {
                if (string.Equals(candidate.Word, word, StringComparison.OrdinalIgnoreCase))
                {
                    spec = candidate;
                    break;
                }
            }
            if (spec == null)
                return ChatCommandParse.NotACommand;
            string rest = wordEnd < line.Length ? line.Substring(wordEnd).Trim() : string.Empty;
            string name = string.Empty;
            if (spec.Value.TakesName)
            {
                int nameEnd = IndexOfSpace(rest, 0);
                name = rest.Substring(0, nameEnd);
                rest = nameEnd < rest.Length ? rest.Substring(nameEnd).Trim() : string.Empty;
                if (name.Length == 0)
                    return ChatCommandParse.Usage;
            }
            if (spec.Value.TakesText ? rest.Length == 0 : rest.Length != 0)
                return ChatCommandParse.Usage;
            command = new ChatCommand(spec.Value.Kind, name, rest);
            return ChatCommandParse.Command;
        }

        private static int IndexOfSpace(string text, int from)
        {
            for (int i = from; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                    return i;
            }
            return text.Length;
        }
    }

    /// <summary>What <see cref="ChatCommandParser.TryParse"/> made of a line.</summary>
    public enum ChatCommandParse : byte
    {
        /// <summary>Not a player command: send the line as typed (the dev console reads its own <c>/</c> commands).</summary>
        NotACommand = 0,
        Command = 1,
        /// <summary>A player command with missing or extra arguments: nothing is sent, the usage line shows.</summary>
        Usage = 2,
    }
}
