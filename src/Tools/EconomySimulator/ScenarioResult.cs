using Shared.Data.Tables;
using Shared.Simulation.Economy;

namespace EconomySimulator;

/// <summary>One simulated hour over the runs: gold created by sales (the faucet), destroyed by vendors (the sink), held at its end.</summary>
public sealed record SimHour(int Hour, double GoldCreated, long GoldCreatedMin, long GoldCreatedMax, double GoldDestroyed,
    long GoldDestroyedMin, long GoldDestroyedMax, double GoldOnline, double Kills);

/// <summary>One check's outcome. A warning is reported and never fails the check run.</summary>
public sealed record SimVerdict(string Name, bool Pass, bool WarningOnly, string Detail);

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
            double online = 0, kills = 0;
            foreach (var run in runs)
            {
                long c = 0, d = 0, k = 0;
                int last = -1;
                for (int i = 0; i < steps; i++)
                {
                    if (((i + 1L) * controlMs - 1) / HourMs != hour)
                        continue;
                    c += run.GoldCreated[i];
                    d += run.GoldDestroyed[i];
                    k += run.Kills[i];
                    last = i;
                }
                created.Add(c);
                destroyed.Add(d);
                kills += k;
                online += last >= 0 ? run.GoldOnline[last] : 0;
            }
            hours.Add(new SimHour(hour + 1, created.Average(), created.Min(), created.Max(), destroyed.Average(), destroyed.Min(),
                destroyed.Max(), online / runs.Count, kills / runs.Count));
        }

        return new ScenarioResult
        {
            Id = scenario.Id,
            Players = scenario.Players,
            Hours = scenario.Hours,
            Seeds = seeds,
            KillsPerPlayerMinuteMilli = scenario.KillsPerPlayerMinuteMilli,
            KillsPerVisit = scenario.KillsPerVisit,
            SampleMinutes = scenario.SampleMinutes,
            HourTotals = hours,
            Samples = samples,
            Verdicts = Check(scenario, samples, hours),
        };
    }

    private static List<SimVerdict> Check(EconomySimScenario scenario, SortedDictionary<string, double[]> samples, List<SimHour> hours)
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

        // 4. Sink cover (reported only in M2: fees are M4 and consumable use is M3, so the vendors alone cannot balance sales).
        double faucet = hours.Sum(h => h.GoldCreated), sink = hours.Sum(h => h.GoldDestroyed);
        double cover = faucet <= 0 ? 0 : sink * 100 / faucet;
        double netPerPlayerHour = (faucet - sink) / Math.Max(1, scenario.Players) / Math.Max(1, scenario.Hours);
        verdicts.Add(new SimVerdict("sink cover", cover >= 100, true,
            FormattableString.Invariant($"vendor sinks destroy {cover:0.#}% of the faucet; net {netPerPlayerHour:0} gold per player-hour (M2: fees are M4, consumable use M3)")));
        return verdicts;
    }
}
