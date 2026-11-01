using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Shared.Simulation.Crafting
{
    /// <summary>
    /// The enchant tables as runtime data (spec 5.3 R-5.3-04, M3 decision 10; WP-M3-8): the odds and bonus by level, the
    /// stones by tier, and for every equipment item its job (<c>ItemDef.jobId</c>, else its mold's), its tier (the tier of
    /// the mold that makes it, dropped gear 1) and the station of that job (the station of its molds). Built once from a
    /// validated config set; read-only; shared by the server, the enchant tab and the bots, so every side checks alike.
    /// </summary>
    public sealed class EnchantRules
    {
        private readonly EnchantOdds?[] _odds = new EnchantOdds?[EnchantLimits.MaxLevel];
        private readonly int[] _statPercent = new int[EnchantLimits.MaxLevel + 1];
        private readonly Dictionary<int, EnchantMaterial> _materials = new Dictionary<int, EnchantMaterial>();
        private readonly Dictionary<string, ItemDef> _items = new Dictionary<string, ItemDef>(StringComparer.Ordinal);
        private readonly Dictionary<int, ItemDef> _itemsByNetId = new Dictionary<int, ItemDef>();
        private readonly Dictionary<string, MoldDef> _moldOf = new Dictionary<string, MoldDef>(StringComparer.Ordinal);
        private readonly Dictionary<string, NpcInteraction> _stations = new Dictionary<string, NpcInteraction>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _jobIds = new Dictionary<int, string>();

        private EnchantRules()
        {
        }

        /// <summary>Builds the rules from a validated config set; null when an enchant table is not loaded.</summary>
        public static EnchantRules? From(ConfigSet set)
        {
            if (!set.TryGet(typeof(EnchantOdds), out _) || !set.TryGet(typeof(EnchantBonus), out _)
                || !set.TryGet(typeof(EnchantMaterial), out _) || !set.TryGet(typeof(MoldDef), out var molds))
                return null;
            var rules = new EnchantRules();
            rules.SafeCode = (byte)(ContainerDef.FindReceiver(set, ItemCategory.Equipment)?.Code ?? 0);
            rules.EquippedCode = (byte)(ContainerDef.FindEquipped(set)?.Code ?? 0);
            rules.NormalCode = (byte)(ContainerDef.Find(set, ContainerDef.NormalBag)?.Code ?? 0);
            foreach (var odds in NetIds.Rows<EnchantOdds>(set))
                rules._odds[odds.Level] = odds;
            foreach (var bonus in NetIds.Rows<EnchantBonus>(set))
                rules._statPercent[bonus.Level] = bonus.StatPercent;
            foreach (var material in NetIds.Rows<EnchantMaterial>(set))
                rules._materials[material.Tier] = material;
            foreach (var item in NetIds.Rows<ItemDef>(set))
            {
                rules._items[item.Id] = item;
                rules._itemsByNetId[item.NetId] = item;
            }
            foreach (var job in NetIds.Rows<JobDef>(set))
                rules._jobIds[job.NetId] = job.Id;
            foreach (var row in molds.Rows)
            {
                var mold = (MoldDef)row;
                if (mold.IsCraftable())
                    rules._moldOf[mold.OutputItemId] = mold;
                if (!rules._stations.ContainsKey(mold.JobId))
                    rules._stations[mold.JobId] = mold.Station;
            }
            for (int level = 0; level < EnchantLimits.MaxLevel; level++)
            {
                if (rules._odds[level] == null)
                    return null;
            }
            return rules;
        }

        /// <summary>The Safe bag's equipment area (the container that receives equipment), where an item to enchant may lie.</summary>
        public byte SafeCode { get; private set; }

        /// <summary>The Equipped container, the other place an item to enchant may lie.</summary>
        public byte EquippedCode { get; private set; }

        /// <summary>The Normal bag, where the stones are taken from.</summary>
        public byte NormalCode { get; private set; }

        /// <summary>An item to enchant lies in this container (the Safe bag's equipment area or Equipped).</summary>
        public bool IsTargetContainer(byte code) => code != 0 && (code == SafeCode || code == EquippedCode);

        public ItemDef? Item(int netId) => _itemsByNetId.TryGetValue(netId, out var item) ? item : null;

        public ItemDef? Item(string id) => _items.TryGetValue(id, out var item) ? item : null;

        /// <summary>Whether the jobs of <paramref name="state"/> (the client's <c>JobState</c>) include <paramref name="jobId"/>.</summary>
        public bool Holds(JobState? state, string jobId)
        {
            if (state?.Jobs == null)
                return false;
            foreach (var entry in state.Jobs)
            {
                if (entry != null && _jobIds.TryGetValue(entry.JobNetId, out var id) && id == jobId)
                    return true;
            }
            return false;
        }

        /// <summary>The item's job: <c>ItemDef.jobId</c>, else the job of the mold that makes it; empty for none.</summary>
        public string JobOf(ItemDef item)
        {
            if (item.JobId.Length > 0)
                return item.JobId;
            return _moldOf.TryGetValue(item.Id, out var mold) ? mold.JobId : string.Empty;
        }

        /// <summary>The item's tier for its stones: the tier of the mold that makes it, dropped gear 1 (user answer).</summary>
        public int TierOf(ItemDef item) => _moldOf.TryGetValue(item.Id, out var mold) ? mold.Tier : 1;

        /// <summary>The station a job's items are enchanted at (the station of its molds); None for a job without molds.</summary>
        public NpcInteraction StationOf(string jobId) =>
            _stations.TryGetValue(jobId, out var station) ? station : NpcInteraction.None;

        /// <summary>The odds of an attempt from <paramref name="level"/> (0..8); null at +9 or out of range.</summary>
        public EnchantOdds? Odds(int level) => level >= 0 && level < _odds.Length ? _odds[level] : null;

        /// <summary>The bonus percent of an item at <paramref name="level"/> (0 at +0 or out of range).</summary>
        public int StatPercent(int level) => level > 0 && level < _statPercent.Length ? _statPercent[level] : 0;

        /// <summary>The material one attempt on an item of <paramref name="tier"/> consumes; null when no row covers it.</summary>
        public EnchantMaterial? Material(int tier) => _materials.TryGetValue(tier, out var material) ? material : null;

        /// <summary>The stone item and count of an attempt on <paramref name="item"/>; null when the tables name none.</summary>
        public bool TryMaterial(ItemDef item, out ItemDef stone, out int count)
        {
            stone = null!;
            count = 0;
            if (Material(TierOf(item)) is not { } material || Item(material.ItemId) is not { } found)
                return false;
            stone = found;
            count = material.Count;
            return true;
        }

        /// <summary>
        /// The checks of an attempt that every side can make from the tables (spec 5.3 M3 decision 10): an equipment item
        /// with a job, at its job's station, the job held, below +9. The station reach, combat, the slot and the stones are
        /// the server's (and the window's) own checks.
        /// </summary>
        public EnchantReason Check(ItemDef? item, NpcInteraction station, Func<string, bool> holdsJob, int level)
        {
            if (item == null || item.Category != ItemCategory.Equipment)
                return EnchantReason.NotEnchantable;
            string job = JobOf(item);
            if (job.Length == 0)
                return EnchantReason.NotEnchantable;
            if (StationOf(job) != station)
                return EnchantReason.WrongStation;
            if (!holdsJob(job))
                return EnchantReason.JobRequired;
            if (level >= EnchantLimits.MaxLevel)
                return EnchantReason.EnchantMaxed;
            return EnchantReason.None;
        }
    }
}
