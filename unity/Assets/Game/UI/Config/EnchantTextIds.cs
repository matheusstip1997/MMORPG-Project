#nullable enable
namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the enchant tab and the enchant lines of the item tooltip (spec 9.1: no user-facing text in code;
    /// spec 5.3 R-5.3-04, 9.13; WP-M3-8). An EditMode test checks that each constant has a row; item names are their
    /// <c>ItemDef.nameId</c>.
    /// </summary>
    public static class EnchantTextIds
    {
        public const string TabForge = "forge.tab.forge";
        public const string TabEnchant = "forge.tab.enchant";
        public const string Empty = "enchant.empty";
        /// <summary>{name} +{level}.</summary>
        public const string Item = "enchant.item";
        /// <summary>{name} +{level} (max).</summary>
        public const string ItemMaxed = "enchant.itemMaxed";
        /// <summary>{stone}: {held} held, {count} per try.</summary>
        public const string Stones = "enchant.stones";
        /// <summary>+1 {plus}%, same {same}%, -1 {minus}%.</summary>
        public const string Odds = "enchant.odds";
        public const string Button = "enchant.button";
        public const string Waiting = "enchant.waiting";
        /// <summary>{name} rose to +{level}.</summary>
        public const string ToastUp = "enchant.toast.up";
        /// <summary>{name} stayed at +{level}.</summary>
        public const string ToastSame = "enchant.toast.same";
        /// <summary>{name} fell to +{level}.</summary>
        public const string ToastDown = "enchant.toast.down";
        /// <summary>The tooltip's name of an enchanted item: {name} +{level}.</summary>
        public const string TooltipName = "inv.tooltip.enchantName";
        /// <summary>The tooltip's enchant line: Enchant +{level}: stats +{percent}%.</summary>
        public const string TooltipLine = "inv.tooltip.enchant";

        public static readonly string[] All =
        {
            TabForge, TabEnchant, Empty, Item, ItemMaxed, Stones, Odds, Button, Waiting, ToastUp, ToastSame, ToastDown, TooltipName,
            TooltipLine,
        };
    }
}
