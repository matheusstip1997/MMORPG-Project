using Shared.Data.Tables;
using Shared.Simulation.Economy;

namespace EconomySimulator;

/// <summary>
/// One simulated hour over the runs: gold created by sales (the faucet), destroyed by vendors (the sink), held at its end;
/// and the vendor goods used up in it (consumable use, WP-M3-3), in units and in what they had cost (means over the runs).
/// </summary>
public sealed record SimHour(int Hour, double GoldCreated, long GoldCreatedMin, long GoldCreatedMax, double GoldDestroyed,
    long GoldDestroyedMin, long GoldDestroyedMax, double GoldOnline, double Kills, double GoodsUsed = 0, double GoodsUsedGold = 0);

/// <summary>One check's outcome. A warning is reported and never fails the check run.</summary>
public sealed record SimVerdict(string Name, bool Pass, bool WarningOnly, string Detail);

/// <summary>A named M3 sink or flow (<see cref="M3Names"/>): the mean over the runs per simulated hour, in all and per player-hour.</summary>
public sealed record SimM3Line(string Name, string Unit, IReadOnlyList<double> PerHour, double Total, double PerPlayerHour);

/// <summary>
/// A scenario's runs summed up (spec 12.5): totals per hour (mean, min and max over the runs), every series averaged per
/// sample (the steps of <c>sampleMinutes</c>, over the runs), and the verdicts of the structural checks.
/// </summary>
public sealed class ScenarioResult
{
    private const long HourMs = 3_600_000;

    public required string Id { get; init; }
    public required int Players { get; init; }
    public required int Hours { get; init; }
    public required IReadOnlyList<ulong> Seeds { get; init; }
    public required int KillsPerPlayerMinuteMilli { get; init; }
    public required int KillsPerVisit { get; init; }
    public required int SampleMinutes { get; init; }
    public required IReadOnlyList<SimHour> HourTotals { get; init; }
    public required SortedDictionary<string, double[]> Samples { get; init; }
    public required IReadOnlyList<SimVerdict> Verdicts { get; init; }

    /// <summary>The named M3 sinks (spec 12.5, R-4.2-04; WP-M3-12); empty for a row without M3 activity.</summary>
    public IReadOnlyList<SimM3Line> Sinks { get; init; } = [];

    /// <summary>The M3 flows (harvests, units, materials from loot, conversions won, enchant tries, sockets won, gems removed).</summary>
    public IReadOnlyList<SimM3Line> Flows { get; init; } = [];

    /// <summary>The gold each fee type destroyed (WP-M4-1, <see cref="FeeNames"/>); empty for a row without fee volume.</summary>
    public IReadOnlyList<SimM3Line> Fees { get; init; } = [];

    /// <summary>Material units the players hold at the end, the mean over the runs.</summary>
    public double MaterialsHeld { get; init; }

    /// <summary>Every check passed (warnings do not count).</summary>
    public bool Pass => Verdicts.All(v => v.Pass || v.WarningOnly);

    public static ScenarioResult From(SimEconomy economy, EconomySimScenario scenario, IReadOnlyList<ulong> seeds, IReadOnlyList<RunRecord> runs)
    {
        int controlMs = economy.Demand.ControlIntervalMs;
        int steps = runs.Min(r => r.GoldCreated.Count);
        int stepsPerSample = Math.Max(1, (int)((long)scenario.SampleMinutes * 60_000 / controlMs));
        int sampleCount = Math.Max(1, steps / stepsPerSample);

        var samples = new SortedDictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var name in runs[0].Series.Keys)
        {
            var values = new double[sampleCount];
            for (int k = 0; k < sampleCount; k++)
            {
                double sum = 0;
                int n = 0;
                foreach (var run in runs)
                {
                    for (int i = k * stepsPerSample; i < Math.Min(steps, (k + 1) * stepsPerSample); i++, n++)
                        sum += run.Series[name][i];
                }
                values[k] = n == 0 ? 0 : sum / n;
            }
            samples.Add(name, values);
        }

        var hours = new List<SimHour>();
        for (int hour = 0; hour < scenario.Hours; hour++)
        {
            var created = new List<long>();
            var destroyed = new List<long>();
            double online = 0, kills = 0, goodsUsed = 0, goodsUsedGold = 0;
            foreach (var run in runs)
            {
                long c = 0, d = 0, k = 0, u = 0, g = 0;
                int last = -1;
                for (int i = 0; i < steps; i++)
                {
                    if (((i + 1L) * controlMs - 1) / HourMs != hour)
                        continue;
                    c += run.GoldCreated[i];
                    d += run.GoldDestroyed[i];
                    k += run.Kills[i];
                    u += i < run.GoodsUsed.Count ? run.GoodsUsed[i] : 0;
                    g += i < run.GoodsUsedGold.Count ? run.GoodsUsedGold[i] : 0;
                    last = i;
                }
                created.Add(c);
                destroyed.Add(d);
                kills += k;
                goodsUsed += u;
                goodsUsedGold += g;
                online += last >= 0 ? run.GoldOnline[last] : 0;
            }
            hours.Add(new SimHour(hour + 1, created.Average(), created.Min(), created.Max(), destroyed.Average(), destroyed.Min(),
                destroyed.Max(), online / runs.Count, kills / runs.Count, goodsUsed / runs.Count, goodsUsedGold / runs.Count));
        }

        double playerHours = (double)Math.Max(1, scenario.Players) * Math.Max(1, scenario.Hours);
        var sinks = M3Lines(M3Names.Sinks, runs, steps, controlMs, scenario.Hours, playerHours);
        var flows = M3Lines(M3Names.Flows, runs, steps, controlMs, scenario.Hours, playerHours);
        var fees = Lines(FeeNames.All, runs, r => r.Fees, _ => "gold", steps, controlMs, scenario.Hours, playerHours);
        double held = runs.Average(r => r.MaterialsHeld.Count > 0 ? r.MaterialsHeld[^1] : 0);
        var verdicts = Check(scenario, samples, hours, fees);
        if (SimCrafting.Plays(scenario))
            verdicts.AddRange(CheckM3(scenario, samples, sinks, flows));
        if (scenario.PlaysFees())
            verdicts.Add(CheckFees(scenario, fees));

        return new ScenarioResult
        {
            Sinks = sinks,
            Flows = flows,
            Fees = fees,
            MaterialsHeld = held,
            Id = scenario.Id,
            Players = scenario.Players,
            Hours = scenario.Hours,
            Seeds = seeds,
            KillsPerPlayerMinuteMilli = scenario.KillsPerPlayerMinuteMilli,
            KillsPerVisit = scenario.KillsPerVisit,
            SampleMinutes = scenario.SampleMinutes,
            HourTotals = hours,
            Samples = samples,
            Verdicts = verdicts,
        };
    }

    private static List<SimM3Line> M3Lines(IReadOnlyList<string> names, IReadOnlyList<RunRecord> runs, int steps, int controlMs, int hourCount,
        double playerHours) =>
        Lines(names, runs, r => r.M3, M3Names.UnitOf, steps, controlMs, hourCount, playerHours);

    /// <summary>The per-hour means of the named per-step series <paramref name="series"/> keeps (the M3 sinks and flows, the fees).</summary>
    private static List<SimM3Line> Lines(IReadOnlyList<string> names, IReadOnlyList<RunRecord> runs,
        Func<RunRecord, SortedDictionary<string, List<long>>> series, Func<string, string> unitOf, int steps, int controlMs, int hourCount,
        double playerHours)
    {
        var lines = new List<SimM3Line>();
        foreach (var name in names)
        {
            if (!series(runs[0]).ContainsKey(name))
                continue;
            var perHour = new double[hourCount];
            foreach (var run in runs)
            {
                for (int i = 0; i < steps; i++)
                {
                    long hour = ((i + 1L) * controlMs - 1) / HourMs;
                    if (hour < hourCount)
                        perHour[hour] += series(run)[name][i];
                }
            }
            for (int h = 0; h < hourCount; h++)
                perHour[h] /= runs.Count;
            double total = perHour.Sum();
            lines.Add(new SimM3Line(name, unitOf(name), perHour, total, total / playerHours));
        }
        return lines;
    }

    /// <summary>
    /// The structural checks of a row with M3 activity (WP-M3-12): the node demand loop settles (R-5.2-03), every sink the row
    /// plays moved (a sink that never runs balances nothing, spec 12.5), and how much of the materials the item sinks destroy
    /// (a warning: forged gear is not destroyed, R-4.2-04, and the forge's own use is not played here).
    /// </summary>
    private static List<SimVerdict> CheckM3(EconomySimScenario scenario, SortedDictionary<string, double[]> samples, List<SimM3Line> sinks,
        List<SimM3Line> flows)
    {
        var verdicts = new List<SimVerdict>();
        int tolerance = scenario.SettleTolerancePercent;
        int sampleCount = samples.Count == 0 ? 0 : samples.First().Value.Length;
        int from = SettleCheck.WindowStart(sampleCount, scenario.SettleWindowPercent);
        double windowHours = (sampleCount - from) * scenario.SampleMinutes / 60.0;

        if (scenario.HarvestsPerPlayerHour > 0)
        {
            var multipliers = samples.Where(s => s.Key.StartsWith("nodeSpawn% ", StringComparison.Ordinal))
                .Select(s => (s.Key, Spread: SettleCheck.Spread(s.Value, from))).OrderByDescending(s => s.Spread).ToList();
            var widest = multipliers.FirstOrDefault();
            var growing = samples.Where(s => s.Key.StartsWith("harvests ", StringComparison.Ordinal))
                .Where(s => SettleCheck.Grows(s.Value, from, tolerance, DemandMath.Unit)).Select(s => s.Key).ToList();
            var units = flows.First(f => f.Name == M3Names.HarvestUnits).PerHour;
            int hourFrom = SettleCheck.WindowStart(units.Count, scenario.SettleWindowPercent);
            double deviation = SettleCheck.DeviationPercent(units, hourFrom);
            bool settled = widest.Spread <= tolerance && growing.Count == 0 && deviation <= tolerance;
            verdicts.Add(new SimVerdict("node demand loop settles", settled, false, FormattableString.Invariant(
                $"every node multiplier within {tolerance} points over the last {windowHours:0.#} h (widest: {widest.Key} moved {widest.Spread:0.#}); ")
                + (growing.Count == 0 ? "no harvest counter grows; " : "still growing: " + string.Join(", ", growing) + "; ")
                + FormattableString.Invariant($"harvest units per hour within {deviation:0.#}% of their mean")));
        }

        var played = new List<string>();
        if (scenario.OneTimeBuyItemIds.Length > 0)
            played.Add(M3Names.VendorToolsAndMolds);
        if (scenario.BuyItemIds.Length > 0 && scenario.UsedPerPlayerHour > 0)
            played.Add(M3Names.ConsumableUse);
        if (scenario.ConversionsPerPlayerHour > 0)
            played.Add(M3Names.FailedConversions);
        if (scenario.EnchantTriesPerPlayerHour > 0)
            played.Add(M3Names.LostEnchantStones);
        if (scenario.SocketsPerPlayerHour > 0)
            played.Add(M3Names.FailedSockets);
        if (scenario.GemRemovalsPerPlayerHour > 0)
            played.Add(M3Names.GemRemovalCost);
        var dead = played.Where(name => sinks.First(s => s.Name == name).Total <= 0).ToList();
        verdicts.Add(new SimVerdict("M3 sinks reached", dead.Count == 0, false, dead.Count == 0
            ? string.Join(", ", sinks.Select(s => FormattableString.Invariant($"{s.Name} {s.Total:0} {s.Unit} ({s.PerPlayerHour:0.##} per player-hour)")))
            : "never moved: " + string.Join(", ", dead)));

        double created = flows.Where(f => f.Name is M3Names.HarvestUnits or M3Names.LootMaterials).Sum(f => f.Total);
        double destroyed = sinks.Where(s => s.Name is M3Names.FailedConversions or M3Names.LostEnchantStones or M3Names.FailedSockets
            or M3Names.GemRemovalCost).Sum(s => s.Total);
        double cover = created <= 0 ? 0 : destroyed * 100 / created;
        verdicts.Add(new SimVerdict("item sink cover", cover >= 100, true, FormattableString.Invariant(
            $"the item sinks destroy {cover:0.#}% of the {created:0} material units gathered and dropped (the forge's use and won conversions not counted; gear is not destroyed, R-4.2-04)")));
        return verdicts;
    }

    private static List<SimVerdict> Check(EconomySimScenario scenario, SortedDictionary<string, double[]> samples, List<SimHour> hours,
        List<SimM3Line> fees)
    {
        int tolerance = scenario.SettleTolerancePercent;
        int sampleCount = samples.Count == 0 ? 0 : samples.First().Value.Length;
        int from = SettleCheck.WindowStart(sampleCount, scenario.SettleWindowPercent);
        double windowHours = (sampleCount - from) * scenario.SampleMinutes / 60.0;
        var verdicts = new List<SimVerdict>();

        // 1. The demand loop converges: every multiplier holds still within the tolerance over the window.
        var multipliers = samples.Where(s => s.Key.StartsWith("spawn% ", StringComparison.Ordinal)
                                             || s.Key.StartsWith("reward% ", StringComparison.Ordinal)
                                             || s.Key.StartsWith("price% ", StringComparison.Ordinal))
            .Select(s => (s.Key, Spread: SettleCheck.Spread(s.Value, from))).OrderByDescending(s => s.Spread).ToList();
        var widest = multipliers.FirstOrDefault();
        verdicts.Add(new SimVerdict("demand loop settles", widest.Spread <= tolerance, false,
            FormattableString.Invariant($"every multiplier within {tolerance} points over the last {windowHours:0.#} h (widest: {widest.Key} moved {widest.Spread:0.#})")));

        // 2. Counters are bounded: no decayed counter still climbs through the window.
        var growing = samples.Where(s => s.Key.StartsWith("kills ", StringComparison.Ordinal) || s.Key.StartsWith("sales ", StringComparison.Ordinal))
            .Where(s => SettleCheck.Grows(s.Value, from, tolerance, DemandMath.Unit)).Select(s => s.Key).ToList();
        verdicts.Add(new SimVerdict("counters bounded", growing.Count == 0, false, growing.Count == 0
            ? FormattableString.Invariant($"no kill or sales counter grows over the last {windowHours:0.#} h")
            : FormattableString.Invariant($"still growing over the last {windowHours:0.#} h: {string.Join(", ", growing)}")));

        // 3. The faucet settles: gold created per hour holds within the tolerance of its mean over the window's hours.
        var created = hours.Select(h => h.GoldCreated).ToList();
        int hourFrom = SettleCheck.WindowStart(created.Count, scenario.SettleWindowPercent);
        double deviation = SettleCheck.DeviationPercent(created, hourFrom);
        verdicts.Add(new SimVerdict("faucet settles", deviation <= tolerance, false,
            FormattableString.Invariant($"gold created per hour within {tolerance}% of its mean over the last {created.Count - hourFrom} h (widest {deviation:0.#}%)")));

        // 4. Sink cover (reported only: balance is deferred, spec 12.5). The vendor sinks and, for a row with fee volume, the fees
        // (WP-M4-1, R-4.2-04: the gold FeeService destroys). From M3 the vendor goods are used up (consumable use, WP-M3-3), so
        // the line counts what was used: the players' reason to buy again.
        double faucet = hours.Sum(h => h.GoldCreated), vendor = hours.Sum(h => h.GoldDestroyed), feeTotal = fees.Sum(f => f.Total);
        double sink = vendor + feeTotal;
        double cover = faucet <= 0 ? 0 : sink * 100 / faucet;
        double feeCover = faucet <= 0 ? 0 : feeTotal * 100 / faucet;
        double playerHours = (double)Math.Max(1, scenario.Players) * Math.Max(1, scenario.Hours);
        double netPerPlayerHour = (faucet - sink) / playerHours;
        double usedPerPlayerHour = hours.Sum(h => h.GoodsUsed) / playerHours;
        double usedGoldPerPlayerHour = hours.Sum(h => h.GoodsUsedGold) / playerHours;
        verdicts.Add(new SimVerdict("sink cover", cover >= 100, true, FormattableString.Invariant(
            $"vendor sinks and fees destroy {cover:0.#}% of the faucet (fees {feeCover:0.#}%, {feeTotal / playerHours:0.##} gold per player-hour); vendor goods used {usedPerPlayerHour:0.##} per player-hour ({usedGoldPerPlayerHour:0} gold); net {netPerPlayerHour:0} gold per player-hour")));
        return verdicts;
    }

    /// <summary>
    /// A row with fee volume (WP-M4-1): every fee type the row plays destroyed gold (a sink that never runs balances nothing,
    /// spec 12.5; a fee that rounds to 0 on every traded value would be one).
    /// </summary>
    private static SimVerdict CheckFees(EconomySimScenario scenario, List<SimM3Line> fees)
    {
        var played = new List<string>();
        if (scenario.ListingsPerPlayerHour > 0)
            played.Add(FeeNames.ListingFees);
        if (scenario.TradesPerPlayerHour > 0)
            played.Add(FeeNames.SaleTaxes);
        if (scenario.ContractsPerPlayerHour > 0)
            played.Add(FeeNames.ContractFees);
        var dead = played.Where(name => fees.FirstOrDefault(f => f.Name == name)?.Total is not > 0).ToList();
        return new SimVerdict("fee sinks reached", dead.Count == 0, false, dead.Count == 0
            ? string.Join(", ", fees.Select(f => FormattableString.Invariant($"{f.Name} {f.Total:0} {f.Unit} ({f.PerPlayerHour:0.##} per player-hour)")))
            : "never moved: " + string.Join(", ", dead));
    }
}
