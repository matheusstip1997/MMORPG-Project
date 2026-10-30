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

        public void Validate(RowErrors errors)
        {
            if (BuyItemIds.Length > 0 && BuyKeep == 0)
                errors.Add(nameof(BuyKeep), "must be above 0 when there are goods to buy");
            if (BuyItemIds.Distinct(StringComparer.Ordinal).Count() != BuyItemIds.Length)
                errors.Add(nameof(BuyItemIds), "names an item twice");
            if (SampleMinutes > Hours * 60)
                errors.Add(nameof(SampleMinutes), "must not be longer than the simulated time");
        }

        /// <summary>Every bought good is in some vendor's <c>NpcVendorStock</c> (R-4.2-01); every hunted type has a spawn point.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (set.TryGet(typeof(NpcVendorStock), out var stock))
            {
                foreach (var item in BuyItemIds.Where(i => !stock.Rows.Cast<NpcVendorStock>().Any(s => s.ItemId == i)))
                    errors.Add(nameof(BuyItemIds), $"'{item}' is in no vendor's NpcVendorStock");
            }
            if (set.TryGet(typeof(SpawnPoints), out var points))
            {
                foreach (var monster in MonsterIds.Where(m => !points.Rows.Cast<SpawnPoints>().Any(p => p.MonsterId == m)))
                    errors.Add(nameof(MonsterIds), $"'{monster}' has no SpawnPoints row");
            }
        }
    }
}
