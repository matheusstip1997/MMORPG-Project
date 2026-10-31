using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;

namespace Shared.Simulation.Crafting
{
    /// <summary>
    /// One input row of a forge definition: a mold slot (any material of <see cref="Tags"/> at <see cref="MinTier"/> or
    /// above) or a recipe input (exactly <see cref="ItemId"/>), <see cref="Count"/> units from one stack.
    /// </summary>
    public sealed class ForgeInputSpec
    {
        internal ForgeInputSpec(string nameId, string[] tags, int minTier, string itemId, int count)
        {
            NameId = nameId;
            Tags = tags;
            MinTier = minTier;
            ItemId = itemId;
            Count = count;
        }

        /// <summary>The slot's display name (<c>UiStrings</c>); empty for a recipe input.</summary>
        public string NameId { get; }

        public IReadOnlyList<string> Tags { get; }

        public int MinTier { get; }

        /// <summary>A recipe input's item; empty for a mold slot.</summary>
        public string ItemId { get; }

        public int Count { get; }

        /// <summary>Whether a material may fill this input (the tag and tier filter, or the exact item).</summary>
        public bool Takes(ItemDef item)
        {
            if (item.Category != ItemCategory.Material)
                return false;
            if (ItemId.Length > 0)
                return item.Id == ItemId;
            if (item.Tier < MinTier)
                return false;
            foreach (var tag in Tags)
            {
                if (Array.IndexOf(item.MaterialTags, tag) >= 0)
                    return true;
            }
            return false;
        }
    }

    /// <summary>A mold or a recipe as the forge runs it: what a session needs and what it makes.</summary>
    public sealed class ForgeDefinition
    {
        internal ForgeDefinition(ForgeKind kind, int netId, string id, NpcInteraction station, string jobId, int minJobLevel,
            int tier, RecipeType recipeType, ItemDef? output, int outputCount, IReadOnlyList<ForgeInputSpec> inputs,
            IReadOnlyList<ForgeActionDef> actions)
        {
            Kind = kind;
            NetId = netId;
            Id = id;
            Station = station;
            JobId = jobId;
            MinJobLevel = minJobLevel;
            Tier = tier;
            RecipeType = recipeType;
            Output = output;
            OutputCount = outputCount;
            Inputs = inputs;
            Actions = actions;
        }

        public ForgeKind Kind { get; }
        public int NetId { get; }

        /// <summary>The <c>MoldDef.id</c> (the mold item) or <c>RecipeDef.id</c>.</summary>
        public string Id { get; }

        public NpcInteraction Station { get; }
        public string JobId { get; }
        public int MinJobLevel { get; }
        public int Tier { get; }

        /// <summary><see cref="RecipeType.None"/> for a mold craft.</summary>
        public RecipeType RecipeType { get; }

        /// <summary>The item made; null for a mold that is not craftable yet.</summary>
        public ItemDef? Output { get; }

        public int OutputCount { get; }
        public IReadOnlyList<ForgeInputSpec> Inputs { get; }

        /// <summary>The forge actions allowed (none for a recipe).</summary>
        public IReadOnlyList<ForgeActionDef> Actions { get; }

        /// <summary>The mold item a mold craft needs in the Safe bag; empty for a recipe.</summary>
        public string MoldItemId => Kind == ForgeKind.Mold ? Id : string.Empty;

        /// <summary>Only mold crafts roll a quality (R-5.3-02); recipes make stacks or a mold item.</summary>
        public bool RollsQuality => Kind == ForgeKind.Mold;

        /// <summary>The job activity it pays XP for (R-5.1-04).</summary>
        public JobActivity Activity => RecipeType switch
        {
            RecipeType.Refine => JobActivity.Refine,
            RecipeType.Convert => JobActivity.Convert,
            _ => JobActivity.Craft,
        };

        public bool AllowsAction(int actionNetId)
        {
            foreach (var action in Actions)
            {
                if (action.NetId == actionNetId)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The forge tables as runtime data (spec 5.3 M3 implementation; WP-M3-6): molds and recipes by net id, items, actions,
    /// the quality caps and bands, material numbers and stats, conversion odds; built once from a validated config set.
    /// Read-only; shared by the server, the forge window and the bots, so every side checks a start alike.
    /// </summary>
    public sealed class ForgeRules
    {
        private readonly Dictionary<int, ForgeDefinition> _molds = new Dictionary<int, ForgeDefinition>();
        private readonly Dictionary<int, ForgeDefinition> _recipes = new Dictionary<int, ForgeDefinition>();
        private readonly List<ForgeDefinition> _ordered = new List<ForgeDefinition>();
        private readonly Dictionary<string, ItemDef> _items = new Dictionary<string, ItemDef>(StringComparer.Ordinal);
        private readonly Dictionary<int, ItemDef> _itemsByNetId = new Dictionary<int, ItemDef>();
        private readonly Dictionary<int, ForgeActionDef> _actions = new Dictionary<int, ForgeActionDef>();
        private readonly Dictionary<int, int> _tierQuality = new Dictionary<int, int>();
        private readonly Dictionary<string, List<MaterialStatDef>> _stats = new Dictionary<string, List<MaterialStatDef>>(StringComparer.Ordinal);
        private readonly Dictionary<int, ConversionOdds> _odds = new Dictionary<int, ConversionOdds>();
        private readonly Dictionary<string, byte> _rarityTiers = new Dictionary<string, byte>(StringComparer.Ordinal);
        private static readonly IReadOnlyList<MaterialStatDef> NoStats = new MaterialStatDef[0];

        private ForgeRules(ForgeSettings settings, QualityCapSet caps, QualityBands bands)
        {
            Settings = settings;
            Caps = caps;
            Bands = bands;
        }

        public ForgeSettings Settings { get; }
        public QualityCapSet Caps { get; }
        public QualityBands Bands { get; }

        /// <summary>Molds then recipes, each in table order (the forge window lists them so).</summary>
        public IReadOnlyList<ForgeDefinition> Definitions => _ordered;

        /// <summary>Builds the rules from a validated config set; null when a forge table is not loaded.</summary>
        public static ForgeRules? From(ConfigSet set)
        {
            var settings = ForgeSettings.Find(set);
            if (settings == null || !set.TryGet(typeof(MoldDef), out var molds) || !set.TryGet(typeof(RecipeDef), out var recipes)
                || !set.TryGet(typeof(QualityCaps), out var capRows) || !set.TryGet(typeof(QualityThresholds), out var thresholds)
                || !set.TryGet(typeof(ForgeActionDef), out _))
                return null;
            var caps = new List<QualityCaps>();
            foreach (var row in capRows.Rows)
                caps.Add((QualityCaps)row);
            if (!QualityCapSet.TryBuild(caps, out var capSet))
                return null;

            var rarities = new Dictionary<string, byte>(StringComparer.Ordinal);
            foreach (var rarity in NetIds.Rows<RarityDef>(set))
                rarities[rarity.Id] = (byte)rarity.Tier;
            var minQuality = new List<int>();
            var rarityTier = new List<byte>();
            var bonus = new List<int>();
            foreach (var row in thresholds.Rows)
            {
                var band = (QualityThresholds)row;
                if (!rarities.TryGetValue(band.RarityId, out byte tier))
                    return null;
                minQuality.Add(band.MinQuality);
                rarityTier.Add(tier);
                bonus.Add(band.StatBonusPercent);
            }
            if (minQuality.Count == 0)
                return null;

            var rules = new ForgeRules(settings, capSet, new QualityBands(minQuality.ToArray(), rarityTier.ToArray(), bonus.ToArray()));
            foreach (var pair in rarities)
                rules._rarityTiers[pair.Key] = pair.Value;
            foreach (var item in NetIds.Rows<ItemDef>(set))
            {
                rules._items[item.Id] = item;
                rules._itemsByNetId[item.NetId] = item;
            }
            foreach (var action in NetIds.Rows<ForgeActionDef>(set))
                rules._actions[action.NetId] = action;
            foreach (var tier in NetIds.Rows<MaterialTierDef>(set))
                rules._tierQuality[tier.Tier] = tier.ForgeQuality;
            foreach (var stat in NetIds.Rows<MaterialStatDef>(set))
            {
                if (!rules._stats.TryGetValue(stat.ItemId, out var list))
                    rules._stats[stat.ItemId] = list = new List<MaterialStatDef>();
                list.Add(stat);
            }
            foreach (var odds in NetIds.Rows<ConversionOdds>(set))
                rules._odds[odds.ToTier] = odds;

            foreach (var row in molds.Rows)
            {
                var mold = (MoldDef)row;
                var inputs = new List<ForgeInputSpec>();
                foreach (var slot in MoldDef.Slots(set, mold.Id))
                    inputs.Add(new ForgeInputSpec(slot.NameId, slot.Tags, slot.MinTier, string.Empty, slot.Count));
                var actions = new List<ForgeActionDef>();
                foreach (var actionId in mold.ForgeActionIds)
                {
                    var action = ForgeActionDef.Find(set, actionId);
                    if (action != null)
                        actions.Add(action);
                }
                ItemDef? output = mold.IsCraftable() ? rules.Item(mold.OutputItemId) : null;
                var definition = new ForgeDefinition(ForgeKind.Mold, mold.NetId, mold.Id, mold.Station, mold.JobId, mold.MinJobLevel,
                    mold.Tier, RecipeType.None, output, 1, inputs, actions);
                rules._molds[mold.NetId] = definition;
                rules._ordered.Add(definition);
            }
            foreach (var row in recipes.Rows)
            {
                var recipe = (RecipeDef)row;
                var inputs = new List<ForgeInputSpec>();
                foreach (var input in RecipeDef.Inputs(set, recipe.Id))
                    inputs.Add(new ForgeInputSpec(string.Empty, new string[0], 0, input.ItemId, input.Count));
                var definition = new ForgeDefinition(ForgeKind.Recipe, recipe.NetId, recipe.Id, recipe.Station, recipe.JobId,
                    recipe.MinJobLevel, recipe.Tier, recipe.Type, rules.Item(recipe.OutputItemId), recipe.OutputCount, inputs,
                    new ForgeActionDef[0]);
                rules._recipes[recipe.NetId] = definition;
                rules._ordered.Add(definition);
            }
            return rules;
        }

        public bool TryGet(ForgeKind kind, int netId, out ForgeDefinition definition)
        {
            definition = null!;
            var map = kind == ForgeKind.Mold ? _molds : kind == ForgeKind.Recipe ? _recipes : null;
            if (map == null || !map.TryGetValue(netId, out var found))
                return false;
            definition = found;
            return true;
        }

        public ItemDef? Item(string id) => _items.TryGetValue(id, out var item) ? item : null;

        public ItemDef? Item(int netId) => _itemsByNetId.TryGetValue(netId, out var item) ? item : null;

        public ForgeActionDef? Action(int netId) => _actions.TryGetValue(netId, out var action) ? action : null;

        /// <summary>The <c>RarityDef.tier</c> of a rarity id (0 when unknown).</summary>
        public byte RarityTier(string rarityId) => _rarityTiers.TryGetValue(rarityId, out byte tier) ? tier : (byte)0;

        /// <summary>The conversion odds into <paramref name="toTier"/>, or null.</summary>
        public ConversionOdds? Odds(int toTier) => _odds.TryGetValue(toTier, out var odds) ? odds : null;

        /// <summary>The material number of one unit (<c>MaterialTierDef.forgeQuality</c> of its tier; R-5.3-02).</summary>
        public int MaterialQuality(ItemDef material) => _tierQuality.TryGetValue(material.Tier, out int quality) ? quality : 0;

        public IReadOnlyList<MaterialStatDef> Stats(string itemId) => _stats.TryGetValue(itemId, out var stats) ? stats : NoStats;

        /// <summary>The conversion's output tier for a convert recipe; 0 otherwise.</summary>
        public static int ConversionTier(ForgeDefinition definition) =>
            definition.RecipeType == RecipeType.Convert && definition.Output != null ? definition.Output.Tier : 0;

        /// <summary>
        /// The job and the definition's own gates, in the server's order: craftable, station, job, job level.
        /// <paramref name="jobLevel"/> is 0 when the character does not hold the job.
        /// </summary>
        public static ForgeReason CheckDefinition(ForgeDefinition definition, NpcInteraction station, int jobLevel)
        {
            if (definition.Output == null)
                return ForgeReason.RecipeUnavailable;
            if (definition.Station != station)
                return ForgeReason.WrongStation;
            if (jobLevel <= 0)
                return ForgeReason.JobRequired;
            if (jobLevel < definition.MinJobLevel)
                return ForgeReason.JobLevelTooLow;
            return ForgeReason.None;
        }

        /// <summary>
        /// Whether input <paramref name="index"/> may be filled from a stack of <paramref name="item"/> with
        /// <paramref name="freeUnits"/> unreserved units (null item: an empty slot).
        /// </summary>
        public static ForgeReason CheckInput(ForgeDefinition definition, int index, ItemDef? item, int freeUnits)
        {
            if (index < 0 || index >= definition.Inputs.Count || item == null || !definition.Inputs[index].Takes(item))
                return ForgeReason.SlotMaterialInvalid;
            return freeUnits < definition.Inputs[index].Count ? ForgeReason.MaterialsMissing : ForgeReason.None;
        }

        /// <summary>
        /// Whether one more action may be used, <paramref name="used"/> being the session's count so far and
        /// <paramref name="extra"/> the job tree's actions beyond <c>ForgeSettings.maxActions</c>.
        /// </summary>
        public ForgeReason CheckAction(ForgeDefinition definition, int actionNetId, int used, int extra = 0)
        {
            if (!definition.AllowsAction(actionNetId))
                return ForgeReason.ActionNotAllowed;
            return used >= Math.Min(Settings.MaxActions + Math.Max(0, extra), ForgeStations.MaxSessionActions)
                ? ForgeReason.ActionLimitReached : ForgeReason.None;
        }

        /// <summary>The material number of a session: every input unit's tier number (R-5.3-02 "material numbers").</summary>
        public int MaterialNumber(ForgeDefinition definition, IReadOnlyList<ItemDef> inputs)
        {
            long sum = 0;
            for (int i = 0; i < inputs.Count && i < definition.Inputs.Count; i++)
                sum += (long)MaterialQuality(inputs[i]) * definition.Inputs[i].Count;
            return sum > int.MaxValue ? int.MaxValue : (int)sum;
        }

        /// <summary>The quality range a session can still end in (<see cref="QualityCalc.Range"/>), for the preview.</summary>
        public void Preview(ForgeDefinition definition, IReadOnlyList<ItemDef> inputs, IReadOnlyList<int> actionNetIds, int jobTree,
            int minigameMax, out int min, out int max)
        {
            if (!definition.RollsQuality)
            {
                min = max = 0;
                return;
            }
            long actionsMin = 0, actionsMax = 0;
            foreach (int netId in actionNetIds)
            {
                var action = Action(netId);
                if (action == null)
                    continue;
                actionsMin += action.QualityMin;
                actionsMax += action.QualityMax;
            }
            QualityCalc.Range(MaterialNumber(definition, inputs), Clamp(actionsMin), Clamp(actionsMax), jobTree, minigameMax, Caps,
                out min, out max);
        }

        /// <summary>The crafted stats of an equipment output (<see cref="CraftedStatsCalc"/>); empty for other outputs.</summary>
        public List<ItemStatBonus> CraftedStats(ForgeDefinition definition, IReadOnlyList<ItemDef> inputs, int quality)
        {
            if (!definition.RollsQuality || definition.Output == null || definition.Output.Category != ItemCategory.Equipment)
                return new List<ItemStatBonus>();
            var uses = new List<MaterialStatUse>();
            for (int i = 0; i < inputs.Count && i < definition.Inputs.Count; i++)
            {
                foreach (var stat in Stats(inputs[i].Id))
                    uses.Add(new MaterialStatUse(stat.StatId, stat.Flat, stat.Percent, definition.Inputs[i].Count));
            }
            return CraftedStatsCalc.Build(uses, Bands.StatBonusPercent(quality));
        }

        private static int Clamp(long value) => value > int.MaxValue ? int.MaxValue : (int)value;
    }
}
