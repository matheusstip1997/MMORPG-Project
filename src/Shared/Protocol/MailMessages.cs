using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>
    /// What wrote a system mail (spec 4.5 M4 mail, M4 decision 4; WP-M4-3). Append only; every kind is a server mail
    /// (player-to-player mail is deferred: TODO(open)). The subject is a <c>UiStrings</c> id the writer chose
    /// (<c>MailSubjects</c>), never free text.
    /// </summary>
    public enum MailKind : byte
    {
        None = 0,
        /// <summary>An unsold, cancelled or expired listing's item back to its seller (4.5).</summary>
        ListingReturn = 1,
        /// <summary>A listing's proceeds to its seller, always by mail even when online (M4 decision 4).</summary>
        ListingProceeds = 2,
        /// <summary>A party roll winning or a pickup that does not fit the winner's bag (4.3).</summary>
        LootOverflow = 3,
        /// <summary>A contract's materials and gold back to the customer (5.4).</summary>
        ContractReturn = 4,
        /// <summary>A support grant from the admin panel (7.4); its claim is a faucet with reason <c>Admin</c>.</summary>
        AdminCompensation = 5,
        /// <summary>A death loot bag's items that reach their owner by mail (4.3, M4 plan decision 8).</summary>
        LootBagReturn = 6,
        /// <summary>
        /// Worn armor of the old tier that did not fit the bag when the character's tier changed (R-3.5-08, M4 decision 9b;
        /// WP-M4-13, protocol 44): sent by the character's own skill tree operation to itself.
        /// </summary>
        ArmorReturn = 7,
    }

    /// <summary>How the client renders a subject argument (never free text from the server). Append only.</summary>
    public enum MailArgKind : byte
    {
        None = 0,
        /// <summary>The value as a number.</summary>
        Number = 1,
        /// <summary>The value is an item net id, shown as the item's name.</summary>
        Item = 2,
    }

    /// <summary>
    /// Why the server refused a mail request (spec 4.5, 9.7 reason codes; WP-M4-3). Append only;
    /// <see cref="ReliableMessages.LastMailReason"/> is the highest. Names shared with other enums share their
    /// <c>ReasonCodeStrings</c> row (<c>BagFull</c>, <c>InCombat</c>, <c>GoldCapReached</c>, <c>InventoryUnavailable</c>).
    /// </summary>
    public enum MailReason : byte
    {
        None = 0,
        /// <summary>No open mail with that id in the character's mailbox.</summary>
        MailNotFound = 1,
        /// <summary>The mail was claimed already (its attachments are in the bag).</summary>
        MailAlreadyClaimed = 2,
        /// <summary>The mail passed its expiry; its attachments are destroyed.</summary>
        MailExpired = 3,
        /// <summary>The mailbox holds <c>MailSettings.mailboxCap</c> open mails (gates new mail sources; settlements still arrive).</summary>
        MailboxFull = 4,
        /// <summary>Nothing of the mail fits the Normal bag (a partial claim is not a refusal).</summary>
        BagFull = 5,
        /// <summary>A claim never interrupts combat (M4 plan risk "souls-like feel").</summary>
        InCombat = 6,
        /// <summary>Only gold was attached and it would pass the gold cap.</summary>
        GoldCapReached = 7,
        /// <summary>More mail requests than <c>MailSettings</c> allows per period (spec 7.5 rate limits).</summary>
        MailRateLimited = 8,
        /// <summary>The character's inventory or mailbox is not loaded (a request before enter world).</summary>
        InventoryUnavailable = 9,
    }

    /// <summary>A typed subject argument: the client fills <c>{Name}</c> of the subject's <c>UiStrings</c> text with it.</summary>
    [MemoryPackable]
    public sealed partial class MailArgEntry
    {
        public string Name { get; set; } = string.Empty;

        public MailArgKind Kind { get; set; }

        public long Value { get; set; }
    }

    /// <summary>One attachment as a summary (the list's icons and counts).</summary>
    [MemoryPackable]
    public sealed partial class MailAttachmentEntry
    {
        public ushort ItemNetId { get; set; }

        public byte Rarity { get; set; }

        public ushort Count { get; set; }

        public bool Stolen { get; set; }
    }

    /// <summary>One open mail of the mailbox: what the list shows.</summary>
    [MemoryPackable]
    public sealed partial class MailHeader
    {
        public long MailId { get; set; }

        public MailKind Kind { get; set; }

        /// <summary>The subject's <c>UiStrings</c> id (<c>mail.subject.*</c>).</summary>
        public string SubjectId { get; set; } = string.Empty;

        public MailArgEntry[] Args { get; set; } = Array.Empty<MailArgEntry>();

        public long Gold { get; set; }

        public long CreatedUtcMs { get; set; }

        /// <summary>When the attachments are destroyed (<c>MailSettings.expiryDays</c> after the send).</summary>
        public long ExpiresUtcMs { get; set; }

        public MailAttachmentEntry[] Items { get; set; } = Array.Empty<MailAttachmentEntry>();
    }

    /// <summary>
    /// Client to server, reliable (spec 4.5, 9.13; WP-M4-3, protocol 42): <see cref="MailId"/> 0 asks for the mailbox list
    /// (answered by a <see cref="MailList"/>), another id for one mail's content (a <see cref="MailContent"/>), both with the
    /// same <see cref="RequestId"/>. Rate limited by <c>MailSettings</c>; capped by its <c>ReliableMessageCaps</c> row.
    /// </summary>
    [MemoryPackable]
    public sealed partial class MailOpenRequest
    {
        public uint RequestId { get; set; }

        public long MailId { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-3, protocol 42): the mailbox, oldest first (the first to expire), at most
    /// <see cref="ReliableMessages.MaxMailHeaders"/>; <see cref="Total"/> counts every open mail.
    /// </summary>
    [MemoryPackable]
    public sealed partial class MailList
    {
        public uint RequestId { get; set; }

        public MailReason Reason { get; set; }

        public ushort Total { get; set; }

        public MailHeader[] Mails { get; set; } = Array.Empty<MailHeader>();
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-3, protocol 42): one mail with its attachments in full (the inventory window's
    /// slot entries, <c>Slot</c> = the attachment index), for the tooltips.
    /// </summary>
    [MemoryPackable]
    public sealed partial class MailContent
    {
        public uint RequestId { get; set; }

        public MailReason Reason { get; set; }

        public MailHeader Header { get; set; } = new MailHeader();

        public InventorySlotEntry[] Items { get; set; } = Array.Empty<InventorySlotEntry>();
    }

    /// <summary>
    /// Client to server, reliable (WP-M4-3, protocol 42): claim the mail's attachments into the Normal bag and its gold;
    /// <see cref="MailId"/> 0 claims every open mail, oldest first, up to <c>MailSettings.claimAllMax</c>. One class 1
    /// operation, answered by a <see cref="MailResult"/> only once its save committed (rule 5).
    /// </summary>
    [MemoryPackable]
    public sealed partial class MailClaimRequest
    {
        public uint RequestId { get; set; }

        public long MailId { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-3, protocol 42): the answer to a <see cref="MailClaimRequest"/>. A refusal at once;
    /// a claim only once its save committed, right before the <c>InventoryContents</c> that shows it. A partial claim is
    /// <see cref="MailReason.None"/> with what stayed in the mail counted in <see cref="ItemsLeft"/> (and the gold left when
    /// it did not fit). Append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class MailResult
    {
        public uint RequestId { get; set; }

        public MailReason Reason { get; set; }

        /// <summary>The claimed mail, 0 for claim all.</summary>
        public long MailId { get; set; }

        /// <summary>Mails emptied by this claim.</summary>
        public ushort MailsEmptied { get; set; }

        public MailAttachmentEntry[] Taken { get; set; } = Array.Empty<MailAttachmentEntry>();

        public long GoldTaken { get; set; }

        /// <summary>Units of the claimed mails that stayed (the bag was full).</summary>
        public ushort ItemsLeft { get; set; }

        public long GoldLeft { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (spec 9.3 the M badge; WP-M4-3, protocol 42): the open mails, at enter world and on every
    /// change. <see cref="Cap"/> is <c>MailSettings.mailboxCap</c>: at or over it, new mail sources are refused
    /// (<see cref="MailReason.MailboxFull"/>) while settlements still arrive.
    /// </summary>
    [MemoryPackable]
    public sealed partial class MailCount
    {
        public ushort Open { get; set; }

        public ushort Cap { get; set; }
    }

    public static partial class ReliableMessages
    {
        /// <summary>Headers a <see cref="MailList"/> carries at most (<c>MailSettings.maxListed</c> is not larger).</summary>
        public const int MaxMailHeaders = 50;

        /// <summary>Attachments a mail carries at most (<c>MailSettings.attachmentsPerMail</c> is not larger).</summary>
        public const int MaxMailAttachments = 8;

        /// <summary>Subject arguments a mail carries at most.</summary>
        public const int MaxMailArgs = 3;

        /// <summary>Bytes of a subject id (ASCII, <c>mail.subject.*</c>).</summary>
        public const int MaxMailSubjectBytes = 48;

        /// <summary>Bytes of a subject argument's name (ASCII).</summary>
        public const int MaxMailArgNameBytes = 16;

        /// <summary>Units a <see cref="MailResult"/> lists at most (claim all over its mails).</summary>
        public const int MaxMailTaken = 64;

        public const MailKind LastMailKind = MailKind.ArmorReturn;
        public const MailArgKind LastMailArgKind = MailArgKind.Item;
        public const MailReason LastMailReason = MailReason.InventoryUnavailable;

        public static bool TryRead(ReadOnlySpan<byte> message, out MailOpenRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.MailOpenRequest, out MailOpenRequest? value) || value == null || value.MailId < 0)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out MailClaimRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.MailClaimRequest, out MailClaimRequest? value) || value == null || value.MailId < 0)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out MailList list)
        {
            list = null!;
            if (!TryBody(message, ReliableMessageId.MailList, out MailList? value) || value == null || value.Reason > LastMailReason
                || value.Mails == null || value.Mails.Length > MaxMailHeaders || value.Total < value.Mails.Length)
                return false;
            foreach (var header in value.Mails)
            {
                if (!ValidMailHeader(header))
                    return false;
            }
            list = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out MailContent content)
        {
            content = null!;
            if (!TryBody(message, ReliableMessageId.MailContent, out MailContent? value) || value == null
                || value.Reason > LastMailReason || value.Items == null || value.Items.Length > MaxMailAttachments
                || (value.Reason == MailReason.None ? !ValidMailHeader(value.Header) : value.Header == null)
                || !ValidSlots(value.Items, MaxMailAttachments))
                return false;
            content = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out MailResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.MailResult, out MailResult? value) || value == null
                || value.Reason > LastMailReason || value.MailId < 0 || value.GoldTaken < 0 || value.GoldLeft < 0
                || value.Taken == null || value.Taken.Length > MaxMailTaken || !ValidAttachments(value.Taken))
                return false;
            result = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out MailCount count)
        {
            count = null!;
            if (!TryBody(message, ReliableMessageId.MailCount, out MailCount? value) || value == null)
                return false;
            count = value;
            return true;
        }

        /// <summary>A subject id or argument name: printable ASCII within its bound.</summary>
        public static bool IsMailAsciiId(string? id, int maxBytes)
        {
            if (id == null || id.Length == 0 || id.Length > maxBytes)
                return false;
            foreach (char c in id)
            {
                if (c < 0x21 || c > 0x7E)
                    return false;
            }
            return true;
        }

        private static bool ValidMailHeader(MailHeader? header)
        {
            if (header == null || header.MailId <= 0 || header.Kind == MailKind.None || header.Kind > LastMailKind
                || !IsMailAsciiId(header.SubjectId, MaxMailSubjectBytes) || header.Gold < 0
                || header.Args == null || header.Args.Length > MaxMailArgs
                || header.Items == null || header.Items.Length > MaxMailAttachments || !ValidAttachments(header.Items))
                return false;
            foreach (var arg in header.Args)
            {
                if (arg == null || arg.Kind == MailArgKind.None || arg.Kind > LastMailArgKind
                    || !IsMailAsciiId(arg.Name, MaxMailArgNameBytes))
                    return false;
            }
            return true;
        }

        private static bool ValidAttachments(MailAttachmentEntry[] items)
        {
            foreach (var item in items)
            {
                if (item == null || item.ItemNetId == 0 || item.Count == 0)
                    return false;
            }
            return true;
        }
    }
}
