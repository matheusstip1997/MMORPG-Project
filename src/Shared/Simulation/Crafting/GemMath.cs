using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Util;

namespace Shared.Simulation.Crafting
{
    /// <summary>The random outcome of one gem operation: the socket roll, or the removal cost after the job tree.</summary>
    public readonly struct GemRollResult
    {
        public GemRollResult(bool success, int removalCost)
        {
            Success = success;
            RemovalCost = removalCost;
        }

        public bool Success { get; }

        public int RemovalCost { get; }
    }

    /// <summary>
    /// The gem rolls (R-5.3-05; M3 decision 11, WP-M3-9), pure and integer (rule 6). Each operation draws a 64-bit seed from
    /// the world's gem stream (logged at boot) and rolls on its own generator, so the seed alone reproduces the outcome.
    /// </summary>
    public static class GemMath
    {
        /// <summary>The <c>Pcg32Rng</c> stream of one gem operation's rolls (the seed is per operation).</summary>
        public const ulong Stream = 0x47454D53UL; // "GEMS"

        /// <summary>The socket chance: the tier's odds plus the job tree's socketOddsPercent (already capped), 0 to 100.</summary>
        public static int SocketChance(int oddsPercent, int bonusPercent)
        {
            long chance = (long)oddsPercent + bonusPercent;
            return chance < 0 ? 0 : chance > 100 ? 100 : (int)chance;
        }

        /// <summary>
        /// The removal cost after the job tree's removalCostPercent (user answer 2026-10-31): <c>count * (100 - percent)</c>
        /// hundredths, the whole units plus one more with the chance of the remainder (one draw, always made). The percent
        /// is clamped to 0..100, so the cost never rises and never goes below 0.
        /// </summary>
        public static int RemovalCost(int count, int percent, IRng rng)
        {
            int p = percent < 0 ? 0 : percent > 100 ? 100 : percent;
            long hundredths = (long)Math.Max(0, count) * (100 - p);
            int units = (int)(hundredths / 100);
            int remainder = (int)(hundredths % 100);
            if (rng.NextInt(100) < remainder)
                units++;
            return units;
        }

        /// <summary>The most <see cref="RemovalCost"/> can come to: the whole units rounded up.</summary>
        public static int MaxRemovalCost(int count, int percent)
        {
            int p = percent < 0 ? 0 : percent > 100 ? 100 : percent;
            long hundredths = (long)Math.Max(0, count) * (100 - p);
            return (int)((hundredths + 99) / 100);
        }

        /// <summary>
        /// Every random number of one gem operation from its seed, in one fixed order: the socket roll first (always drawn),
        /// then the removal cost's remainder roll (always drawn). A socket reads <see cref="GemRollResult.Success"/>, a
        /// removal <see cref="GemRollResult.RemovalCost"/>.
        /// </summary>
        public static GemRollResult Draw(ulong seed, int socketChance, int removalCount, int removalPercent)
        {
            var rng = new Pcg32Rng(seed, Stream);
            bool success = rng.RollPercent(socketChance);
            int cost = RemovalCost(removalCount, removalPercent, rng);
            return new GemRollResult(success, cost);
        }
    }

    /// <summary>
    /// The gem tables as runtime rules (spec 5.3 M3 implementation WP-M3-9), shared by the server's <c>GemService</c>, the
    /// client's gems tab and the bots' socket step, so all three refuse the same requests. Built once from the tables.
    /// </summary>
    public sealed class GemRules
    {
        private readonly Dictionary<string, GemDef> _gems = new Dictionary<string, GemDef>(StringComparer.Ordinal);
        private readonly Dictionary<int, GemSocketOdds> _odds = new Dictionary<int, GemSocketOdds>();
        private readonly Dictionary<string, ItemDef> _items = new Dictionary<string, ItemDef>(StringComparer.Ordinal);
        private readonly Dictionary<int, ItemDef> _itemsByNetId = new Dictionary<int, ItemDef>();

        private GemRules()
        {
        }

        /// <summary>Null while the gem tables are not loaded (an older data set).</summary>
        public static GemRules? From(ConfigSet set)
        {
            if (!set.TryGet(typeof(GemDef), out var gems) || !set.TryGet(typeof(GemSocketOdds), out var odds)
                || !set.TryGet(typeof(ItemDef), out var items))
                return null;
            var rules = new GemRules();
            foreach (var row in items.Rows)
            {
                var item = (ItemDef)row;
                rules._items[item.Id] = item;
                rules._itemsByNetId[item.NetId] = item;
            }
            foreach (var row in gems.Rows)
            {
                var gem = (GemDef)row;
                rules._gems[gem.Id] = gem;
            }
            foreach (var row in odds.Rows)
            {
                var tier = (GemSocketOdds)row;
                rules._odds[tier.Tier] = tier;
            }
            return rules;
        }

        public GemDef? Gem(string itemId) => _gems.TryGetValue(itemId, out var gem) ? gem : null;

        public GemSocketOdds? Odds(int tier) => _odds.TryGetValue(tier, out var odds) ? odds : null;

        public ItemDef? Item(string id) => _items.TryGetValue(id, out var item) ? item : null;

        public ItemDef? Item(int netId) => _itemsByNetId.TryGetValue(netId, out var item) ? item : null;

        /// <summary>Equipment with at least one gem slot (<c>ItemDef.gemSlots</c>).</summary>
        public static bool IsSocketable(ItemDef? item) => item != null && item.Category == ItemCategory.Equipment && item.GemSlots > 0;

        /// <summary>
        /// Socketing the gem <paramref name="gemItem"/> into <paramref name="target"/> holding <paramref name="gemsInItem"/>
        /// gems, for a character at <paramref name="jobLevel"/> in the tier's job (0: not held). The order is the server's.
        /// </summary>
        public GemReason CheckSocket(ItemDef? target, int gemsInItem, ItemDef? gemItem, int jobLevel, int freeUnits)
        {
            if (!IsSocketable(target))
                return GemReason.NotSocketable;
            var gem = gemItem == null ? null : Gem(gemItem.Id);
            var odds = gem == null ? null : Odds(gem.Tier);
            if (gem == null || odds == null)
                return GemReason.NotAGem;
            if (jobLevel <= 0)
                return GemReason.JobRequired;
            if (gemsInItem >= target!.GemSlots)
                return GemReason.NoGemSlot;
            if (jobLevel < odds.MinJobLevel)
                return GemReason.JobLevelTooLow;
            return freeUnits >= 1 ? GemReason.None : GemReason.MaterialsMissing;
        }

        /// <summary>
        /// Removing the gem at <paramref name="index"/> of <paramref name="gems"/> from <paramref name="target"/>: the bag
        /// must hold the most the cost can come to after <paramref name="removalPercent"/> (<see cref="MaxRemovalCost"/>).
        /// </summary>
        public GemReason CheckRemove(ItemDef? target, IReadOnlyList<string> gems, int index, int jobLevel, int freeCostUnits,
            int removalPercent)
        {
            if (!IsSocketable(target))
                return GemReason.NotSocketable;
            if (index < 0 || index >= gems.Count)
                return GemReason.NoSuchGem;
            var gem = Gem(gems[index]);
            var odds = gem == null ? null : Odds(gem.Tier);
            if (gem == null || odds == null)
                return GemReason.NoSuchGem;
            if (jobLevel <= 0)
                return GemReason.JobRequired;
            if (jobLevel < odds.MinJobLevel)
                return GemReason.JobLevelTooLow;
            return freeCostUnits >= GemMath.MaxRemovalCost(odds.RemovalCostCount, removalPercent)
                ? GemReason.None : GemReason.MaterialsMissing;
        }

        /// <summary>The job the tier's service belongs to (the Jeweler), or empty.</summary>
        public string JobFor(GemDef gem) => Odds(gem.Tier)?.JobId ?? string.Empty;
    }
}
