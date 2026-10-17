using GameServer.World.Map;
using Shared.Simulation;
using Shared.World;

namespace GameServer.Tests.Support;

/// <summary>Worlds built in memory, the way <see cref="WorldLoader"/> would build them from exported files.</summary>
public static class TestMaps
{
    public const string SpawnId = "test.spawn";
    public const string OpenRegionId = "test.open";
    public const int OpenCellCm = 100;
    public const int OpenHalfSizeCm = 10_000;

    public static readonly RegionFlags OpenFlags = new(PvpMode.ArmedOnly, honorEffects: true, DeathDrop.On);

    /// <summary>A flat, all-walkable square of ±100 m in one region, with the player spawn at the given point.</summary>
    public static WorldMap Open(int spawnXCm = 0, int spawnYCm = 0, int heightCm = 0, ushort facing = 0)
    {
        int cells = 2 * OpenHalfSizeCm / OpenCellCm;
        var grid = Grid(-OpenHalfSizeCm, -OpenHalfSizeCm, OpenCellCm, cells, cells, heightCm, (_, _) => true);
        return Map(grid, [SpawnMarker(spawnXCm, spawnYCm, facing)]);
    }

    /// <summary>A grid whose walkable cells are given by <paramref name="walkable"/>(column, row); every cell is in region 1.</summary>
    public static WorldGrid Grid(int originXCm, int originYCm, int cellCm, int width, int height, int baseHeightCm,
        Func<int, int, bool> walkable, Func<int, int, short>? heightOffset = null, Func<int, int, byte>? region = null)
    {
        var flags = new byte[width * height];
        var regions = new byte[width * height];
        var heights = new short[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                flags[i] = walkable(x, y) ? WorldGridFormat.FlagWalkable : (byte)0;
                regions[i] = region?.Invoke(x, y) ?? 1;
                heights[i] = heightOffset?.Invoke(x, y) ?? 0;
            }
        }
        return new WorldGrid(originXCm, originYCm, cellCm, width, height, baseHeightCm, flags, regions, heights);
    }

    public static WorldMarker SpawnMarker(int xCm, int yCm, ushort facing = 0, string id = SpawnId) =>
        new(id, MarkerKind.PlayerSpawn, FixVec2.FromCm(xCm, yCm), facing);

    public static WorldMap Map(WorldGrid grid, IReadOnlyList<WorldMarker> markers, IReadOnlyList<WorldRegion>? regions = null)
    {
        var manifest = new WorldManifest
        {
            Format = WorldFiles.ManifestFormat,
            FormatVersion = WorldFiles.ManifestVersion,
            WorldId = "test",
            CellSizeCm = grid.CellSizeCm,
            WidthCells = grid.Width,
            HeightCells = grid.Height,
        };
        regions ??= [new WorldRegion(1, OpenRegionId, "openWorld", OpenFlags)];
        return new WorldMap(manifest, grid, regions, markers, []);
    }
}
