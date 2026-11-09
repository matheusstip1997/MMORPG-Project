using System;
using System.Linq;

namespace Shared.Data.Tables
{
    /// <summary>
    /// One run of the economy simulator (spec 12.5, WP-M2-12, <c>src/Tools/EconomySimulator</c>): how many players hunt
    /// which monster types at what kill rate, how often they sell at an outpost and what they buy from the vendors, and the
    /// settle check's window and tolerance. The simulator plays it over the economy tables (prices, demand loop, vendor
    /// stock, loot, spawn points) to catch structural flaws only: a loop that never settles, counters that grow without
    /// bound, a faucet that keeps accelerating. Tools only; not a game rule. Every value is a placeholder.
    /// </summary>
    [Table("EconomySimScenario", 1)]
    public sealed class EconomySimScenario : IValidatableRow, ISetValidatableRow
    {
        /// <summary>For example <c>m2-small</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Active players: they hunt, and they normalize the demand loop (R-4.2-02, R-4.2-03).</summary>
        [Range(1, 10_000)] public int Players { get; set; }

        /// <summary>Simulated time.</summary>
        [Range(1, 168)] public int Hours { get; set; }

        /// <summary>Monte Carlo replicates; replicate <c>i</c> uses the seed <see cref="Seed"/> + i.</summary>
        [Range(1, 100)] public int Runs { get; set; }

        [Range(0, int.MaxValue)] public int Seed { get; set; }

        /// <summary>Kills one player makes per minute while a monster of its pool is alive, in thousandths (QA wave 1: 1330), at most one a second.</summary>
        [Range(1, 60_000)] public int KillsPerPlayerMinuteMilli { get; set; }

        /// <summary>The hunted types; players spread round robin over their spawn pools. Empty: every type that is not a boss.</summary>
        [Ref(typeof(MonsterDef))] public string[] MonsterIds { get; set; } = Array.Empty<string>();

        /// <summary>A player visits an outpost after this many kills and sells every sell-only stack it carries.</summary>
        [Range(1, 1_000)] public int KillsPerVisit { get; set; }

        /// <summary>At each visit the player tops these vendor goods up to <see cref="BuyKeep"/> units, as far as its gold pays.</summary>
        [Ref(typeof(ItemDef))] public string[] BuyItemIds { get; set; } = Array.Empty<string>();

        [Range(0, 1_000)] public int BuyKeep { get; set; }

        /// <summary>
        /// Units of each bought good a player uses per hour: a placeholder assumption the simulator caps per good by its
        /// <c>ConsumableDef</c> (the meter decay over its cost and its cooldown, <c>ConsumableRules.MaxUsesPerHour</c>; consumable
        /// use, WP-M3-3). It is the simulator's input, not a measured rate.
        /// </summary>
        [Range(0, 1_000)] public int UsedPerPlayerHour { get; set; }

        /// <summary>
        /// Outputs are averaged over samples of this length (the mean of the control steps in it, over all runs), so the
        /// settle check reads the trend, not one step's noise; the report prints one line per sample.
        /// </summary>
        [Range(1, 600)] public int SampleMinutes { get; set; }

        /// <summary>The settle check reads this last share of the simulated time.</summary>
        [Range(1, 100)] public int SettleWindowPercent { get; set; }

        /// <summary>
        /// Settled: within the window every multiplier moves by at most this many percentage points, and the gold created
        /// per hour stays within this percent of its window mean.
        /// </summary>
        [Range(0, 1_000)] public int SettleTolerancePercent { get; set; }

        // The M3 activity of a row (spec 12.5, WP-M3-12): the sinks it plays, all optional (the M2 rows play none). Rates per
        // player-hour are the simulator's assumptions, never measured and never a game rule.

        /// <summary>Harvest attempts per player-hour; one needs an available node of the player's turn.</summary>
        [Optional, Range(0, 3_600)] public int HarvestsPerPlayerHour { get; set; }

        /// <summary>The harvested node types; empty: every type with a <c>GatherNodes</c> row.</summary>
        [Optional, Ref(typeof(NodeDef))] public string[] NodeIds { get; set; } = Array.Empty<string>();

        /// <summary>The job level of the row's players: the yield bonus and the recipes and gem tiers they reach. 0 when the row plays no M3 activity.</summary>
        [Optional, Range(0, 1_000)] public int JobLevel { get; set; }

        /// <summary>Vendor goods each character buys once (tools and molds: tools never wear out, R-5.2-02; molds are reusable, R-5.3-01).</summary>
        [Optional, Ref(typeof(ItemDef))] public string[] OneTimeBuyItemIds { get; set; } = Array.Empty<string>();

        /// <summary>Conversion tries per player-hour, capped by the materials held and <c>ConversionOdds.dailyCap</c>.</summary>
        [Optional, Range(0, 3_600)] public int ConversionsPerPlayerHour { get; set; }

        /// <summary>Enchant tries per player-hour, capped by the stones held.</summary>
        [Optional, Range(0, 3_600)] public int EnchantTriesPerPlayerHour { get; set; }

        /// <summary>Socket tries per player-hour, capped by the cut gems held.</summary>
        [Optional, Range(0, 3_600)] public int SocketsPerPlayerHour { get; set; }

        /// <summary>Gem removals per player-hour, capped by the socketed gems and the rough gems of the cost held.</summary>
        [Optional, Range(0, 3_600)] public int GemRemovalsPerPlayerHour { get; set; }

        // The M4 fee volume of a row (spec 12.5, WP-M4-1): player-to-player gold moves that pay a FeeService fee, all optional
        // (the M2 and M3 rows play none). Assumptions of the tool (placeholders), never measured and never a game rule.

        /// <summary>Auction or stall listings created per player-hour; each pays the listing fee on its asking price.</summary>
        [Optional, Range(0, 3_600)] public int ListingsPerPlayerHour { get; set; }

        /// <summary>Listings bought per player-hour: the buyer pays another player, who pays the sale tax on the price.</summary>
        [Optional, Range(0, 3_600)] public int TradesPerPlayerHour { get; set; }

        /// <summary>Contracts completed per player-hour: the client pays a worker, who pays the contract fee on the gold.</summary>
        [Optional, Range(0, 3_600)] public int ContractsPerPlayerHour { get; set; }

        /// <summary>The traded value (asking price, sale price, contract gold) is uniform from this ...</summary>
        [Optional, Range(0, 1_000_000_000)] public int TradeValueMinGold { get; set; }

        /// <summary>... to this, both included.</summary>
        [Optional, Range(0, 1_000_000_000)] public int TradeValueMaxGold { get; set; }

        /// <summary>The row plays the M4 fee volume.</summary>
        public bool PlaysFees() => ListingsPerPlayerHour > 0 || TradesPerPlayerHour > 0 || ContractsPerPlayerHour > 0;

        public void Validate(RowErrors errors)
        {
            if (TradeValueMaxGold < TradeValueMinGold)
                errors.Add(nameof(TradeValueMaxGold), $"{TradeValueMaxGold} is below tradeValueMinGold {TradeValueMinGold}");
            if (PlaysFees() && TradeValueMaxGold == 0)
                errors.Add(nameof(TradeValueMaxGold), "must be above 0 when the row lists, trades or completes contracts");
            if (BuyItemIds.Length > 0 && BuyKeep == 0)
                errors.Add(nameof(BuyKeep), "must be above 0 when there are goods to buy");
            if (BuyItemIds.Distinct(StringComparer.Ordinal).Count() != BuyItemIds.Length)
                errors.Add(nameof(BuyItemIds), "names an item twice");
            if (OneTimeBuyItemIds.Distinct(StringComparer.Ordinal).Count() != OneTimeBuyItemIds.Length)
                errors.Add(nameof(OneTimeBuyItemIds), "names an item twice");
            if (OneTimeBuyItemIds.Intersect(BuyItemIds, StringComparer.Ordinal).Any())
                errors.Add(nameof(OneTimeBuyItemIds), "names a good BuyItemIds keeps in stock");
            if (SampleMinutes > Hours * 60)
                errors.Add(nameof(SampleMinutes), "must not be longer than the simulated time");
            bool gathersOrCrafts = HarvestsPerPlayerHour > 0 || ConversionsPerPlayerHour > 0 || EnchantTriesPerPlayerHour > 0
                                   || SocketsPerPlayerHour > 0 || GemRemovalsPerPlayerHour > 0;
            if (gathersOrCrafts && JobLevel == 0)
                errors.Add(nameof(JobLevel), "must be above 0 when the row gathers or crafts");
            if (NodeIds.Length > 0 && HarvestsPerPlayerHour == 0)
                errors.Add(nameof(NodeIds), "needs harvestsPerPlayerHour above 0");
        }

        /// <summary>
        /// Every bought good is in some vendor's <c>NpcVendorStock</c> (R-4.2-01); every hunted type has a spawn point; every
        /// harvested node type has a <c>GatherNodes</c> row; the job level is within the job's level cap.
        /// </summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (set.TryGet(typeof(NpcVendorStock), out var stock))
            {
                foreach (var item in BuyItemIds.Where(i => !stock.Rows.Cast<NpcVendorStock>().Any(s => s.ItemId == i)))
                    errors.Add(nameof(BuyItemIds), $"'{item}' is in no vendor's NpcVendorStock");
                foreach (var item in OneTimeBuyItemIds.Where(i => !stock.Rows.Cast<NpcVendorStock>().Any(s => s.ItemId == i)))
                    errors.Add(nameof(OneTimeBuyItemIds), $"'{item}' is in no vendor's NpcVendorStock");
            }
            if (set.TryGet(typeof(GatherNodes), out var nodes))
            {
                foreach (var node in NodeIds.Where(n => !nodes.Rows.Cast<GatherNodes>().Any(g => g.NodeId == n)))
                    errors.Add(nameof(NodeIds), $"'{node}' has no GatherNodes row");
            }
            if (set.TryGet(typeof(JobSettings), out var jobs) && jobs.Rows.Count == 1 && JobLevel > ((JobSettings)jobs.Rows[0]).MaxLevel)
                errors.Add(nameof(JobLevel), $"{JobLevel} is above JobSettings.maxLevel");
            if (set.TryGet(typeof(SpawnPoints), out var points))
            {
                foreach (var monster in MonsterIds.Where(m => !points.Rows.Cast<SpawnPoints>().Any(p => p.MonsterId == m)))
                    errors.Add(nameof(MonsterIds), $"'{monster}' has no SpawnPoints row");
            }
        }
    }
}
