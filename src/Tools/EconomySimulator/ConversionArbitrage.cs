using System.Globalization;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Crafting;
using Shared.Simulation.Gathering;
using Shared.Util;

namespace EconomySimulator;

/// <summary>
/// One route to a higher tier item (R-5.2-04): <see cref="FromItem"/> converted up into one <see cref="ToItem"/>, at one level of
/// the job tree's bonuses. Costs are seconds of gathering labour, and gold at the row's gold per player-hour. A material no
/// vendor sells is priced at the labour it embodies; <see cref="VendorGoldPerInput"/> is set when a vendor sells it.
/// </summary>
public sealed record ArbitrageRoute(string ToItem, int ToTier, bool ReachesTopTier, string FromItem, int OddsBonusPoints, int KeepPercent, double ExpectedInputs, double MeanInputs,
    long P50Inputs, long P95Inputs, double DaysAtCap, double? GatheredSeconds, string GatheredVia, double? InputSeconds, string InputVia, int? VendorGoldPerInput,
    double? GatheredGold, double? RouteGold)
{
    /// <summary>The route's labour: the inputs it consumes times each one's labour; null when no node yields the input.</summary>
    public double? RouteSeconds => InputSeconds is { } seconds ? seconds * MeanInputs : null;

    /// <summary>
    /// Buying wins: the route costs less than gathering the item. Compared in gold when a vendor sells the input (the only real
    /// price in M3), else in labour. False when the item has no node (the route is the only way) or the input has no price.
    /// </summary>
    public bool BuyingWins =>
        VendorGoldPerInput != null
            ? GatheredGold is { } gathered && RouteGold is { } route && route < gathered
            : GatheredSeconds is { } seconds && RouteSeconds is { } routeSeconds && routeSeconds < seconds;

    /// <summary>The share of its labour the input would have to sell at for the route to break even, in percent; null without a labour on both sides.</summary>
    public double? BreakEvenPercent => GatheredSeconds is { } seconds && RouteSeconds is { } route && route > 0 ? seconds * 100 / route : null;
}

/// <summary>
/// The conversion arbitrage Monte Carlo of the M3 economy (spec 10.2; R-5.2-04; WP-M3-12): can a lower-tier material bought and
/// converted beat the higher tier gathered? For every convert chain of <c>RecipeDef</c> and every step range (T1 to T2, T2 to T3,
/// T1 to T3): seeded trials with the server's own rolls (<see cref="ForgeRolls"/>) count the inputs one output consumes, without
/// the job tree and at its <c>JobBonusCap</c>s. Never tunes a value.
/// <para>
/// No vendor sells a material in M3 and contracts are M4, so a material has no price yet. The cost is gathering labour, from the
/// tables alone: one harvest takes the longer of the harvest action and <c>respawnMs</c> over the nodes of that type (one player
/// with every node to itself), over the units a harvest yields (<see cref="HarvestMath"/>, no repeats). A bought input is priced at
/// the labour it embodies, or at its <c>NpcVendorStock</c> price if a vendor ever sells it. TODO(open): contract prices (M4).
/// Limits: walking is not counted; monster drops feed both routes and are left out.
/// </para>
/// </summary>
public static class ConversionArbitrage
{
    /// <summary>Trials per route (the tool's own sample size, not game data).</summary>
    public const int Trials = 2000;

    /// <summary>Harvest rolls per node table for the expected units of a harvest.</summary>
    public const int HarvestTrials = 20_000;

    /// <summary>The <c>Pcg32Rng</c> stream of the trials.</summary>
    private const ulong Stream = 0x41524249UL; // "ARBI"

    private static readonly int[] NoActions = [];

    /// <summary>The inputs consumed per output of one try: <paramref name="count"/> inputs, a failure keeps them with the keep chance.</summary>
    public static double ExpectedInputs(int count, int outputCount, int successPercent, int keepPercent)
    {
        double success = successPercent / 100.0, keep = keepPercent / 100.0;
        return count * (1 - (1 - success) * keep) / (success * outputCount);
    }

    /// <summary>The expected first inputs per last output of a chain, cheapest step first.</summary>
    public static double ExpectedChainInputs(IReadOnlyList<ChainStep> steps)
    {
        double inputs = 1;
        foreach (var step in steps)
            inputs *= ExpectedInputs(step.InputCount, step.OutputCount, step.SuccessPercent, step.KeepPercent);
        return inputs;
    }

    /// <summary>One conversion of a chain with its odds after the bonus.</summary>
    public sealed record ChainStep(int InputCount, int OutputCount, int SuccessPercent, int KeepPercent, int DailyCap);

    /// <summary>The inputs (first step's item) each trial consumed for one output, and the tries per day-capped step.</summary>
    public static (List<long> Inputs, double DaysAtCap) Trial(IReadOnlyList<ChainStep> steps, ulong seed, int trials = Trials)
    {
        var rng = new Pcg32Rng(seed, Stream);
        var inputs = new List<long>(trials);
        var attempts = new long[steps.Count];
        for (int trial = 0; trial < trials; trial++)
        {
            var stock = new long[steps.Count + 1];
            inputs.Add(Make(steps, steps.Count, stock, attempts, rng));
        }
        double days = 0;
        for (int i = 0; i < steps.Count; i++)
            days = Math.Max(days, (double)attempts[i] / trials / steps[i].DailyCap);
        return (inputs, days);
    }

    /// <summary>Makes one unit at <paramref name="level"/> (0: one input bought), spending the stock left by earlier tries first.</summary>
    private static long Make(IReadOnlyList<ChainStep> steps, int level, long[] stock, long[] attempts, Pcg32Rng rng)
    {
        if (level == 0)
            return 1;
        if (stock[level] > 0)
        {
            stock[level]--;
            return 0;
        }
        var step = steps[level - 1];
        long spent = 0;
        long held = 0;
        while (true)
        {
            while (held < step.InputCount)
            {
                spent += Make(steps, level - 1, stock, attempts, rng);
                held++;
            }
            attempts[level - 1]++;
            ulong seed = ((ulong)rng.NextUInt() << 32) | rng.NextUInt();
            var roll = ForgeRolls.Draw(seed, NoActions, NoActions, 0, 0, step.SuccessPercent, step.KeepPercent);
            if (roll.Success)
            {
                stock[level] += step.OutputCount - 1;
                return spent;
            }
            if (!roll.InputsKept)
                held = 0;
        }
    }

    /// <summary>
    /// Every route at every bonus level. <paramref name="jobLevel"/> is the harvester's (the yield bonus and the entries it reaches);
    /// <paramref name="goldPerPlayerHour"/> values the labour in gold (0: gold is not reported).
    /// </summary>
    public static IReadOnlyList<ArbitrageRoute> Run(SimEconomy economy, ulong seed, int jobLevel, double goldPerPlayerHour)
    {
        var tables = economy.Tables;
        var rules = ForgeRules.From(tables);
        if (rules == null || economy.Harvest == null)
            return [];
        var conversions = SimCrafting.ConversionsFrom(tables);
        var byOutput = conversions.GroupBy(c => c.OutputId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var harvest = HarvestLabour(economy, jobLevel, seed);
        var vendor = SimEconomy.Rows<NpcVendorStock>(tables).GroupBy(s => s.ItemId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(s => s.PriceGold), StringComparer.Ordinal);
        var refines = SimEconomy.Rows<RecipeDef>(tables).Where(r => r.Type == RecipeType.Refine)
            .GroupBy(r => r.OutputItemId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        int oddsCap = SimEconomy.Rows<JobBonusCap>(tables).FirstOrDefault(c => c.Kind == Shared.Simulation.Jobs.JobBonusKind.ConversionOddsPercent)?.Cap ?? 0;
        int keepCap = SimEconomy.Rows<JobBonusCap>(tables).FirstOrDefault(c => c.Kind == Shared.Simulation.Jobs.JobBonusKind.KeepInputsPercent)?.Cap ?? 0;
        var levels = new List<(int Odds, int Keep)> { (0, 0) };
        if (oddsCap > 0 || keepCap > 0)
            levels.Add((oddsCap, keepCap));

        var routes = new List<ArbitrageRoute>();
        int topTier = conversions.Count == 0 ? 0 : conversions.Max(c => c.ToTier);
        ulong trialSeed = seed;
        double goldPerSecond = goldPerPlayerHour / 3600;
        foreach (var conversion in conversions)
        {
            // Walk down the ladder from this output: its input, the input's input, and so on.
            var chain = new List<SimConversion>();
            for (var step = conversion; step != null; step = byOutput.GetValueOrDefault(step.InputId))
            {
                chain.Insert(0, step);
                foreach (var (odds, keep) in levels)
                {
                    var steps = chain.Select(c => new ChainStep(c.InputCount, c.OutputCount, rules.ConversionPercent(c.SuccessPercent, odds), keep, c.DailyCap)).ToList();
                    var (inputs, days) = Trial(steps, trialSeed++);
                    inputs.Sort();
                    double mean = inputs.Average();
                    var (gathered, gatheredVia) = Labour(conversion.OutputId, harvest, refines, tables);
                    var (input, inputVia) = Labour(step.InputId, harvest, refines, tables);
                    int? price = vendor.TryGetValue(step.InputId, out int gold) ? gold : null;
                    routes.Add(new ArbitrageRoute(conversion.OutputId, conversion.ToTier, conversion.ToTier == topTier, step.InputId, odds, keep,
                        ExpectedChainInputs(steps), mean, inputs[inputs.Count / 2],
                        inputs[Math.Clamp((int)Math.Ceiling(inputs.Count * 0.95) - 1, 0, inputs.Count - 1)], days, gathered, gatheredVia, input,
                        inputVia, price, gathered is { } g && goldPerSecond > 0 ? g * goldPerSecond : null,
                        price is { } p ? p * mean : input is { } i && goldPerSecond > 0 ? i * mean * goldPerSecond : null));
                }
            }
        }
        return routes;
    }

    /// <summary>
    /// The verdict of <c>--check</c> (R-5.2-04, plan section 6: "can T1 bought into T3 beat T3 gathered?"): no route into the top
    /// tier beats gathering it, at any bonus level. A structural flaw when one does.
    /// </summary>
    public static SimVerdict Check(IReadOnlyList<ArbitrageRoute> routes)
    {
        if (routes.Count == 0)
            return new SimVerdict("conversion arbitrage", false, false, "no conversion or harvest tables loaded");
        var top = routes.Where(r => r.ReachesTopTier).ToList();
        var flawed = top.Where(r => r.BuyingWins).ToList();
        if (flawed.Count > 0)
            return new SimVerdict("conversion arbitrage", false, false, "buying wins: " + string.Join("; ", flawed.Select(Summary)));
        var closest = top.Where(r => r.BreakEvenPercent != null).OrderByDescending(r => r.BreakEvenPercent).FirstOrDefault();
        return new SimVerdict("conversion arbitrage", true, false, FormattableString.Invariant($"gathering the top tier beats buying and converting on all {top.Count} routes")
            + (closest == null ? "" : "; closest: " + Summary(closest)));
    }

    /// <summary>
    /// A warning for the legs below the top tier: a route that beats gathering its middle-tier item is reported, never a failure.
    /// It does not reach the question of R-5.2-04 and the whole number set is a placeholder (spec 12.5), so the user decides.
    /// </summary>
    public static SimVerdict CheckLegs(IReadOnlyList<ArbitrageRoute> routes)
    {
        var winning = routes.Where(r => !r.ReachesTopTier && r.BuyingWins).ToList();
        return new SimVerdict("conversion legs below the top tier", winning.Count == 0, true, winning.Count == 0
            ? "gathering wins on every leg"
            : "buying wins: " + string.Join("; ", winning.Select(Summary)));
    }

    private static string Summary(ArbitrageRoute r) => FormattableString.Invariant(
        $"{r.FromItem} to {r.ToItem} (tree +{r.OddsBonusPoints} pp, keep {r.KeepPercent}%): {r.MeanInputs:0.#} inputs, {r.RouteSeconds ?? 0:0} s against {r.GatheredSeconds ?? 0:0} s gathered, break-even at {r.BreakEvenPercent ?? 0:0.#}% of the input's labour");

    public static void Print(IReadOnlyList<ArbitrageRoute> routes, TextWriter output, bool verdictOnly, double goldPerPlayerHour)
    {
        var verdict = Check(routes);
        var c = CultureInfo.InvariantCulture;
        output.WriteLine();
        output.WriteLine("== conversion arbitrage (ConversionOdds, R-5.2-04; cost in gathering labour, node labour only: walking and monster drops left out) ==");
        if (!verdictOnly)
        {
            output.WriteLine(string.Create(c, $"{Trials} trials per route; gold at {goldPerPlayerHour:0} gold per player-hour"));
            output.WriteLine("  route                                 tree/keep  inputs (expected, p50, p95)   days at cap  gathered via               route labour  break-even");
            foreach (var r in routes)
            {
                string gold = r.GatheredGold is { } g && r.RouteGold is { } route ? string.Create(c, $" [{g:0.#} g vs {route:0.#} g]") : "";
                output.WriteLine(string.Create(c,
                    $"  {r.FromItem + " > " + r.ToItem,-36}  +{r.OddsBonusPoints,2}/{r.KeepPercent,2}%   {r.MeanInputs,7:0.0} ({r.ExpectedInputs:0.0}, {r.P50Inputs}, {r.P95Inputs})  {r.DaysAtCap,9:0.0} d  {r.GatheredSeconds ?? 0,6:0} s {r.GatheredVia,-16}  {r.RouteSeconds ?? 0,8:0} s  {r.BreakEvenPercent ?? 0,6:0.#}%{gold}"));
            }
        }
        output.WriteLine($"[{(verdict.Pass ? "PASS" : "FAIL")}] {verdict.Name}: {verdict.Detail}");
        var legs = CheckLegs(routes);
        output.WriteLine($"[{(legs.Pass ? "PASS" : "WARN")}] {legs.Name}: {legs.Detail}");
    }

    /// <summary>The labour of one unit of an item: its best node, else through its refine recipe's input; null when neither gives it.</summary>
    private static (double? Seconds, string Via) Labour(string itemId, IReadOnlyDictionary<string, (double Seconds, string Node)> harvest,
        IReadOnlyDictionary<string, RecipeDef> refines, ConfigSet tables)
    {
        if (harvest.TryGetValue(itemId, out var direct))
            return (direct.Seconds, direct.Node);
        if (refines.TryGetValue(itemId, out var recipe))
        {
            var inputs = RecipeDef.Inputs(tables, recipe.Id);
            if (inputs.Count == 1)
            {
                var (seconds, via) = Labour(inputs[0].ItemId, harvest, refines, tables);
                if (seconds is { } s)
                    return (s * inputs[0].Count / recipe.OutputCount, via);
            }
        }
        return (null, "no node");
    }

    /// <summary>Seconds per unit of each item at its cheapest node type, from seeded rolls of each node's yield table with the shared math.</summary>
    private static Dictionary<string, (double Seconds, string Node)> HarvestLabour(SimEconomy economy, int jobLevel, ulong seed)
    {
        var settings = economy.Harvest!;
        var netToId = SimEconomy.Rows<ItemDef>(economy.Tables).ToDictionary(i => i.NetId, i => i.Id);
        int bonus = HarvestMath.BonusPercent(jobLevel, 0, 0, settings);
        var best = new Dictionary<string, (double Seconds, string Node)>(StringComparer.Ordinal);
        foreach (var type in economy.NodePools.GroupBy(p => p.NodeId, StringComparer.Ordinal))
        {
            var pool = type.First();
            int nodes = type.Sum(p => p.Points);
            var rng = new Pcg32Rng(seed, Stream + (ulong)pool.NodeNetId);
            var units = new Dictionary<int, long>();
            for (int i = 0; i < HarvestTrials; i++)
            {
                var yielded = HarvestMath.Roll(pool.Yield, jobLevel, bonus, 100, rng);
                if (!yielded.IsEmpty)
                    units[yielded.ItemNetId] = units.GetValueOrDefault(yielded.ItemNetId) + yielded.Count;
            }
            double secondsPerHarvest = Math.Max(pool.HarvestMs, (double)pool.RespawnMs / nodes) / 1000;
            foreach (var (netId, count) in units)
            {
                double seconds = secondsPerHarvest * HarvestTrials / count;
                string item = netToId[netId];
                if (!best.TryGetValue(item, out var seen) || seconds < seen.Seconds)
                    best[item] = (seconds, pool.NodeId);
            }
        }
        return best;
    }
}

/// <summary>Checks over the tables alone, once per run (spec 12.5; WP-M3-12).</summary>
public static class M3TableChecks
{
    /// <summary>
    /// M3 adds no gold faucet (plan section 1, R-4.1-01): gathered and crafted items are not sell-only, so no node yield, recipe
    /// output or mold output has a <c>PriceBase</c> row an outpost would pay.
    /// </summary>
    public static SimVerdict NoGoldFaucet(ConfigSet tables)
    {
        var priced = SimEconomy.Rows<PriceBase>(tables).Select(p => p.ItemId).ToHashSet(StringComparer.Ordinal);
        var made = SimEconomy.Rows<NodeYieldEntry>(tables).Select(y => y.ItemId)
            .Concat(SimEconomy.Rows<RecipeDef>(tables).Select(r => r.OutputItemId))
            .Concat(SimEconomy.Rows<MoldDef>(tables).Select(m => m.OutputItemId))
            .Where(priced.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new SimVerdict("no M3 gold faucet", made.Count == 0, false, made.Count == 0
            ? "no gathered or crafted item has a PriceBase row (R-4.1-01)"
            : "gathered or crafted and sold at an outpost: " + string.Join(", ", made));
    }
}
