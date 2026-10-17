using Shared.Simulation;
using Shared.World;

namespace GameServer.World.Map;

/// <summary>A <c>ServerMarker</c> from markers.json: ground position in Q24.8 cm and a 16-bit facing (0 = +Y, clockwise).</summary>
public sealed record WorldMarker(string Id, MarkerKind Kind, FixVec2 Position, ushort Facing);

/// <summary>A <c>ServerBlocker</c> box from blockers.json, in whole cm. The grid already holds it (grown by the agent radius).</summary>
public sealed record WorldBlocker(int MinXCm, int MinYCm, int MaxXCm, int MaxYCm, int MinHeightCm, int MaxHeightCm);

/// <summary>One region of the world: its index in the grid, its <c>RegionDef</c> id and its resolved flags.</summary>
public sealed record WorldRegion(byte Index, string Id, string AreaType, RegionFlags Flags);

/// <summary>
/// One loaded, verified world (spec 2.3, 11.6): the shared <see cref="WorldGrid"/> for collision and height, the regions
/// with their flags, the markers and the blockers. Read-only, so it can be shared by the world thread and tools. Build
/// it with <see cref="WorldLoader"/>.
/// </summary>
public sealed class WorldMap
{
    private readonly WorldRegion?[] _regionsByIndex;
    private readonly Dictionary<string, WorldMarker> _markersById;

    public WorldMap(WorldManifest manifest, WorldGrid grid, IReadOnlyList<WorldRegion> regions,
        IReadOnlyList<WorldMarker> markers, IReadOnlyList<WorldBlocker> blockers)
    {
        Manifest = manifest;
        Grid = grid;
        Regions = regions;
        Markers = markers;
        Blockers = blockers;
        _regionsByIndex = new WorldRegion?[WorldGridFormat.MaxRegions + 1];
        foreach (var region in regions)
            _regionsByIndex[region.Index] = region;
        _markersById = markers.ToDictionary(m => m.Id, StringComparer.Ordinal);
    }

    public WorldManifest Manifest { get; }

    public WorldGrid Grid { get; }

    /// <summary>Regions in index order (index 1 first).</summary>
    public IReadOnlyList<WorldRegion> Regions { get; }

    /// <summary>Markers sorted by id.</summary>
    public IReadOnlyList<WorldMarker> Markers { get; }

    public IReadOnlyList<WorldBlocker> Blockers { get; }

    /// <summary>The region at a position (two array reads), or null outside every region and outside the grid.</summary>
    public WorldRegion? RegionAt(FixVec2 position) => _regionsByIndex[Grid.RegionIndexAt(position)];

    /// <summary>The region with a grid index; null for 0 (no region).</summary>
    public WorldRegion? RegionByIndex(byte index) => _regionsByIndex[index];

    /// <summary>
    /// The flags at a position (R-2.3-05). False where there is no region: the exporter refuses walkable cells without
    /// one, so that only happens off the walkable ground, and callers decide what that means for them.
    /// </summary>
    public bool TryGetFlags(FixVec2 position, out RegionFlags flags)
    {
        var region = RegionAt(position);
        flags = region?.Flags ?? default;
        return region != null;
    }

    public bool TryGetMarker(string id, out WorldMarker marker) => _markersById.TryGetValue(id, out marker!);

    /// <summary>Ground height in cm at a position, or <paramref name="fallbackCm"/> outside the grid.</summary>
    public int HeightCmAt(FixVec2 position, int fallbackCm) =>
        Grid.TryGetHeightCm(position, out var height) ? height : fallbackCm;
}
