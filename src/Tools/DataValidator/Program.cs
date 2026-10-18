// Validates the config tables in data/tables (spec 1.8): schema, ranges, unique IDs, references, row rules.
// Then validates the exported world (spec 11.6) against them: manifest version, file hashes, grid, region ids in
// RegionDef, marker ids and kinds, and the ServerLoop.PlayerSpawnMarkerId spawn.
// Usage: dotnet run --project src/Tools/DataValidator -- [--data <dir>] [--world <dir>] [--report <file.json>]
// --world defaults to data/world/m1 and is skipped with a note when that default folder does not exist.
// Exit codes: 0 valid, 1 validation errors, 2 bad arguments or missing directory.

using System.Text.Json;
using GameServer.Host.Config;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Stats;

var dataDirectory = Path.Combine("data", "tables");
var worldDirectory = Path.Combine("data", "world", "m1");
var worldGiven = false;
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
            worldGiven = true;
            break;
        case "--report" when i + 1 < args.Length:
            reportPath = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            Console.Error.WriteLine("Usage: DataValidator [--data <dir>] [--world <dir>] [--report <file.json>]");
            return 2;
    }
}

if (!Directory.Exists(dataDirectory))
{
    Console.Error.WriteLine($"Data directory not found: {Path.GetFullPath(dataDirectory)}");
    return 2;
}

if (worldGiven && !Directory.Exists(worldDirectory))
{
    Console.Error.WriteLine($"World directory not found: {Path.GetFullPath(worldDirectory)}");
    return 2;
}

var result = ConfigLoader.LoadAndValidate(dataDirectory, ConfigTables.All);
// Cross-table rule: every reason code enum has a ReasonCodeStrings row (spec 9.1).
result = result with { Errors = result.Errors.Concat(ReasonCodeCoverage.Check(result.Set)).ToList() };
if (result.Ok)
{
    // Cross-table rules of the stat and damage tables (backlog D5); they assume rows that passed the row validator.
    var crossTableErrors = new List<ValidationError>();
    CombatConfig.TryBuild(result.Set, crossTableErrors);
    result = result with { Errors = crossTableErrors };
}

foreach (var error in result.Errors)
    Console.WriteLine($"ERROR {error}");

var tableCount = result.Set.Tables.Count();
Console.WriteLine(result.Ok
    ? $"OK: {tableCount} table(s) valid in {Path.GetFullPath(dataDirectory)}"
    : $"FAILED: {result.Errors.Count} error(s); {tableCount} of {ConfigTables.All.Count} table(s) loaded");

var worldErrors = new List<WorldLoadError>();
var worldChecked = Directory.Exists(worldDirectory);
if (!worldChecked)
{
    Console.WriteLine($"SKIPPED world: {Path.GetFullPath(worldDirectory)} does not exist");
}
else
{
    var world = WorldLoader.Load(worldDirectory, result.Set);
    worldErrors.AddRange(world.Errors);
    if (world.Map != null && result.Set.TryGet(typeof(ServerLoop), out var loopTable) && loopTable.Rows.Count == 1)
    {
        var spawnError = WorldLoader.CheckPlayerSpawn(world.Map, ((ServerLoop)loopTable.Rows[0]).PlayerSpawnMarkerId);
        if (spawnError != null)
            worldErrors.Add(spawnError);
    }
    foreach (var error in worldErrors)
        Console.WriteLine($"ERROR world {error}");
    Console.WriteLine(worldErrors.Count == 0
        ? $"OK: world {world.Map!.Manifest.WorldId} valid in {Path.GetFullPath(worldDirectory)} " +
          $"({world.Map.Grid.Width} x {world.Map.Grid.Height} cells, {world.Map.Regions.Count} region(s), {world.Map.Markers.Count} marker(s))"
        : $"FAILED: world has {worldErrors.Count} error(s)");
}

var ok = result.Ok && worldErrors.Count == 0;

if (reportPath != null)
{
    var report = new
    {
        ok,
        dataDirectory = Path.GetFullPath(dataDirectory),
        tables = result.Set.Tables.Select(t => new { name = t.Name, version = t.Version, rows = t.Rows.Count }),
        errors = result.Errors.Select(e => new { code = e.Code, table = e.Table, row = e.Row, field = e.Field, message = e.Message }),
        world = new
        {
            directory = Path.GetFullPath(worldDirectory),
            @checked = worldChecked,
            errors = worldErrors.Select(e => new { code = e.Code, file = e.File, message = e.Message }),
        },
    };
    var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
    if (directory != null)
        Directory.CreateDirectory(directory);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}

return ok ? 0 : 1;
