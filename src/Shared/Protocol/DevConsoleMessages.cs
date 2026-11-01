using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>
    /// Why the dev console refused a command, or <see cref="None"/> for one it ran (spec 11.7 dev console; WP-M3-13). The
    /// client shows the <c>ReasonCodeStrings</c> text of a refusal before the server's detail lines. Append only;
    /// <see cref="ReliableMessages.LastDevReason"/> is the highest. Names shared with other enums share their row
    /// (<c>BagFull</c>).
    /// </summary>
    public enum DevReason : byte
    {
        None = 0,
        /// <summary>This server has no dev console: not a DevTools build, or <c>MMORPG_DEV_TOOLS</c> is off.</summary>
        DevToolsOff = 1,
        /// <summary>The account is not one the dev login made (a Steam account is never served), or it could not be checked.</summary>
        NotDevAccount = 2,
        UnknownCommand = 3,
        /// <summary>Missing, extra or malformed arguments; the detail lines show the command's usage.</summary>
        BadArguments = 4,
        /// <summary>No <c>ItemDef</c>, <c>RarityDef</c> or <c>GemDef</c> row with that id.</summary>
        UnknownItem = 5,
        /// <summary>No <c>MonsterDef</c> row with that id.</summary>
        UnknownMonster = 6,
        /// <summary>No <c>JobDef</c> row with that id, or the character does not hold that job.</summary>
        UnknownJob = 7,
        /// <summary>No world marker with that id.</summary>
        UnknownMarker = 8,
        /// <summary>The level is below the character's own: the game cannot lower a level (user answer 2026-11-01).</summary>
        LevelDown = 9,
        /// <summary>A kill count below the character's own: the game cannot lower it either.</summary>
        ValueDown = 10,
        /// <summary>Above what the tables allow (the level cap, a stack, the gem slots, the enchant cap, the spawn cap).</summary>
        OverLimit = 11,
        BagFull = 12,
        /// <summary>The bag that holds the item (the Normal or the Safe bag) holds fewer than asked.</summary>
        NotInBag = 13,
        /// <summary>The job pick was refused by its own rules; the detail line names the <c>JobReason</c>.</summary>
        JobRefused = 14,
        /// <summary>The teleport target is outside the walkable world or in a region the character's level may not enter.</summary>
        TargetBlocked = 15,
        /// <summary>The character's previous dev command is still saving.</summary>
        DevBusy = 16,
        /// <summary>The world was built without the system the command needs (worlds for tests).</summary>
        DevUnavailable = 17,
        /// <summary>The character is dead (only <c>/where</c> and <c>/help</c> work then).</summary>
        Dead = 18,
        /// <summary>No resource node within reach.</summary>
        NoNode = 19,
    }

    /// <summary>
    /// Server to client, reliable (spec 11.7 dev console; WP-M3-13, protocol 36): the answer to a chat line starting with
    /// <c>/</c>. A refusal comes at once; a command that changes the character only once its save committed (rule 5), right
    /// before the messages that show the change. <see cref="Lines"/> are the server's detail lines (ids and values, the
    /// help), English dev text that never reaches a Release build. MemoryPack after the id byte; append fields only.
    /// </summary>
    [MemoryPackable]
    public sealed partial class DevCommandResult
    {
        public DevReason Reason { get; set; }

        public string[] Lines { get; set; } = Array.Empty<string>();
    }

    /// <summary>Protocol caps of <see cref="DevCommandResult"/>.</summary>
    public static class DevConsoleLimits
    {
        /// <summary>Most detail lines in one answer (<c>/help</c> is the longest).</summary>
        public const int MaxLines = 32;

        /// <summary>Most characters in one detail line.</summary>
        public const int MaxLineChars = 160;
    }

    public static partial class ReliableMessages
    {
        public const DevReason LastDevReason = DevReason.NoNode;

        /// <summary>A dev console answer: a known reason and at most <see cref="DevConsoleLimits.MaxLines"/> clean lines.</summary>
        public static bool TryRead(ReadOnlySpan<byte> message, out DevCommandResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.DevCommandResult, out DevCommandResult? value) || value?.Lines == null
                || value.Reason > LastDevReason || value.Lines.Length > DevConsoleLimits.MaxLines)
                return false;
            foreach (var line in value.Lines)
            {
                if (ChatText.Check(line, DevConsoleLimits.MaxLineChars, out var clean) != ChatReason.None || clean.Length != line!.Length)
                    return false;
            }
            result = value;
            return true;
        }
    }
}
