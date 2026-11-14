using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>
    /// Server to client, reliable (<see cref="ReliableMessageId.LootRollNotice"/>, WP-M4-6, protocol 46): one winner's part of a
    /// party loot roll (spec 4.3 R-4.3-02, "M4 party loot"), sent to every connected member of the party once the winner's save
    /// committed it (user answer 2026-11-13). The client writes the party tab's roll line from <c>UiStrings</c> and shows the
    /// winner a toast. <see cref="WinnerName"/> already went through <c>DisplayIdentity(winner, recipient)</c> (rule 8).
    /// <see cref="Units"/> went into the winner's bag, <see cref="Mailed"/> by mail (the bag was full). Append fields only.
    /// </summary>
    [MemoryPackable]
    public sealed partial class LootRollNotice
    {
        /// <summary>The ground drop that was rolled (it may be gone by now).</summary>
        public uint DropEntityId { get; set; }

        /// <summary><c>ItemDef.netId</c>.</summary>
        public ushort ItemNetId { get; set; }

        /// <summary><c>RarityDef.tier</c>.</summary>
        public byte Rarity { get; set; }

        /// <summary>The winner's entity, 0 when it left the world before the commit.</summary>
        public uint WinnerEntityId { get; set; }

        public string WinnerName { get; set; } = string.Empty;

        /// <summary>Units the winner's bag took.</summary>
        public ushort Units { get; set; }

        /// <summary>Units sent to the winner by mail.</summary>
        public ushort Mailed { get; set; }
    }

    public static partial class ReliableMessages
    {
        public static bool TryRead(ReadOnlySpan<byte> message, out LootRollNotice notice)
        {
            notice = null!;
            if (!TryBody(message, ReliableMessageId.LootRollNotice, out LootRollNotice? value) || value == null || value.ItemNetId == 0
                || value.Units + value.Mailed == 0 || !IsCleanName(value.WinnerName, true))
                return false;
            notice = value;
            return true;
        }
    }
}
