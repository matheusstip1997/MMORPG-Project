// The economy simulator (spec 12.5, WP-M2-12): a seeded Monte Carlo over the M2 economy tables (sell prices with the
// demand loop and the price floor recovery, spawn delays, vendor sinks with the decided stock), played per
// EconomySimScenario row. Prints the faucet and sink totals per hour, every multiplier over time, and whether the loop
// converges; catches structural flaws only, never tunes a value.
// Usage: dotnet run --project src/Tools/EconomySimulator -- [--data <dir>] [--world <dir>] [--scenario <id>] [--seed <n>]
//        [--check] [--report <file.json>]
// --scenario plays one row (default: every row); --seed replaces the rows' seeds; --check prints the verdicts only and
// fails on a structural flaw (CI); --report writes everything as JSON.
// Exit codes: 0 every check passed, 1 a check failed, 2 bad arguments, tables or world.

using System.Globalization;
using System.Text.Json;
using EconomySimulator;

var dataDirectory = Path.Combine("data", "tables");
var worldDirectory = Path.Combine("data", "world", "m1");
string? scenarioId = null;
int? seed = null;
bool check = false;
string? reportPath = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data" when i + 1 < args.Length:
            dataDirectory = args[++i];
            break;
        case "--world" when i + 1 < args.Length:
            worldDirectory = args[++i];
            break;
        case "--scenario" when i + 1 < args.Length:
            scenarioId = args[++i];
            break;
        case "--seed" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var s):
            seed = s;
            i++;
            break;
        case "--check":
            check = true;
            break;
        case "--report" when i + 1 < args.Length:
            reportPath = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            Console.Error.WriteLine("Usage: EconomySimulator [--data <dir>] [--world <dir>] [--scenario <id>] [--seed <n>] [--check] [--report <file.json>]");
            return 2;
    }
}

SimEconomy economy;
try
{
    economy = SimEconomy.Load(dataDirectory, worldDirectory);
}
catch (Exception e) when (e is InvalidDataException or IOException)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

var scenarios = economy.Scenarios.Where(s => scenarioId == null || s.Id == scenarioId).ToList();
if (scenarios.Count == 0)
{
    Console.Error.WriteLine(scenarioId == null ? "EconomySimScenario has no rows" : $"No EconomySimScenario row '{scenarioId}'");
    return 2;
}

var results = new List<ScenarioResult>();
foreach (var scenario in scenarios)
{
    var result = SimRunner.Play(economy, scenario, seed);
    results.Add(result);
    SimPrinter.Print(result, Console.Out, verdictsOnly: check);
}

bool pass = results.All(r => r.Pass);
Console.WriteLine(pass
    ? $"OK: {results.Count} scenario(s), no structural flaw (demand loop settles, counters bounded, faucet settles)"
    : $"FAILED: {string.Join(", ", results.Where(r => !r.Pass).Select(r => r.Id))}");

if (reportPath != null)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
    if (directory != null)
        Directory.CreateDirectory(directory);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(new { pass, scenarios = results },
        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    Console.WriteLine($"Report: {Path.GetFullPath(reportPath)}");
}
return pass ? 0 : 1;
