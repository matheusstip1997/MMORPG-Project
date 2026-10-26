using System.Text.Json;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.World;

namespace BotClient;

/// <summary>
/// What a bot knows before it connects, read the way the Unity client reads it: the config tables with the portable
/// reader (<see cref="ConfigTableReader"/>) and the exported world verified by the shared <see cref="WorldPackage"/>.
/// </summary>
public sealed class BotData
{
    private BotData(string repository, ConfigSet tables, WorldGrid grid, IReadOnlyDictionary<string, FixVec2> markers)
    {
        Repository = repository;
        Tables = tables;
        Grid = grid;
        Markers = markers;
    }

    public string Repository { get; }

    public ConfigSet Tables { get; }

    public WorldGrid Grid { get; }

    /// <summary>Marker positions of the world (markers.json) by id.</summary>
    public IReadOnlyDictionary<string, FixVec2> Markers { get; }

    public static BotData Load(string repository, string? tablesDirectory = null, string? worldDirectory = null)
    {
        tablesDirectory ??= Path.Combine(repository, "data", "tables");
        worldDirectory ??= Path.Combine(repository, "data", "world", "m1");
        var errors = new List<ValidationError>();
        var tables = ConfigTableReader.LoadAndValidate(ConfigTables.All, name =>
        {
            var path = Path.Combine(tablesDirectory, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, errors);
        if (errors.Count > 0)
            throw new InvalidDataException($"Config tables in {tablesDirectory} are invalid (run DataValidator): {errors[0]}");

        if (!WorldPackage.TryLoadGrid(name =>
            {
                var path = Path.Combine(worldDirectory, name);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }, out var grid, out var error))
            throw new InvalidDataException($"World {worldDirectory}: {error}");

        var markers = new Dictionary<string, FixVec2>(StringComparer.Ordinal);
        using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(worldDirectory, WorldFiles.Markers))))
        {
            foreach (var marker in document.RootElement.GetProperty("markers").EnumerateArray())
            {
                markers[marker.GetProperty("id").GetString()!] =
                    FixVec2.FromCm(marker.GetProperty("xCm").GetInt32(), marker.GetProperty("yCm").GetInt32());
            }
        }
        return new BotData(repository, tables, grid!, markers);
    }

    public IEnumerable<T> Rows<T>() =>
        Tables.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>() : Enumerable.Empty<T>();

    public T Row<T>(string id, Func<T, string> key) =>
        Rows<T>().FirstOrDefault(r => key(r) == id) ?? throw new ArgumentException($"No {typeof(T).Name} row '{id}'.");

    public BotScenario Scenario(string id) => Row<BotScenario>(id, s => s.Id);

    public BotBehaviour Behaviour(string id) => Row<BotBehaviour>(id, b => b.Id);

    public BotLinkProfile Link(string id) => Row<BotLinkProfile>(id, l => l.Id);

    /// <summary>The repository root: <paramref name="explicitPath"/>, or the first folder up from here with Mmorpg.sln.</summary>
    public static string FindRepository(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
            return Path.GetFullPath(explicitPath);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
                    return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Mmorpg.sln not found above the working directory; pass --repo <dir>.");
    }
}
