#nullable enable
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the mail window (spec 9.1: no user-facing text in code; spec 4.5, 9.3, 9.13; WP-M4-3). An EditMode
    /// test checks that each constant has a row; the subjects are the server's <c>mail.subject.*</c> ids (<see cref="Subjects"/>).
    /// </summary>
    public static class MailTextIds
    {
        public const string Title = "mail.title";
        public const string Close = "mail.close";
        /// <summary>The close button's label for screen readers (spec 9.8).</summary>
        public const string CloseLabel = "mail.closeLabel";
        public const string Empty = "mail.empty";
        /// <summary>{open} of {cap} mails.</summary>
        public const string Count = "mail.count";
        /// <summary>At or over the cap: new mail sources wait (settlements still arrive).</summary>
        public const string Full = "mail.full";
        /// <summary>{count} more mails past the listed ones.</summary>
        public const string More = "mail.more";
        /// <summary>{gold} gold.</summary>
        public const string Gold = "mail.gold";
        /// <summary>Expires in {days} d.</summary>
        public const string Expires = "mail.expires";
        /// <summary>Expires in {hours} h (the last day).</summary>
        public const string ExpiresHours = "mail.expiresHours";
        /// <summary>{name} x{count}.</summary>
        public const string Attachment = "mail.attachment";
        public const string Hint = "mail.hint";
        public const string Claim = "mail.claim";
        public const string ClaimAll = "mail.claimAll";
        /// <summary>You took {what} from your mail.</summary>
        public const string ToastClaimed = "mail.toast.claimed";
        public const string ToastPartial = "mail.toast.partial";
        /// <summary>{list} and {count} more (the claim toast past <c>MailSettings.toastItemsShown</c> items).</summary>
        public const string ToastMore = "mail.toast.more";
        /// <summary>{item} ({details}): an attachment with its rarity, the stolen mark, quality, gems.</summary>
        public const string ItemDetails = "mail.itemDetails";
        /// <summary>The list was refused (rate limit, the mailbox loading); asked for again.</summary>
        public const string Unavailable = "mail.unavailable";

        /// <summary>The subjects the server writes (<c>MailSubjects</c> on the server), one per <c>MailKind</c>.</summary>
        public static readonly string[] Subjects =
        {
            "mail.subject.listingReturn", "mail.subject.listingProceeds", "mail.subject.lootOverflow", "mail.subject.contractReturn",
            "mail.subject.adminCompensation", "mail.subject.lootBagReturn", "mail.subject.armorReturn", "mail.subject.listingPurchase",
        };

        public static readonly string[] All =
        {
            Title, Close, CloseLabel, Empty, Count, Full, More, Gold, Expires, ExpiresHours, Attachment, Hint, Claim, ClaimAll,
            ToastClaimed, ToastPartial, ToastMore, ItemDetails, Unavailable,
        };
    }

    /// <summary>
    /// The config tables the mail window reads (spec 4.5, 9.13; WP-M4-3), from a loaded and validated <see cref="ConfigSet"/>:
    /// the toggle key (<c>Hotkeys</c> row <c>toggleMail</c>), the items by net id for the attachment names, the inventory's
    /// rarity names and gem rules for the attachment details, and the client display numbers of <c>MailSettings</c>. The window only
    /// shows mail; the server owns every mailbox.
    /// </summary>
    public sealed class MailConfig
    {
        private readonly Dictionary<int, ItemDef> _items = new Dictionary<int, ItemDef>();
        private readonly HashSet<int> _mailboxes = new HashSet<int>();

        public MailConfig(string toggleBinding, InventoryConfig inventory, MailSettings? settings)
        {
            ToggleBinding = toggleBinding;
            Inventory = inventory;
            ToastItemsShown = settings?.ToastItemsShown ?? 3;
            WindowRefreshMs = settings?.WindowRefreshMs ?? 60_000;
            ListRetryMs = settings?.PeriodMs ?? 2_000;
        }

        /// <summary>Rarity names and gem rules for the attachment details (the inventory tooltip's).</summary>
        public InventoryConfig Inventory { get; }

        /// <summary>Items the claim toast names before "and N more" (<c>MailSettings.toastItemsShown</c>).</summary>
        public int ToastItemsShown { get; }

        /// <summary>How often an open window redraws its expiry times (<c>MailSettings.windowRefreshMs</c>).</summary>
        public int WindowRefreshMs { get; }

        /// <summary>A refused list is asked for again once, one rate-limit period later (<c>MailSettings.periodMs</c>).</summary>
        public int ListRetryMs { get; }

        /// <summary>The <c>toggleMail</c> control path; empty when the table has no row.</summary>
        public string ToggleBinding { get; }

        /// <summary>The item of a net id; null for one the tables lack.</summary>
        public ItemDef? Item(int netId) => _items.TryGetValue(netId, out var item) ? item : null;

        public void AddItem(ItemDef item) => _items[item.NetId] = item;

        /// <summary>Whether the NPC of a <c>NpcDef.netId</c> is a mailbox (it offers <c>Mail</c>; WP-M4-14, R-4.5-03).</summary>
        public bool IsMailbox(int npcNetId) => _mailboxes.Contains(npcNetId);

        public void AddMailbox(int npcNetId) => _mailboxes.Add(npcNetId);

        /// <param name="inventory">The HUD's inventory config; null builds one from the tables.</param>
        public static MailConfig From(ConfigSet set, InventoryConfig? inventory = null)
        {
            var config = new MailConfig(Hotkeys.Find(set, Hotkeys.ToggleMail)?.Binding ?? string.Empty, inventory ?? InventoryConfig.From(set),
                MailSettings.Find(set));
            if (set.TryGet(typeof(ItemDef), out var items))
            {
                foreach (var row in items.Rows)
                    config.AddItem((ItemDef)row);
            }
            if (set.TryGet(typeof(NpcDef), out var npcs))
            {
                foreach (var row in npcs.Rows)
                {
                    if (((NpcDef)row).Offers(NpcInteraction.Mail))
                        config.AddMailbox(((NpcDef)row).NetId);
                }
            }
            return config;
        }
    }
}
