#nullable enable
using Shared.Data;
using Shared.Data.Tables;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the stall window and prompt (spec 9.1: no user-facing text in code; spec 4.5, 9.13; WP-M4-8). An
    /// EditMode test checks that each constant has a row.
    /// </summary>
    public static class StallTextIds
    {
        /// <summary>Stall of {name} (a buyer looking at it).</summary>
        public const string TitleBrowse = "stall.title.browse";
        public const string TitleManage = "stall.title.manage";
        public const string TitleOpen = "stall.title.open";
        public const string Close = "stall.close";
        /// <summary>The close button's label for screen readers (spec 9.8).</summary>
        public const string CloseLabel = "stall.closeLabel";
        /// <summary>Closes in {hours} h.</summary>
        public const string ClosesIn = "stall.closesIn";
        /// <summary>{count} of {cap} listings.</summary>
        public const string Count = "stall.count";
        public const string Empty = "stall.empty";
        /// <summary>{name} x{count}   {price} gold each.</summary>
        public const string Row = "stall.row";
        public const string ItemCount = "stall.itemCount";
        public const string ItemDetails = "stall.itemDetails";
        public const string Buy = "stall.buy";
        public const string Select = "stall.select";
        public const string Total = "stall.total";
        public const string Cancel = "stall.cancel";
        public const string CloseStall = "stall.closeStall";
        /// <summary>Pick a stack from your bag, then a count and a unit price.</summary>
        public const string PickHint = "stall.pickHint";
        public const string CountLabel = "stall.countLabel";
        public const string PriceLabel = "stall.priceLabel";
        public const string OpenStall = "stall.openStall";
        public const string Add = "stall.add";
        /// <summary>The stall closed (its time, sold out, or its owner).</summary>
        public const string Closed = "stall.closed";
        /// <summary>Listed {name} x{count} (fee {fee} gold).</summary>
        public const string ToastListed = "stall.toast.listed";
        /// <summary>Bought {name} x{count} for {price} gold.</summary>
        public const string ToastBought = "stall.toast.bought";
        public const string ToastCancelled = "stall.toast.cancelled";
        public const string ToastClosed = "stall.toast.closed";
        /// <summary>Prompts at the stall or the free place in reach; {key} is the interaction key.</summary>
        public const string PromptOpen = "hud.stall.prompt.open";
        public const string PromptBrowse = "hud.stall.prompt.browse";
        public const string PromptManage = "hud.stall.prompt.manage";

        public static readonly string[] All =
        {
            TitleBrowse, TitleManage, TitleOpen, Close, CloseLabel, ClosesIn, Count, Empty, Row, ItemCount, ItemDetails, Buy, Select,
            Total, Cancel, CloseStall, PickHint, CountLabel, PriceLabel, OpenStall, Add, Closed, ToastListed, ToastBought, ToastCancelled,
            ToastClosed, PromptOpen, PromptBrowse, PromptManage,
        };
    }

    /// <summary>
    /// The config tables the stall window reads (spec 4.5, 9.13; WP-M4-8): the interaction key (<c>Hotkeys</c> row <c>interact</c>),
    /// the inventory's items, rarity names and Normal bag code for the rows and the bag picker, and the stall cap of
    /// <c>StallSettings</c> for the window's count. Display only: the server owns every stall.
    /// </summary>
    public sealed class StallConfig
    {
        public StallConfig(string interactBinding, InventoryConfig inventory, StallSettings? settings)
        {
            InteractBinding = interactBinding;
            Inventory = inventory;
            ListingsPerStall = settings?.ListingsPerStall ?? 8;
        }

        /// <summary>The <c>interact</c> control path; empty when the table has no row.</summary>
        public string InteractBinding { get; }

        public InventoryConfig Inventory { get; }

        /// <summary>Listings one stall holds (<c>StallSettings.listingsPerStall</c>).</summary>
        public int ListingsPerStall { get; }

        /// <param name="inventory">The HUD's inventory config; null builds one from the tables.</param>
        public static StallConfig From(ConfigSet set, InventoryConfig? inventory = null) =>
            new StallConfig(Hotkeys.Find(set, Hotkeys.Interact)?.Binding ?? string.Empty, inventory ?? InventoryConfig.From(set),
                StallSettings.Find(set));
    }
}
