using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Crafting;
using Shared.Simulation.Jobs;
using Shared.Util;

namespace EconomySimulator;

/// <summary>The named M3 sinks and flows of a scenario's report (spec 12.5, 10.2; R-4.2-04; WP-M3-12).</summary>
public static class M3Names
{
    /// <summary>Vendor tools and molds, in gold: once per character (tools never wear out, R-5.2-02; molds are reusable, R-5.3-01).</summary>
    public const string VendorToolsAndMolds = "vendorToolsAndMolds";

    /// <summary>Vendor goods used up, in units (spec 3.4).</summary>
    public const string ConsumableUse = "consumableUse";

    /// <summary>Inputs of failed conversions, in units (R-5.2-04).</summary>
    public const string FailedConversions = "failedConversions";

    /// <summary>Enchant stones, in units: every try consumes them (R-5.3-04).</summary>
    public const string LostEnchantStones = "lostEnchantStones";

    /// <summary>Gems lost to failed sockets, in units (R-5.3-05).</summary>
    public const string FailedSockets = "failedSockets";

    /// <summary>Rough gems paid for gem removal, in units (R-5.3-05).</summary>
    public const string GemRemovalCost = "gemRemovalCost";

    public const string Harvests = "harvests";
    public const string HarvestUnits = "harvestUnits";
    public const string LootMaterials = "lootMaterials";
    public const string ConversionsWon = "conversionsWon";
    public const string EnchantTries = "enchantTries";
    public const string SocketsWon = "socketsWon";
    public const string GemsRemoved = "gemsRemoved";

    public static readonly IReadOnlyList<string> Sinks =
        [VendorToolsAndMolds, ConsumableUse, FailedConversions, LostEnchantStones, FailedSockets, GemRemovalCost];

    public static readonly IReadOnlyList<string> Flows =
        [Harvests, HarvestUnits, LootMaterials, ConversionsWon, EnchantTries, SocketsWon, GemsRemoved];

    public static string UnitOf(string sink) => sink == VendorToolsAndMolds ? "gold" : "units";
}

/// <summary>What the M3 activity of a run moved since the last control step.</summary>
public sealed class M3Counters
{
    private readonly Dictionary<string, long> _values = new(StringComparer.Ordinal);

    public void Add(string name, long value) => _values[name] = _values.GetValueOrDefault(name) + value;

    public long Take(string name)
    {
        long value = _values.GetValueOrDefault(name);
        _values[name] = 0;
        return value;
    }
}

/// <summary>A tier conversion recipe (R-5.2-04) with its odds row: one input item, T to T+1.</summary>
public sealed record SimConversion(string RecipeId, int MinJobLevel, int ToTier, int SuccessPercent, int DailyCap, int InputNetId,
    string InputId, int InputCount, int OutputNetId, string OutputId, int OutputCount);

/// <summary>A cut (the Jeweler's refine recipe, R-5.3-06): rough gems into a cut gem of one kind.</summary>
public sealed record SimCut(int RoughNetId, int InputCount, int GemNetId, int OutputCount);

/// <summary>One gem tier's socket odds and removal cost (<c>GemSocketOdds</c>) and its cuts.</summary>
public sealed record SimGemTier(int Tier, int SuccessPercent, int MinJobLevel, int RemovalCostNetId, int RemovalCostCount, IReadOnlyList<SimCut> Cuts);

/// <summary>A player's materials and its progress through the M3 activities in one run.</summary>
public sealed class CraftingPlayer(int recentWindow)
{
    public SortedDictionary<int, long> Bag { get; } = new();
    public JobRecentWindow Recent { get; } = new(Math.Max(1, recentWindow));

    /// <summary>The next node pool to harvest, in round robin over the row's pools (players walk between the nodes).</summary>
    public int NodeTurn { get; set; }

    /// <summary>The next kind to cut (rows of one tier, in round robin).</summary>
    public int CutTurn { get; set; }

    public int EnchantLevel { get; set; }
    public long Day { get; set; } = -1;
    public Dictionary<int, int> TriesToday { get; } = new();
    public SortedDictionary<int, long> Socketed { get; } = new();
    public HashSet<string> OneTimeBought { get; } = new(StringComparer.Ordinal);
    public long ConversionCarry { get; set; }
    public long EnchantCarry { get; set; }
    public long SocketCarry { get; set; }
    public long RemovalCarry { get; set; }

    public long Held(int netId) => Bag.GetValueOrDefault(netId);

    public void Add(int netId, long count) => Bag[netId] = Held(netId) + count;

    public void Remove(int netId, long count) => Bag[netId] = Held(netId) - count;
}

/// <summary>
/// The M3 item sinks as the simulator plays them (spec 12.5, 10.2, R-4.2-04; WP-M3-12), from the real tables and with the
/// shared math the server rolls: <c>ConversionOdds</c> and <c>RecipeDef</c> through <see cref="ForgeRolls"/> (the daily cap per
/// tier and UTC day, both outcomes count), <c>EnchantOdds</c> and <c>EnchantMaterial</c> through <see cref="EnchantMath"/>
/// (a tier 1 item), <c>GemSocketOdds</c> through <see cref="GemMath"/>. Rough gems are cut at each visit (<c>RecipeDef</c> refine
/// rows of the bench). No job tree bonus here: the Monte Carlos report the bonus caps. A player is taken to hold the jobs and
/// the higher-tier tools of what its row plays (a Blacksmith with a Jeweler secondary).
/// </summary>
public sealed class SimCrafting
{
    private const long HourMs = 3_600_000;
    private static readonly int[] NoActions = [];

    private readonly EnchantWeights[] _weights;

    private SimCrafting(IReadOnlyList<SimConversion> conversions, IReadOnlyList<SimGemTier> gemTiers, EnchantWeights[] weights, int stoneNetId,
        int stonesPerTry)
    {
        Conversions = conversions;
        GemTiers = gemTiers;
        _weights = weights;
        StoneNetId = stoneNetId;
        StonesPerTry = stonesPerTry;
    }

    /// <summary>Highest output tier first, then table order, so every tier of the ladder gets its tries.</summary>
    public IReadOnlyList<SimConversion> Conversions { get; }

    /// <summary>Highest tier first.</summary>
    public IReadOnlyList<SimGemTier> GemTiers { get; }

    public int StoneNetId { get; }
    public int StonesPerTry { get; }

    /// <summary>The row plays the M3 activities (M2 rows do not, and their results stay as they were for their seeds).</summary>
    public static bool Plays(EconomySimScenario row) =>
        row.HarvestsPerPlayerHour > 0 || row.OneTimeBuyItemIds.Length > 0 || row.ConversionsPerPlayerHour > 0
        || row.EnchantTriesPerPlayerHour > 0 || row.SocketsPerPlayerHour > 0 || row.GemRemovalsPerPlayerHour > 0;

    /// <summary>The convert recipes with one input (the rule's "T to T+1", R-5.2-04), in table order.</summary>
    public static List<SimConversion> ConversionsFrom(ConfigSet tables)
    {
        var items = SimEconomy.Rows<ItemDef>(tables).ToDictionary(i => i.Id, StringComparer.Ordinal);
        var odds = SimEconomy.Rows<ConversionOdds>(tables).ToDictionary(o => o.ToTier);
        var conversions = new List<SimConversion>();
        foreach (var recipe in SimEconomy.Rows<RecipeDef>(tables).Where(r => r.Type == RecipeType.Convert))
        {
            var inputs = RecipeDef.Inputs(tables, recipe.Id);
            int toTier = recipe.Tier + 1;
            if (inputs.Count != 1 || !odds.TryGetValue(toTier, out var row))
                continue;
            conversions.Add(new SimConversion(recipe.Id, recipe.MinJobLevel, toTier, row.SuccessPercent, row.DailyCap, items[inputs[0].ItemId].NetId,
                inputs[0].ItemId, inputs[0].Count, items[recipe.OutputItemId].NetId, recipe.OutputItemId, recipe.OutputCount));
        }
        return conversions;
    }

    /// <summary>The crafting tables in the simulator's form; null when a table is missing.</summary>
    public static SimCrafting? From(ConfigSet tables)
    {
        var items = SimEconomy.Rows<ItemDef>(tables).ToDictionary(i => i.Id, StringComparer.Ordinal);
        var odds = new EnchantWeights[EnchantLimits.MaxLevel];
        int levels = 0;
        foreach (var row in SimEconomy.Rows<EnchantOdds>(tables))
        {
            odds[row.Level] = EnchantMath.Weights(row, 0);
            levels++;
        }
        var stone = SimEconomy.Rows<EnchantMaterial>(tables).FirstOrDefault(m => m.Tier == 1);
        if (levels != EnchantLimits.MaxLevel || stone == null)
            return null;

        var gems = SimEconomy.Rows<GemDef>(tables).ToDictionary(g => g.Id, StringComparer.Ordinal);
        var cuts = new Dictionary<int, List<SimCut>>();
        foreach (var recipe in SimEconomy.Rows<RecipeDef>(tables).Where(r => r.Type == RecipeType.Refine && gems.ContainsKey(r.OutputItemId)))
        {
            var inputs = RecipeDef.Inputs(tables, recipe.Id);
            if (inputs.Count != 1)
                continue;
            int tier = gems[recipe.OutputItemId].Tier;
            if (!cuts.TryGetValue(tier, out var list))
                cuts.Add(tier, list = []);
            list.Add(new SimCut(items[inputs[0].ItemId].NetId, inputs[0].Count, items[recipe.OutputItemId].NetId, recipe.OutputCount));
        }
        var tiers = SimEconomy.Rows<GemSocketOdds>(tables).OrderByDescending(o => o.Tier).Select(o => new SimGemTier(o.Tier, o.SuccessPercent,
            o.MinJobLevel, items[o.RemovalCostItemId].NetId, o.RemovalCostCount, cuts.GetValueOrDefault(o.Tier) ?? [])).ToList();
        var conversions = ConversionsFrom(tables).OrderByDescending(c => c.ToTier).ToList(); // stable: table order within a tier
        return new SimCrafting(conversions, tiers, odds, items[stone.ItemId].NetId, stone.Count);
    }

    /// <summary>The activity one outpost visit spends the time since the last one on, as far as what the player holds allows.</summary>
    public void Visit(CraftingPlayer player, EconomySimScenario row, long elapsedMs, long nowMs, IRng rng, M3Counters counters)
    {
        Cut(player, row);
        long tries = Due(elapsedMs, row.ConversionsPerPlayerHour, player.ConversionCarry, out long carry);
        player.ConversionCarry = carry;
        Convert(player, row.JobLevel, tries, nowMs, rng, counters);

        tries = Due(elapsedMs, row.EnchantTriesPerPlayerHour, player.EnchantCarry, out carry);
        player.EnchantCarry = carry;
        Enchant(player, tries, rng, counters);

        tries = Due(elapsedMs, row.SocketsPerPlayerHour, player.SocketCarry, out carry);
        player.SocketCarry = carry;
        Socket(player, row.JobLevel, tries, rng, counters);

        tries = Due(elapsedMs, row.GemRemovalsPerPlayerHour, player.RemovalCarry, out carry);
        player.RemovalCarry = carry;
        RemoveGems(player, tries, rng, counters);
    }

    private static long Due(long elapsedMs, int perHour, long carry, out long left)
    {
        long total = elapsedMs * perHour + carry;
        left = total % HourMs;
        return total / HourMs;
    }

    /// <summary>A failed conversion consumes its inputs (R-5.2-04); a tier's daily cap counts both outcomes.</summary>
    private void Convert(CraftingPlayer player, int jobLevel, long tries, long nowMs, IRng rng, M3Counters counters)
    {
        long day = ForgeRolls.UtcDay(nowMs);
        if (day != player.Day)
        {
            player.Day = day;
            player.TriesToday.Clear();
        }
        for (long i = 0; i < tries; i++)
        {
            var conversion = Conversions.FirstOrDefault(c => c.MinJobLevel <= jobLevel && player.Held(c.InputNetId) >= c.InputCount
                                                             && player.TriesToday.GetValueOrDefault(c.ToTier) < c.DailyCap);
            if (conversion == null)
                return;
            player.Remove(conversion.InputNetId, conversion.InputCount);
            player.TriesToday[conversion.ToTier] = player.TriesToday.GetValueOrDefault(conversion.ToTier) + 1;
            if (ForgeRolls.Draw(NextSeed(rng), NoActions, NoActions, 0, 0, conversion.SuccessPercent).Success)
            {
                player.Add(conversion.OutputNetId, conversion.OutputCount);
                counters.Add(M3Names.ConversionsWon, 1);
            }
            else
            {
                counters.Add(M3Names.FailedConversions, conversion.InputCount);
            }
        }
    }

    /// <summary>Every try consumes its stones (R-5.3-04). An item at +9 is done: the next try starts on a new item at +0.</summary>
    private void Enchant(CraftingPlayer player, long tries, IRng rng, M3Counters counters)
    {
        for (long i = 0; i < tries && player.Held(StoneNetId) >= StonesPerTry; i++)
        {
            player.Remove(StoneNetId, StonesPerTry);
            counters.Add(M3Names.LostEnchantStones, StonesPerTry);
            counters.Add(M3Names.EnchantTries, 1);
            if (player.EnchantLevel >= EnchantLimits.MaxLevel)
                player.EnchantLevel = 0;
            player.EnchantLevel = EnchantMath.Apply(player.EnchantLevel, EnchantMath.Outcome(_weights[player.EnchantLevel], rng.NextInt(100)));
        }
    }

    /// <summary>The Jeweler cuts every rough gem above the removal cost it keeps, each tier's kinds in turn (R-5.3-06).</summary>
    private void Cut(CraftingPlayer player, EconomySimScenario row)
    {
        foreach (var tier in GemTiers)
        {
            if (tier.Cuts.Count == 0 || tier.MinJobLevel > row.JobLevel || row.SocketsPerPlayerHour <= 0)
                continue;
            long keep = row.GemRemovalsPerPlayerHour > 0 ? tier.RemovalCostCount : 0;
            while (true)
            {
                var cut = tier.Cuts[player.CutTurn % tier.Cuts.Count];
                if (player.Held(cut.RoughNetId) - cut.InputCount < keep)
                    break;
                player.Remove(cut.RoughNetId, cut.InputCount);
                player.Add(cut.GemNetId, cut.OutputCount);
                player.CutTurn++;
            }
        }
    }

    /// <summary>A failed socket consumes the gem and leaves the item untouched (R-5.3-05); a won one holds it in the item.</summary>
    private void Socket(CraftingPlayer player, int jobLevel, long tries, IRng rng, M3Counters counters)
    {
        for (long i = 0; i < tries; i++)
        {
            var tier = GemTiers.FirstOrDefault(t => t.MinJobLevel <= jobLevel && t.Cuts.Any(c => player.Held(c.GemNetId) > 0));
            if (tier == null)
                return;
            int gem = tier.Cuts.First(c => player.Held(c.GemNetId) > 0).GemNetId;
            player.Remove(gem, 1);
            if (GemMath.Draw(NextSeed(rng), GemMath.SocketChance(tier.SuccessPercent, 0), 0, 0).Success)
            {
                player.Socketed[gem] = player.Socketed.GetValueOrDefault(gem) + 1;
                counters.Add(M3Names.SocketsWon, 1);
            }
            else
            {
                counters.Add(M3Names.FailedSockets, 1);
            }
        }
    }

    /// <summary>Removal returns the gem intact for its rough gem cost (R-5.3-05; user answer 2026-10-31).</summary>
    private void RemoveGems(CraftingPlayer player, long tries, IRng rng, M3Counters counters)
    {
        for (long i = 0; i < tries; i++)
        {
            SimGemTier? tier = null;
            int gem = 0;
            foreach (var candidate in GemTiers.Where(t => player.Held(t.RemovalCostNetId) >= t.RemovalCostCount))
            {
                var held = candidate.Cuts.FirstOrDefault(c => player.Socketed.GetValueOrDefault(c.GemNetId) > 0);
                if (held != null)
                {
                    tier = candidate;
                    gem = held.GemNetId;
                    break;
                }
            }
            if (tier == null)
                return;
            int cost = GemMath.Draw(NextSeed(rng), 0, tier.RemovalCostCount, 0).RemovalCost;
            player.Remove(tier.RemovalCostNetId, cost);
            player.Socketed[gem]--;
            player.Add(gem, 1);
            counters.Add(M3Names.GemRemovalCost, cost);
            counters.Add(M3Names.GemsRemoved, 1);
        }
    }

    private static ulong NextSeed(IRng rng) => ((ulong)rng.NextUInt() << 32) | rng.NextUInt();
}
