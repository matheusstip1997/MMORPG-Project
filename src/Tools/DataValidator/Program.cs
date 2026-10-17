// Validates the config tables in data/tables (spec 1.8): schema, ranges, unique IDs, references, row rules.
// Usage: dotnet run --project src/Tools/DataValidator -- [--data <dir>] [--report <file.json>]
// Exit codes: 0 valid, 1 validation errors, 2 bad arguments or missing directory.

using System.Text.Json;
using GameServer.Host.Config;
using Shared.Data;

var dataDirectory = Path.Combine("data", "tables");
string? reportPath = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data" when i + 1 < args.Length:
            dataDirectory = args[++i];
            break;
        case "--report" when i + 1 < args.Length:
            reportPath = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            Console.Error.WriteLine("Usage: DataValidator [--data <dir>] [--report <file.json>]");
            return 2;
    }
}

if (!Directory.Exists(dataDirectory))
{
    Console.Error.WriteLine($"Data directory not found: {Path.GetFullPath(dataDirectory)}");
    return 2;
}

var result = ConfigLoader.LoadAndValidate(dataDirectory, ConfigTables.All);

foreach (var error in result.Errors)
    Console.WriteLine($"ERROR {error}");

var tableCount = result.Set.Tables.Count();
Console.WriteLine(result.Ok
    ? $"OK: {tableCount} table(s) valid in {Path.GetFullPath(dataDirectory)}"
    : $"FAILED: {result.Errors.Count} error(s); {tableCount} of {ConfigTables.All.Count} table(s) loaded");

if (reportPath != null)
{
    var report = new
    {
        ok = result.Ok,
        dataDirectory = Path.GetFullPath(dataDirectory),
        tables = result.Set.Tables.Select(t => new { name = t.Name, version = t.Version, rows = t.Rows.Count }),
        errors = result.Errors.Select(e => new { code = e.Code, table = e.Table, row = e.Row, field = e.Field, message = e.Message }),
    };
    var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
    if (directory != null)
        Directory.CreateDirectory(directory);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}

return result.Ok ? 0 : 1;
