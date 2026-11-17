#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Economy;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the auction window (spec 9.1: no user-facing text in code; spec 4.5, 9.13; WP-M4-7). An EditMode test
    /// checks that each constant has a row.
    /// </summary>
    public static class AuctionTextIds
    {
        public const string Title = "auction.title";
        public const string Close = "auction.close";
        public const string CloseLabel = "auction.closeLabel";
        public const string TabSearch = "auction.tab.search";
        public const string TabMine = "auction.tab.mine";
        public const string TabSell = "auction.tab.sell";
        /// <summary>The name field's placeholder: the typed text is matched against the item names here, never sent.</summary>
        public const string NameHint = "auction.nameHint";
        /// <summary>Kind: {kind} (the category filter button, cycling through the tradable categories and "any").</summary>
        public const string Category = "auction.category";
        public const string CategoryAny = "auction.category.any";
        public const string Search = "auction.search";
        public const string Previous = "auction.previous";
        public const string Next = "auction.next";
        public const string Empty = "auction.empty";
        /// <summary>No item is named like the typed text: nothing is asked.</summary>
        public const string NoMatch = "auction.noMatch";
        /// <summary>The name matches more than {max} items: only the first {max} were searched; type more of the name.</summary>
        public const string TooMany = "auction.tooMany";
        /// <summary>{item} x{count}   {price} gold each   {time} left.</summary>
        public const string Row = "auction.row";
        /// <summary>{hours} h.</summary>
        public const string Hours = "auction.hours";
        /// <summary>{minutes} min (the last hour).</summary>
        public const string Minutes = "auction.minutes";
        public const string Buy = "auction.buy";
        public const string Count = "auction.count";
        /// <summary>Total {gold} gold.</summary>
        public const string Total = "auction.total";
        /// <summary>{count} of {cap} listings.</summary>
        public const string MineCount = "auction.mineCount";
        public const string MineEmpty = "auction.mineEmpty";
        public const string Cancel = "auction.cancel";
        public const string SellHint = "auction.sellHint";
        public const string Price = "auction.price";
        /// <summary>For {hours} h (the duration button, cycling through <c>ListingSettings.durationsMs</c>).</summary>
        public const string Duration = "auction.duration";
        /// <summary>Listing fee {fee} gold (never returned).</summary>
        public const string Fee = "auction.fee";
        public const string List = "auction.list";
        public const string Waiting = "auction.waiting";
        /// <summary>Listed {what} (an item and its count).</summary>
        public const string ToastListed = "auction.toast.listed";
        /// <summary>You bought {what} for {gold} gold.</summary>
        public const string ToastBought = "auction.toast.bought";
        /// <summary>You bought {what}; your bag was full, so it waits in your mail.</summary>
        public const string ToastBoughtMail = "auction.toast.boughtMail";
        /// <summary>The listing is cancelled; what was left comes back by mail.</summary>
        public const string ToastCancelled = "auction.toast.cancelled";
        /// <summary>{name} x{count} (a row's item, the toasts' {what}).</summary>
        public const string ItemCount = "auction.itemCount";
        /// <summary>{item} ({details}): a row's item with its rarity above common, its quality and enchant level.</summary>
        public const string ItemDetails = "auction.itemDetails";
        /// <summary>Rarity: {rarity} (the filter button: "any", or the lowest tier shown).</summary>
        public const string Rarity = "auction.rarity";
        /// <summary>{rarity} or better.</summary>
        public const string RarityAtLeast = "auction.rarityAtLeast";
        /// <summary>Job: {job}.</summary>
        public const string Job = "auction.job";
        /// <summary>Armour: {tier}.</summary>
        public const string Armor = "auction.armor";
        public const string Any = "auction.any";
        public const string LevelMin = "auction.levelMin";
        public const string LevelMax = "auction.levelMax";
        /// <summary>The auctioneer's yes below the auction level (WP-M4-11, M4 decision 14): {level}.</summary>
        public const string LevelLocked = "auction.levelLocked";

        public static readonly string[] All =
        {
            Title, Close, CloseLabel, TabSearch, TabMine, TabSell, NameHint, Category, CategoryAny, Search, Previous, Next, Empty,
            NoMatch, TooMany, Row, Hours, Minutes, Buy, Count, Total, MineCount, MineEmpty, Cancel, SellHint, Price, Duration, Fee, List,
            Waiting, ToastListed, ToastBought, ToastBoughtMail, ToastCancelled, ItemCount, ItemDetails, Rarity, RarityAtLeast, Job, Armor,
            Any, LevelMin, LevelMax, LevelLocked,
        };

        /// <summary>The name of a category in the filter ("auction.category.material", ...).</summary>
        public static string CategoryOf(ItemCategory category) => "auction.category." + char.ToLowerInvariant(category.ToString()[0])
                                                                  + category.ToString().Substring(1);
    }

    /// <summary>
    /// The config tables the auction window reads (spec 4.5, 9.13; M4 decision 6; WP-M4-7), from a loaded and validated
    /// <see cref="ConfigSet"/>: the items by net id (names for the rows and the name filter), the categories a listing may be of,
    /// the durations, the page size and the name filter's item count of <c>ListingSettings</c>, the listing fee's rate of
    /// <c>FeeRates</c> (shown before listing; the server charges it), and the auctioneers' <c>NpcDef.netId</c>s. Display only: the
    /// server owns every listing and decides every sale.
    /// </summary>
    public sealed class AuctionConfig
    {
        /// <summary>The categories a listing can be of (sell-only and quest items are never listed, R-4.5-01).</summary>
        public static readonly ItemCategory[] Categories = ListingItems.Categories;

        private readonly Dictionary<int, ItemDef> _items = new Dictionary<int, ItemDef>();
        private readonly HashSet<int> _auctioneers = new HashSet<int>();
        private readonly List<byte> _rarityTiers = new List<byte>();
        private readonly List<JobDef> _jobs = new List<JobDef>();

        public AuctionConfig(InventoryConfig inventory, ListingSettings? settings, int listingFeePercent)
        {
            Inventory = inventory;
            DurationsMs = settings?.DurationsMs ?? Array.Empty<int>();
            PageSize = settings?.PageSize ?? 20;
            SearchItemIdsMax = settings?.SearchItemIdsMax ?? 32;
            ListingFeePercent = listingFeePercent;
        }

        /// <summary>Rarity names and gem rules for the row details (the inventory tooltip's).</summary>
        public InventoryConfig Inventory { get; }

        /// <summary>The durations a seller chooses from (<c>ListingSettings.durationsMs</c>), shortest first.</summary>
        public IReadOnlyList<int> DurationsMs { get; }

        public int PageSize { get; }

        /// <summary>Item net ids one search names at most (<c>ListingSettings.searchItemIdsMax</c>).</summary>
        public int SearchItemIdsMax { get; }

        /// <summary>The listing fee's default rate (<c>FeeRates</c> <c>listing</c>); the server's quote decides.</summary>
        public int ListingFeePercent { get; }

        /// <summary>The listing fee shown before listing (<see cref="FeeMath"/>, rounded down as the server does; 0 for a bad total).</summary>
        public long FeeFor(long unitPrice, int count) =>
            ListingMath.TryTotal(unitPrice, count, long.MaxValue, out long total) ? FeeMath.Total(FeeType.Listing, total, ListingFeePercent, 0) : 0;

        public ItemDef? Item(int netId) => _items.TryGetValue(netId, out var item) ? item : null;

        public void AddItem(ItemDef item) => _items[item.NetId] = item;

        /// <summary>Whether the NPC of a <c>NpcDef.netId</c> runs an auction (it offers <c>Auction</c>).</summary>
        public bool IsAuctioneer(int npcNetId) => _auctioneers.Contains(npcNetId);

        public void AddAuctioneer(int npcNetId) => _auctioneers.Add(npcNetId);

        /// <summary>The rarity tiers above common, lowest first (the rarity filter's steps; common is "any").</summary>
        public IReadOnlyList<byte> RarityTiers => _rarityTiers;

        public void AddRarityTier(byte tier)
        {
            if (tier > 0 && !_rarityTiers.Contains(tier))
            {
                _rarityTiers.Add(tier);
                _rarityTiers.Sort();
            }
        }

        /// <summary>The jobs, lowest net id first (the job filter's steps).</summary>
        public IReadOnlyList<JobDef> Jobs => _jobs;

        public void AddJob(JobDef job)
        {
            _jobs.Add(job);
            _jobs.Sort((a, b) => a.NetId.CompareTo(b.NetId));
        }

        /// <summary>
        /// The name filter (M4 decision 6, "search by name"): the net ids of the listable items whose shown name contains
        /// <paramref name="text"/> (any case), lowest net id first, at most <see cref="SearchItemIdsMax"/>. The server never sees the
        /// text, so every language searches the same way. <paramref name="tooMany"/>: more items matched than that, and only the
        /// first were kept (the window says so, so a short page is not read as "nothing listed").
        /// </summary>
        public ushort[] MatchNames(string text, Func<string, string> nameOf, out bool tooMany)
        {
            tooMany = false;
            string wanted = text.Trim();
            if (wanted.Length == 0)
                return Array.Empty<ushort>();
            var ids = new List<int>();
            foreach (var item in _items.Values)
            {
                if (Array.IndexOf(Categories, item.Category) < 0)
                    continue;
                if (nameOf(item.NameId).IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                    ids.Add(item.NetId);
            }
            ids.Sort();
            tooMany = ids.Count > SearchItemIdsMax;
            var result = new ushort[Math.Min(ids.Count, SearchItemIdsMax)];
            for (int i = 0; i < result.Length; i++)
                result[i] = (ushort)ids[i];
            return result;
        }

        /// <param name="inventory">The HUD's inventory config; null builds one from the tables.</param>
        public static AuctionConfig From(ConfigSet set, InventoryConfig? inventory = null)
        {
            int feePercent = 0;
            if (set.TryGet(typeof(FeeRates), out var fees))
            {
                foreach (var row in fees.Rows)
                {
                    if (((FeeRates)row).Id == "listing")
                        feePercent = ((FeeRates)row).DefaultPercent;
                }
            }
            var config = new AuctionConfig(inventory ?? InventoryConfig.From(set), ListingSettings.Find(set), feePercent);
            if (set.TryGet(typeof(ItemDef), out var items))
            {
                foreach (var row in items.Rows)
                    config.AddItem((ItemDef)row);
            }
            if (set.TryGet(typeof(RarityDef), out var rarities))
            {
                foreach (var row in rarities.Rows)
                    config.AddRarityTier((byte)((RarityDef)row).Tier);
            }
            if (set.TryGet(typeof(JobDef), out var jobs))
            {
                foreach (var row in jobs.Rows)
                    config.AddJob((JobDef)row);
            }
            if (set.TryGet(typeof(NpcDef), out var npcs))
            {
                foreach (var row in npcs.Rows)
                {
                    if (((NpcDef)row).Offers(NpcInteraction.Auction))
                        config.AddAuctioneer(((NpcDef)row).NetId);
                }
            }
            return config;
        }
    }
}
