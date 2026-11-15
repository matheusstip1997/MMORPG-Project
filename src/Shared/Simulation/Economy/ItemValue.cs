using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Simulation.Economy
{
    /// <summary>
    /// The gold value of items for the large-transfer flags and the report's trade value (spec 4.5 R-4.5-02, M4 decision 17,
    /// user answer 2026-11-15; WP-M4-15; a pure function with unit tests): a unit is worth its category's
    /// <see cref="ItemValueBand"/> with the highest tier not above the item's, times its rarity's <see cref="RarityMult"/>
    /// percent (rounded down); a sell-only item its <see cref="PriceBase"/> times the same percent; an unknown item, a rarity
    /// without a percent or a category without a band is worth 0. Integer only (rule 6). Immutable once built: safe on any thread.
    /// </summary>
    public sealed class ItemValue
    {
        private readonly Dictionary<string, ItemDef> _byId;
        private readonly Dictionary<int, ItemDef> _byNetId;
        private readonly Dictionary<ItemCategory, List<ItemValueBand>> _bands;
        private readonly Dictionary<string, int> _priceBase;
        private readonly Dictionary<int, int> _rarityPercent;

        public ItemValue(IReadOnlyList<ItemDef> items, IReadOnlyList<ItemValueBand> bands, IReadOnlyList<PriceBase> prices,
            IReadOnlyList<RarityDef> rarities, IReadOnlyList<RarityMult> mults)
        {
            _byId = new Dictionary<string, ItemDef>(StringComparer.Ordinal);
            _byNetId = new Dictionary<int, ItemDef>();
            foreach (var item in items)
            {
                _byId[item.Id] = item;
                _byNetId[item.NetId] = item;
            }
            _bands = new Dictionary<ItemCategory, List<ItemValueBand>>();
            foreach (var band in bands)
            {
                if (!_bands.TryGetValue(band.Category, out var list))
                    _bands[band.Category] = list = new List<ItemValueBand>();
                list.Add(band);
            }
            foreach (var list in _bands.Values)
                list.Sort((a, b) => a.Tier.CompareTo(b.Tier));
            _priceBase = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var price in prices)
                _priceBase[price.ItemId] = price.BaseGold;
            var tiers = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var rarity in rarities)
                tiers[rarity.Id] = rarity.Tier;
            _rarityPercent = new Dictionary<int, int>();
            foreach (var mult in mults)
            {
                if (tiers.TryGetValue(mult.RarityId, out int tier))
                    _rarityPercent[tier] = mult.PricePercent;
            }
        }

        /// <summary>From tables that passed validation; null when the value bands are not loaded.</summary>
        public static ItemValue? From(ConfigSet set)
        {
            if (!set.TryGet(typeof(ItemValueBand), out _))
                return null;
            return new ItemValue(Rows<ItemDef>(set), Rows<ItemValueBand>(set), Rows<PriceBase>(set), Rows<RarityDef>(set),
                Rows<RarityMult>(set));
        }

        /// <summary>The value of one unit of the item with this net id at this rarity tier.</summary>
        public long UnitValue(int itemNetId, int rarityTier) =>
            _byNetId.TryGetValue(itemNetId, out var item) ? UnitValue(item, rarityTier) : 0;

        /// <summary>The value of one unit of the item with this id at this rarity tier.</summary>
        public long UnitValue(string itemId, int rarityTier) =>
            _byId.TryGetValue(itemId, out var item) ? UnitValue(item, rarityTier) : 0;

        /// <summary>The value of <paramref name="count"/> units (0 for a count below 1).</summary>
        public long StackValue(int itemNetId, int rarityTier, long count) => count > 0 ? UnitValue(itemNetId, rarityTier) * count : 0;

        /// <summary>The value of <paramref name="count"/> units (0 for a count below 1).</summary>
        public long StackValue(string itemId, int rarityTier, long count) => count > 0 ? UnitValue(itemId, rarityTier) * count : 0;

        /// <summary>The value of one unit of <paramref name="item"/> at this rarity tier.</summary>
        public long UnitValue(ItemDef item, int rarityTier)
        {
            if (!_rarityPercent.TryGetValue(rarityTier, out int percent))
                return 0;
            long baseGold = item.Category == ItemCategory.SellOnly
                ? (_priceBase.TryGetValue(item.Id, out int price) ? price : 0)
                : Band(item);
            return baseGold * percent / 100;
        }

        private long Band(ItemDef item)
        {
            if (!_bands.TryGetValue(item.Category, out var list))
                return 0;
            long gold = 0;
            foreach (var band in list)
            {
                if (band.Tier > item.Tier)
                    break;
                gold = band.GoldPerUnit;
            }
            return gold;
        }

        private static IReadOnlyList<T> Rows<T>(ConfigSet set) where T : class
        {
            if (!set.TryGet(typeof(T), out var table))
                return Array.Empty<T>();
            var rows = new List<T>(table.Rows.Count);
            foreach (var row in table.Rows)
                rows.Add((T)row);
            return rows;
        }
    }
}
