#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Items;

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
        public const string TooltipMinLevel = "inv.tooltip.minLevel";
        /// <summary>WP-M2-11 item stats in the tooltip: a flat bonus <c>{stat} {value}</c>, a percent one, the weapon type, the
        /// equipment slots it is worn on, and a bound (non-tradable) item; the split bar <c>{count}</c> of <c>{stack}</c>.</summary>
        public const string TooltipStat = "inv.tooltip.stat";
        public const string TooltipStatPercent = "inv.tooltip.statPercent";
        public const string TooltipWeapon = "inv.tooltip.weapon";
        public const string TooltipSlots = "inv.tooltip.slots";
        public const string TooltipBound = "inv.tooltip.bound";
        /// <summary>WP-M3-2 crafted items: <c>{quality}</c>, the crafter line (its name waits for an <c>EntityIdentity</c>,
        /// rule 8), a tainted item (R-4.4-03) and a reserved one (spec 4.1 reservations).</summary>
        public const string TooltipQuality = "inv.tooltip.quality";
        public const string TooltipCrafted = "inv.tooltip.crafted";
        public const string TooltipTainted = "inv.tooltip.tainted";
        public const string TooltipReserved = "inv.tooltip.reserved";
        public const string Split = "inv.split";
        public const string SplitLess = "inv.split.less";
        public const string SplitMore = "inv.split.more";
        public const string SplitCancel = "inv.split.cancel";
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
    /// The config tables the inventory window reads (spec 9.13, WP16; containers and equipment WP-M2-1), from a loaded and
    /// validated <see cref="ConfigSet"/>: its settings, the containers (<c>ContainerDef</c>, code order) and equipment slots
    /// (<c>EquipSlotDef</c>), the toggle key, items by net id with their icon rows and what the shared preview rules need
    /// of them (<see cref="IItemRuleLookup"/>), and rarity names by tier.
    /// </summary>
    public sealed class InventoryConfig : IItemRuleLookup
    {
        private readonly Dictionary<int, ItemRuleInfo> _ruleInfo = new Dictionary<int, ItemRuleInfo>();
        private readonly List<ContainerRule> _containers = new List<ContainerRule>();
        private readonly Dictionary<int, string> _containerIds = new Dictionary<int, string>();
        private readonly List<string> _equipSlotIds = new List<string>();
        private readonly Dictionary<int, ItemInfo> _items = new Dictionary<int, ItemInfo>();
        private readonly Dictionary<int, string> _rarityIds = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _rarityStatPercent = new Dictionary<int, int>();
        private readonly Dictionary<int, List<ItemStatBonus>> _bonuses = new Dictionary<int, List<ItemStatBonus>>();
        private readonly List<string> _statIds = new List<string>();

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

        /// <summary>Every container in code order, as rules of the shared preview.</summary>
        public IReadOnlyList<ContainerRule> Containers => _containers;

        /// <summary>The Normal bag's <c>ContainerDef.code</c> (the first tab).</summary>
        public byte NormalBagCode { get; private set; } = 1;

        /// <summary>The <c>UiStrings</c> id of a container's tab name (<c>inv.container.&lt;id&gt;</c>), or empty for an unknown code.</summary>
        public string ContainerTextId(byte code) =>
            _containerIds.TryGetValue(code, out var id) ? UiStringCoverage.ContainerId(id) : "";

        /// <summary>The <c>UiStrings</c> id of an equipment slot's name (<c>inv.equipSlot.&lt;id&gt;</c>), by its code.</summary>
        public string EquipSlotTextId(int code) =>
            code >= 0 && code < _equipSlotIds.Count ? UiStringCoverage.EquipSlotId(_equipSlotIds[code]) : "";

        bool IItemRuleLookup.TryGetItem(ushort itemNetId, out ItemRuleInfo info) => _ruleInfo.TryGetValue(itemNetId, out info);

        public ItemInfo? Item(ushort netId) => _items.TryGetValue(netId, out var info) ? info : null;

        /// <summary>Most units one stack holds; 1 for an unknown item (the preview then never merges it).</summary>
        public int MaxStack(ushort netId) => _items.TryGetValue(netId, out var info) ? info.Def.MaxStack : 1;

        /// <summary>
        /// An item's stat bonuses at a rarity tier (WP-M2-11 tooltips): its <c>ItemStatBonusDef</c> rows scaled by
        /// <c>RarityDef.statPercent</c> with the shared <see cref="ItemStatBonus.Scale"/>, the numbers the server's gear stat
        /// source applies (spec 4.1 M2 item model). Empty for an item without bonuses.
        /// </summary>
        public IReadOnlyList<ItemStatBonus> Bonuses(ushort netId, byte rarity)
        {
            if (!_bonuses.TryGetValue(netId, out var rows))
                return Array.Empty<ItemStatBonus>();
            int percent = _rarityStatPercent.TryGetValue(rarity, out int p) ? p : 100;
            var scaled = new ItemStatBonus[rows.Count];
            for (int i = 0; i < rows.Count; i++)
                scaled[i] = rows[i].Scale(percent);
            return scaled;
        }

        /// <summary>
        /// A crafted instance's stats snapshot as the server sent it (protocol 26: stat indexes in <c>StatDef</c> file order),
        /// shown instead of the item's own list (spec 4.1 M3 item model). Lines naming an unknown stat are left out.
        /// </summary>
        public IReadOnlyList<ItemStatBonus> CraftedBonuses(IReadOnlyList<Shared.Protocol.CraftedStatEntry> lines)
        {
            var bonuses = new List<ItemStatBonus>(lines.Count);
            foreach (var line in lines)
            {
                if (line.Stat < _statIds.Count)
                    bonuses.Add(new ItemStatBonus(_statIds[line.Stat], line.Flat, line.Percent));
            }
            return bonuses;
        }

        /// <summary>The <c>UiStrings</c> ids of the equipment slots an item is worn on (<c>inv.equipSlot.&lt;id&gt;</c>), in code order.</summary>
        public IEnumerable<string> EquipSlotTextIds(ushort netId)
        {
            if (!_ruleInfo.TryGetValue(netId, out var info))
                yield break;
            for (int code = 0; code < _equipSlotIds.Count; code++)
            {
                if ((info.EquipSlotMask & (1 << code)) != 0)
                    yield return EquipSlotTextId(code);
            }
        }

        /// <summary>The <c>UiStrings</c> id of a rarity tier's name, or empty for an unknown tier.</summary>
        public string RarityTextId(byte tier) =>
            _rarityIds.TryGetValue(tier, out var id) ? UiStringCoverage.RarityId(id) : "";

        public static InventoryConfig From(ConfigSet set)
        {
            var bag = ContainerDef.Find(set, ContainerDef.NormalBag);
            var toggle = Hotkeys.Find(set, Hotkeys.ToggleInventory);
            var config = new InventoryConfig(set.Single<InventoryWindowSettings>(), bag?.Slots ?? 1, toggle?.Binding ?? "");
            if (bag != null)
                config.NormalBagCode = (byte)bag.Code;
            var slots = Rows<EquipSlotDef>(set);
            slots.Sort((a, b) => a.Code.CompareTo(b.Code));
            var slotCodes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var slot in slots)
            {
                config._equipSlotIds.Add(slot.Id);
                slotCodes[slot.Id] = slot.Code;
            }
            var containers = Rows<ContainerDef>(set);
            containers.Sort((a, b) => a.Code.CompareTo(b.Code));
            foreach (var container in containers)
            {
                config._containers.Add(new ContainerRule((byte)container.Code, container.Slots, container.Accepts,
                    container.Kind == ContainerKind.Equipped));
                config._containerIds[container.Code] = container.Id;
            }
            if (config._containers.Count == 0)
                config._containers.Add(new ContainerRule(config.NormalBagCode, config.BagSlots, (ItemCategory[])Enum.GetValues(typeof(ItemCategory)), false));
            var icons = new Dictionary<string, ItemIconRegistry>(StringComparer.Ordinal);
            foreach (var icon in Rows<ItemIconRegistry>(set))
                icons[icon.Id] = icon;
            foreach (var item in Rows<ItemDef>(set))
            {
                icons.TryGetValue(item.Id, out var icon);
                config._items[item.NetId] = new ItemInfo(item, icon);
                int mask = 0;
                foreach (var slotId in item.EquipSlots)
                {
                    if (slotCodes.TryGetValue(slotId, out int code))
                        mask |= 1 << code;
                }
                config._ruleInfo[item.NetId] = new ItemRuleInfo(item.Category, item.MaxStack, (ushort)mask);
            }
            foreach (var rarity in Rows<RarityDef>(set))
            {
                config._rarityIds[rarity.Tier] = rarity.Id;
                config._rarityStatPercent[rarity.Tier] = rarity.StatPercent;
            }
            // WP-M3-2: stat ids by index (StatDef file order, as the server numbers crafted stats).
            foreach (var stat in Rows<StatDef>(set))
                config._statIds.Add(stat.Id);
            // WP-M2-11: the gear stats the tooltip shows, the rows the server's equipment stat source reads (ItemRules).
            var netIds = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in Rows<ItemDef>(set))
                netIds[item.Id] = item.NetId;
            foreach (var bonus in Rows<ItemStatBonusDef>(set))
            {
                if (!netIds.TryGetValue(bonus.ItemId, out int netId))
                    continue;
                if (!config._bonuses.TryGetValue(netId, out var list))
                    config._bonuses[netId] = list = new List<ItemStatBonus>();
                list.Add(new ItemStatBonus(bonus.StatId, bonus.Flat, bonus.Percent));
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
