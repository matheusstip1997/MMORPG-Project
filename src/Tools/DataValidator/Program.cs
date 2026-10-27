// Validates the config tables in data/tables (spec 1.8): schema, ranges, unique IDs, references, row rules.
// Then validates the exported world (spec 11.6) against them: manifest version, file hashes, grid, region ids in
// RegionDef, marker ids and kinds, and the ServerLoop.PlayerSpawnMarkerId spawn.
// Usage: dotnet run --project src/Tools/DataValidator -- [--data <dir>] [--world <dir>] [--report <file.json>] [--skill <node id>]
// --world defaults to data/world/m1 and is skipped with a note when that default folder does not exist.
// --skill prints one skill-tree node with its SkillDef inheritance resolved (spec 3.5, R-3.5-05; WP-M2-5) after the checks.
// Exit codes: 0 valid, 1 validation errors, 2 bad arguments or missing directory.

using System.Text.Json;
using GameServer.Host.Config;
using GameServer.World.Map;
using Shared.Data;

var dataDirectory = Path.Combine("data", "tables");
var worldDirectory = Path.Combine("data", "world", "m1");
var worldGiven = false;
string? reportPath = null;
string? skillId = null;

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
        case "--skill" when i + 1 < args.Length:
            skillId = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            Console.Error.WriteLine("Usage: DataValidator [--data <dir>] [--world <dir>] [--report <file.json>] [--skill <node id>]");
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

// Every table check (ConfigChecks): the same ones the game server runs at start and on a dev hot reload (spec 11.7).
var result = ConfigChecks.LoadAndCheck(dataDirectory);

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
    // The markers the tables name: ServerLoop.PlayerSpawnMarkerId and every SpawnPoints row (spec 11.6).
    if (world.Map != null)
        worldErrors.AddRange(WorldLoader.CheckConfigMarkers(world.Map, result.Set));
    foreach (var error in worldErrors)
        Console.WriteLine($"ERROR world {error}");
    Console.WriteLine(worldErrors.Count == 0
        ? $"OK: world {world.Map!.Manifest.WorldId} valid in {Path.GetFullPath(worldDirectory)} " +
          $"({world.Map.Grid.Width} x {world.Map.Grid.Height} cells, {world.Map.Regions.Count} region(s), {world.Map.Markers.Count} marker(s))"
        : $"FAILED: world has {worldErrors.Count} error(s)");
}

var ok = result.Ok && worldErrors.Count == 0;

if (skillId != null)
{
    var printed = DataValidator.SkillPrinter.Print(result.Set, skillId);
    Console.WriteLine(printed ?? $"ERROR no SkillTree node '{skillId}'");
    ok &= printed != null;
}

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
