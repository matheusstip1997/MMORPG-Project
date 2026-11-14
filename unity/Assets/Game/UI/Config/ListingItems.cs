#nullable enable
using System;
using System.Collections.Generic;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.UI.Config
{
    /// <summary>
    /// What the auction and stall windows share about a listed item (spec 4.5, 9.13; WP-M4-7, WP-M4-8): the bag stacks a player may
    /// list, and an item's text on a row (the name with its enchant level, the count, the rarity above common and the quality), so a
    /// +7 sword of quality 80 never reads like a +0 of quality 20. Each window passes its own <c>UiStrings</c> ids for the count and
    /// details texts. Display only: the server checks every listing rule again.
    /// </summary>
    public static class ListingItems
    {
        /// <summary>The item categories the listing model takes (the others are sold to an NPC only).</summary>
        public static readonly ItemCategory[] Categories =
        {
            ItemCategory.Material, ItemCategory.Equipment, ItemCategory.Consumable, ItemCategory.MoldOrTool,
        };

        /// <summary>
        /// The Normal bag's stacks a player may list: not reserved (a trade, a forge, another listing holds them), tradable, not
        /// stolen or tainted, of a listed category (never a sell-only one).
        /// </summary>
        public static List<InventorySlotEntry> Sellable(InventoryConfig inventory, InventoryContents? contents)
        {
            var result = new List<InventorySlotEntry>();
            if (contents == null)
                return result;
            foreach (var entry in contents.Slots)
            {
                if (entry.ItemNetId == 0 || entry.Count == 0 || entry.Reserved || entry.NonTradable || entry.Stolen || entry.Tainted)
                    continue;
                var item = inventory.Item(entry.ItemNetId);
                if (item == null || Array.IndexOf(Categories, item.Def.Category) < 0)
                    continue;
                result.Add(entry);
            }
            return result;
        }

        /// <summary>"Demon Ash x4" through <paramref name="countId"/> (the item's <c>UiStrings</c> name; its net id when unknown).</summary>
        public static string ItemCount(InventoryConfig inventory, UiText text, string countId, int itemNetId, int count)
        {
            var item = inventory.Item((ushort)itemNetId);
            string name = item == null ? "#" + NumberText.Of(itemNetId) : text.Get(item.Def.NameId);
            return text.Get(countId, new TextArg("name", name), new TextArg("count", NumberText.Of(count)));
        }

        /// <summary>
        /// A row's item: "Forged Sword +7 x1 (Rare, quality 80)" through <paramref name="countId"/> and <paramref name="detailsId"/>,
        /// with the enchant level on the name, the rarity above common and the quality (the inventory tooltip's texts).
        /// </summary>
        public static string ItemText(InventoryConfig inventory, UiText text, string countId, string detailsId, InventorySlotEntry item)
        {
            string line = ItemCount(inventory, text, countId, item.ItemNetId, item.Count);
            if (item.Enchant > 0 && inventory.Item(item.ItemNetId) is { } info)
            {
                string name = text.Get(EnchantTextIds.TooltipName, new TextArg("name", text.Get(info.Def.NameId)),
                    new TextArg("level", NumberText.Of(item.Enchant)));
                line = text.Get(countId, new TextArg("name", name), new TextArg("count", NumberText.Of(item.Count)));
            }
            var details = new List<string>();
            if (item.Rarity > 0)
            {
                string rarityId = inventory.RarityTextId(item.Rarity);
                details.Add(rarityId.Length > 0 ? text.Get(rarityId) : NumberText.Of(item.Rarity));
            }
            if (item.Quality > 0)
                details.Add(text.Get(InventoryTextIds.TooltipQuality, new TextArg("quality", NumberText.Of(item.Quality))));
            return details.Count == 0 ? line : text.Get(detailsId, new TextArg("item", line), new TextArg("details", string.Join(", ", details)));
        }
    }
}
