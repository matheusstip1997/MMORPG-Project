using System.Text;
using System.Text.Json;
using GameServer.World.Map;
using Shared.World;

namespace GameServer.Tests.Support;

/// <summary>
/// Writes a world directory in the UnityExporter format (spec 11.6) so loader tests can corrupt one piece at a time.
/// </summary>
public sealed class TestWorldFiles : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public TestWorldFiles(WorldGrid? grid = null)
    {
        Grid = grid ?? TestMaps.Grid(-500, -500, 100, 10, 10, 0, (x, y) => !(x == 5 && y == 5),
            region: (x, _) => (byte)(x < 5 ? 1 : 2));
        Write();
    }

    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "mmorpg-world-" + Guid.NewGuid().ToString("N"));

    public WorldGrid Grid { get; set; }

    public List<(int Index, string Id)> Regions { get; } = [(1, "m1.city"), (2, "m1.combat")];

    public List<object> Markers { get; } = [new { id = "m1.spawn.city", kind = "playerSpawn", xCm = -250, yCm = 0, facing = 16384 }];

    public List<object> Blockers { get; } = [new { minXCm = 0, minYCm = 0, maxXCm = 100, maxYCm = 100, minHeightCm = 0, maxHeightCm = 300 }];

    public int FormatVersion { get; set; } = WorldFiles.ManifestVersion;

    /// <summary>Writes every file and a manifest that matches them.</summary>
    public void Write()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(Path.Combine(Directory, WorldFiles.Grid), WorldGridWriter.Write(Grid));
        WriteJson(WorldFiles.Regions, new { regions = Regions.Select(r => new { index = r.Index, id = r.Id }) });
        WriteJson(WorldFiles.Markers, new { markers = Markers });
        WriteJson(WorldFiles.Blockers, new { blockers = Blockers });
        WriteManifest();
    }

    public void WriteManifest()
    {
        var manifest = new
        {
            format = WorldFiles.ManifestFormat,
            formatVersion = FormatVersion,
            toolVersion = "test",
            worldId = "m1",
            cellSizeCm = Grid.CellSizeCm,
            agentRadiusCm = 40,
            bounds = new
            {
                minXCm = Grid.OriginXCm,
                minYCm = Grid.OriginYCm,
                maxXCm = Grid.OriginXCm + Grid.Width * Grid.CellSizeCm,
                maxYCm = Grid.OriginYCm + Grid.Height * Grid.CellSizeCm,
            },
            widthCells = Grid.Width,
            heightCells = Grid.Height,
            sources = new[] { new { path = "Assets/Scenes/Test.unity", sha256 = new string('0', 64) } },
            files = WorldFiles.DataFiles.Select(name =>
            {
                var bytes = File.ReadAllBytes(Path.Combine(Directory, name));
                return new { name, bytes = bytes.LongLength, sha256 = WorldLoader.Sha256Hex(bytes) };
            }),
        };
        WriteJson(WorldFiles.Manifest, manifest);
    }

    public string PathOf(string name) => Path.Combine(Directory, name);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteJson(string name, object value) =>
        File.WriteAllText(Path.Combine(Directory, name), JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
}
