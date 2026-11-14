using GameServer.Persistence;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Items.Mail;

/// <summary>The <c>UiStrings</c> ids of the system mail subjects (spec 4.5 M4 mail; the client fills their typed arguments).</summary>
public static class MailSubjects
{
    public const string Prefix = "mail.subject.";
    public const string ListingReturn = Prefix + "listingReturn";
    public const string ListingProceeds = Prefix + "listingProceeds";
    public const string LootOverflow = Prefix + "lootOverflow";
    public const string ContractReturn = Prefix + "contractReturn";
    public const string AdminCompensation = Prefix + "adminCompensation";
    public const string LootBagReturn = Prefix + "lootBagReturn";
    public const string ArmorReturn = Prefix + "armorReturn";

    /// <summary>Every subject (the client's strings cover them all).</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        ListingReturn, ListingProceeds, LootOverflow, ContractReturn, AdminCompensation, LootBagReturn, ArmorReturn,
    };

    /// <summary>The default subject of a kind.</summary>
    public static string For(MailKind kind) => kind switch
    {
        MailKind.ListingReturn => ListingReturn,
        MailKind.ListingProceeds => ListingProceeds,
        MailKind.LootOverflow => LootOverflow,
        MailKind.ContractReturn => ContractReturn,
        MailKind.AdminCompensation => AdminCompensation,
        MailKind.LootBagReturn => LootBagReturn,
        MailKind.ArmorReturn => ArmorReturn,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>
/// The mail rules of a world or a tool (spec 4.5 M4 mail, WP-M4-3): <c>MailSettings</c> and the item tables, and the check of
/// a draft before it is written (<see cref="Check"/>). Built once at start (the settings reload on restart).
/// </summary>
public sealed class MailRules(MailSettings settings, ItemRules items, long maxGold = long.MaxValue)
{
    /// <summary>
    /// The most gold one mail carries: the gold cap of a character (<c>EconomySettings.maxGold</c>). A claim takes a mail's gold
    /// all or nothing, so a mail above it could never be claimed (review of PR #171).
    /// </summary>
    public long MaxGold { get; } = maxGold;

    /// <summary>Longest operation key a send may carry.</summary>
    public const int MaxOpKeyChars = 200;

    public MailSettings Settings { get; } = settings;
    public ItemRules Items { get; } = items;

    public static MailRules? From(ConfigSet set, ItemRules? items) =>
        MailSettings.Find(set) is { } settings && items != null
            ? new MailRules(settings, items, set.TryGet(typeof(EconomySettings), out var economy) && economy.Rows.Count == 1 ? ((EconomySettings)economy.Rows[0]).MaxGold : long.MaxValue)
            : null;

    public static MailRules? From(MailSettings? settings, ItemRules? items, long maxGold = long.MaxValue) =>
        settings != null && items != null ? new MailRules(settings, items, maxGold) : null;

    /// <summary>When a mail sent at <paramref name="createdUtcMs"/> expires.</summary>
    public long ExpiresAt(long createdUtcMs) => createdUtcMs + Settings.ExpiryDurationMs();

    /// <summary>
    /// Why <paramref name="draft"/> cannot be written, or null when it can: a real recipient and kind, a <c>mail.subject.*</c>
    /// id and at most <see cref="ReliableMessages.MaxMailArgs"/> typed arguments, an operation key, gold not negative, at least
    /// one attachment or some gold, at most <c>MailSettings.attachmentsPerMail</c> attachments, each a known item with a count
    /// within its stack (an instance one at a time) and properties the tables can represent.
    /// </summary>
    public string? Check(MailDraft draft)
    {
        if (draft.ToCharacterId <= 0)
            return "no recipient";
        if (draft.Kind == MailKind.None || draft.Kind > ReliableMessages.LastMailKind)
            return $"unknown kind {draft.Kind}";
        if (!ReliableMessages.IsMailAsciiId(draft.SubjectId, ReliableMessages.MaxMailSubjectBytes)
            || !draft.SubjectId.StartsWith(MailSubjects.Prefix, StringComparison.Ordinal))
            return $"subject '{draft.SubjectId}' is not a {MailSubjects.Prefix}* id";
        if (draft.Args.Count > ReliableMessages.MaxMailArgs)
            return "too many subject arguments";
        foreach (var arg in draft.Args)
        {
            if (!ReliableMessages.IsMailAsciiId(arg.Name, ReliableMessages.MaxMailArgNameBytes) || arg.Kind == MailArgKind.None
                || arg.Kind > ReliableMessages.LastMailArgKind)
                return $"subject argument '{arg.Name}' is not valid";
        }
        if (string.IsNullOrEmpty(draft.OpKey) || draft.OpKey.Length > MaxOpKeyChars)
            return "no operation key";
        if (draft.Gold < 0)
            return "negative gold";
        if (draft.Gold > MaxGold)
            return $"gold above the gold cap of {MaxGold}";
        if (draft.Items.Count == 0 && draft.Gold == 0)
            return "nothing attached";
        if (draft.Items.Count > Settings.AttachmentsPerMail)
            return $"more than {Settings.AttachmentsPerMail} attachments";
        foreach (var attachment in draft.Items)
        {
            if (!Items.TryGetItem(attachment.DefId, out var item))
                return $"unknown item '{attachment.DefId}'";
            if (attachment.Count <= 0)
                return $"{attachment.DefId}: count {attachment.Count}";
            if (ItemCategories.IsInstance(item.Category))
            {
                if (attachment.Count != 1)
                    return $"{attachment.DefId}: an instance is attached one at a time";
                if (attachment.PropertiesJson != null && (!ItemProperties.TryParse(attachment.PropertiesJson, out var properties, out _)
                                                          || !Items.Represents(properties)))
                    return $"{attachment.DefId}: properties the tables cannot represent";
            }
            else
            {
                if (attachment.InstanceId != 0 || attachment.PropertiesJson != null)
                    return $"{attachment.DefId}: a stackable item has no instance";
                if (attachment.Count > item.MaxStack)
                    return $"{attachment.DefId}: count above its stack of {item.MaxStack}";
            }
            if (Items.ReceiverOf(item.Category) == null)
                return $"{attachment.DefId}: no container takes new items of its category";
        }
        return null;
    }
}
