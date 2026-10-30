#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the outpost and vendor windows, the NPC prompt and the gold display (spec 9.1: no
    /// user-facing text in code; spec 9.13 M2; WP-M2-4). An EditMode test checks that each constant has a row.
    /// </summary>
    public static class OutpostTextIds
    {
        public const string SellTitle = "outpost.title";
        public const string VendorTitle = "vendor.title";
        public const string Close = "outpost.close";
        /// <summary>The close button's label for screen readers (spec 9.8).</summary>
        public const string CloseLabel = "outpost.closeLabel";
        public const string Empty = "outpost.empty";
        public const string Stack = "outpost.stack";
        public const string Less = "outpost.less";
        public const string More = "outpost.more";
        public const string AllUnits = "outpost.all";
        public const string Count = "outpost.count";
        public const string QuotePending = "outpost.quotePending";
        public const string Quote = "outpost.quote";
        public const string Sell = "outpost.sell";
        public const string Sold = "outpost.sold";
        public const string StockRow = "vendor.row";
        public const string Total = "vendor.total";
        public const string Buy = "vendor.buy";
        public const string Bought = "vendor.bought";
        public const string Gold = "hud.gold";
        public const string PromptOutpost = "hud.npc.prompt.outpost";
        public const string PromptVendor = "hud.npc.prompt.vendor";
        /// <summary>The job master's prompt (WP-M3-1).</summary>
        public const string PromptJobMaster = "hud.npc.prompt.jobMaster";

        public static readonly string[] All =
        {
            SellTitle, VendorTitle, Close, CloseLabel, Empty, Stack, Less, More, AllUnits, Count, QuotePending, Quote, Sell, Sold,
            StockRow, Total, Buy, Bought, Gold, PromptOutpost, PromptVendor, PromptJobMaster,
        };

        /// <summary>The prompt text of an NPC kind.</summary>
        public static string PromptOf(NpcKind kind) => kind switch
        {
            NpcKind.Vendor => PromptVendor,
            NpcKind.JobMaster => PromptJobMaster,
            _ => PromptOutpost,
        };
    }

    /// <summary>One item a vendor sells, as its window shows it (WP-M2-4).</summary>
    public sealed class StockEntry
    {
        public StockEntry(NpcVendorStock row, ItemInfo item)
        {
            Row = row;
            Item = item;
        }

        public NpcVendorStock Row { get; }

        public ItemInfo Item { get; }

        public ushort ItemNetId => (ushort)Item.Def.NetId;

        public int PriceGold => Row.PriceGold;

        /// <summary>A purchase is at most one stack.</summary>
        public int MaxCount => Item.Def.MaxStack;
    }

    /// <summary>
    /// The config tables the outpost and vendor windows read (spec 4.2, 9.13; WP-M2-4), from a loaded and validated
    /// <see cref="ConfigSet"/>: NPCs by net id, each vendor's stock in table order, the interaction key (<c>Hotkeys</c>
    /// row <c>interact</c>), and the items through the inventory window's config (names, icons).
    /// </summary>
    public sealed class OutpostConfig
    {
        private readonly Dictionary<ushort, NpcDef> _npcs = new Dictionary<ushort, NpcDef>();
        private readonly Dictionary<string, List<StockEntry>> _stock = new Dictionary<string, List<StockEntry>>(StringComparer.Ordinal);

        public OutpostConfig(InventoryConfig inventory, string interactBinding)
        {
            Inventory = inventory;
            InteractBinding = interactBinding;
        }

        public InventoryConfig Inventory { get; }

        /// <summary>Input System path of the <c>Hotkeys</c> row <c>interact</c>.</summary>
        public string InteractBinding { get; }

        public NpcDef? Npc(ushort netId) => _npcs.TryGetValue(netId, out var npc) ? npc : null;

        /// <summary>What the vendor <paramref name="npcId"/> sells, in table order; empty for an NPC that sells nothing.</summary>
        public IReadOnlyList<StockEntry> Stock(string npcId) =>
            _stock.TryGetValue(npcId, out var rows) ? rows : (IReadOnlyList<StockEntry>)Array.Empty<StockEntry>();

        /// <summary>Whether outposts take this stack (R-4.1-01 sell-only, R-4.4-02 never stolen); the server checks again.</summary>
        public bool IsSellable(ushort itemNetId, bool stolen) =>
            !stolen && Inventory.Item(itemNetId) is { } item && item.Def.Category == ItemCategory.SellOnly;

        public static OutpostConfig From(ConfigSet set, InventoryConfig inventory)
        {
            var interact = Hotkeys.Find(set, Hotkeys.Interact);
            var config = new OutpostConfig(inventory, interact?.Binding ?? "");
            foreach (var npc in Rows<NpcDef>(set))
                config._npcs[(ushort)npc.NetId] = npc;
            var items = new Dictionary<string, ItemDef>(StringComparer.Ordinal);
            foreach (var item in Rows<ItemDef>(set))
                items[item.Id] = item;
            foreach (var row in Rows<NpcVendorStock>(set))
            {
                if (!items.TryGetValue(row.ItemId, out var def) || inventory.Item((ushort)def.NetId) is not { } info)
                    continue;
                if (!config._stock.TryGetValue(row.NpcId, out var list))
                    config._stock[row.NpcId] = list = new List<StockEntry>();
                list.Add(new StockEntry(row, info));
            }
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
