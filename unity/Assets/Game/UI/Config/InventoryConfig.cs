#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the inventory window (spec 9.1: no user-facing text in code; spec 9.13). An EditMode test
    /// checks that each constant has a row. Rarity and category names use the prefixes of
    /// <c>Shared.Data.Tables.UiStringCoverage</c>, which the DataValidator checks.
    /// </summary>
    public static class InventoryTextIds
    {
        public const string Title = "inv.title";
        public const string Close = "inv.close";
        /// <summary>The close button's label for screen readers (spec 9.8: icon-only controls have a text label).</summary>
        public const string CloseLabel = "inv.closeLabel";
        public const string Count = "inv.count";
        public const string TooltipRarity = "inv.tooltip.rarity";
        public const string TooltipCategory = "inv.tooltip.category";
        public const string TooltipCount = "inv.tooltip.count";
        public const string TooltipStolen = "inv.tooltip.stolen";
    }

    /// <summary>An item as the window shows it: its definition and its icon row (null if the validator was skipped).</summary>
    public sealed class ItemInfo
    {
        public ItemInfo(ItemDef def, ItemIconRegistry? icon)
        {
            Def = def;
            Icon = icon;
        }

        public ItemDef Def { get; }

        public ItemIconRegistry? Icon { get; }
    }

    /// <summary>
    /// The config tables the inventory window reads (spec 9.13, WP16), from a loaded and validated
    /// <see cref="ConfigSet"/>: its settings, the Normal bag's slot count, the toggle key, items by net id with their icon
    /// rows, and rarity names by tier.
    /// </summary>
    public sealed class InventoryConfig
    {
        private readonly Dictionary<int, ItemInfo> _items = new Dictionary<int, ItemInfo>();
        private readonly Dictionary<int, string> _rarityIds = new Dictionary<int, string>();

        public InventoryConfig(InventoryWindowSettings settings, int bagSlots, string toggleBinding)
        {
            Settings = settings;
            BagSlots = bagSlots;
            ToggleBinding = toggleBinding;
        }

        public InventoryWindowSettings Settings { get; }

        /// <summary><c>ContainerDef.slots</c> of the Normal bag (the server's contents say again).</summary>
        public int BagSlots { get; }

        /// <summary>Input System path of the <c>Hotkeys</c> row <c>toggleInventory</c>.</summary>
        public string ToggleBinding { get; }

        public ItemInfo? Item(ushort netId) => _items.TryGetValue(netId, out var info) ? info : null;

        /// <summary>Most units one stack holds; 1 for an unknown item (the preview then never merges it).</summary>
        public int MaxStack(ushort netId) => _items.TryGetValue(netId, out var info) ? info.Def.MaxStack : 1;

        /// <summary>The <c>UiStrings</c> id of a rarity tier's name, or empty for an unknown tier.</summary>
        public string RarityTextId(byte tier) =>
            _rarityIds.TryGetValue(tier, out var id) ? UiStringCoverage.RarityId(id) : "";

        public static InventoryConfig From(ConfigSet set)
        {
            var bag = ContainerDef.Find(set, ContainerDef.NormalBag);
            var toggle = Hotkeys.Find(set, Hotkeys.ToggleInventory);
            var config = new InventoryConfig(set.Single<InventoryWindowSettings>(), bag?.Slots ?? 1, toggle?.Binding ?? "");
            var icons = new Dictionary<string, ItemIconRegistry>(StringComparer.Ordinal);
            foreach (var icon in Rows<ItemIconRegistry>(set))
                icons[icon.Id] = icon;
            foreach (var item in Rows<ItemDef>(set))
            {
                icons.TryGetValue(item.Id, out var icon);
                config._items[item.NetId] = new ItemInfo(item, icon);
            }
            foreach (var rarity in Rows<RarityDef>(set))
                config._rarityIds[rarity.Tier] = rarity.Id;
            return config;
        }

        private static List<T> Rows<T>(ConfigSet set)
        {
            var rows = new List<T>();
            if (set.TryGet(typeof(T), out var table))
            {
                foreach (var row in table.Rows)
                    rows.Add((T)row);
            }
            return rows;
        }
    }
}
