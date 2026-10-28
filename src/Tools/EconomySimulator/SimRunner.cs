using System.Globalization;
using Shared.Data.Tables;

namespace EconomySimulator;

/// <summary>Plays a scenario's runs (seeds <c>seed</c> to <c>seed + runs - 1</c>) and sums them up.</summary>
public static class SimRunner
{
    public static ScenarioResult Play(SimEconomy economy, EconomySimScenario scenario, int? seedOverride = null)
    {
        var simulation = new Simulation(economy, scenario);
        ulong first = (ulong)(seedOverride ?? scenario.Seed);
        var seeds = Enumerable.Range(0, scenario.Runs).Select(i => first + (ulong)i).ToList();
        var runs = seeds.Select(simulation.Run).ToList();
        return ScenarioResult.From(economy, scenario, seeds, runs);
    }
}

/// <summary>The text report: one line per simulated hour, the multipliers per sample, then the verdicts.</summary>
public static class SimPrinter
{
    public static void Print(ScenarioResult result, TextWriter output, bool verdictsOnly)
    {
        var c = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(c,
            $"== {result.Id}: {result.Players} players, {result.Hours} h, {result.Seeds.Count} run(s) (seeds {result.Seeds[0]}..{result.Seeds[^1]}), "
            + $"{result.KillsPerPlayerMinuteMilli / 1000.0:0.##} kills per player-minute, an outpost visit every {result.KillsPerVisit} kills"));
        if (!verdictsOnly)
        {
            output.WriteLine("  hour  gold created (min-max)    destroyed (min-max)   net      gold held  kills");
            foreach (var h in result.HourTotals)
                output.WriteLine(string.Create(c,
                    $"  {h.Hour,4}  {h.GoldCreated,9:0} ({h.GoldCreatedMin}-{h.GoldCreatedMax})  {h.GoldDestroyed,8:0} ({h.GoldDestroyedMin}-{h.GoldDestroyedMax})  "
                    + $"{h.GoldCreated - h.GoldDestroyed,8:0}  {h.GoldOnline,9:0}  {h.Kills,6:0}"));
            output.WriteLine(string.Create(c, $"  multipliers, one value per {result.SampleMinutes} min (mean over the runs):"));
            foreach (var (name, values) in result.Samples.Where(s => s.Key.Contains('%')))
                output.WriteLine($"    {name,-34} {string.Join(" ", values.Select(v => v.ToString("0", c)))}");
        }
        foreach (var verdict in result.Verdicts)
            output.WriteLine($"  [{(verdict.Pass ? "PASS" : verdict.WarningOnly ? "WARN" : "FAIL")}] {verdict.Name}: {verdict.Detail}");
    }
}
