using System.Text.Json;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.World;

namespace GameServer.World.Map;

/// <summary>One reason a world directory was refused; <see cref="Code"/> is stable for reports and tests.</summary>
public sealed record WorldLoadError(string Code, string File, string Message)
{
    public override string ToString() => $"{Code} {File}: {Message}";
}

public sealed record WorldLoadResult(WorldMap? Map, IReadOnlyList<WorldLoadError> Errors)
{
    public bool Ok => Map != null && Errors.Count == 0;
}

/// <summary>
/// Loads and verifies a world exported by UnityExporter (spec 11.6): the manifest format and version, the size and
/// SHA-256 of every file it lists, the grid (bound-checked decode, geometry matching the manifest), region ids against
/// <c>RegionDef</c>, and marker ids, kinds and positions. Used by the server at startup, which refuses to start on any
/// error, and by <c>DataValidator --world</c>. Collects every error it can instead of stopping at the first.
/// </summary>
public static class WorldLoader
{
    public static class Codes
    {
        public const string MissingFile = "MissingFile";
        public const string BadJson = "BadJson";
        public const string FormatMismatch = "FormatMismatch";
        public const string VersionMismatch = "VersionMismatch";
        public const string FileMismatch = "FileMismatch";
        public const string GridInvalid = "GridInvalid";
        public const string GridMismatch = "GridMismatch";
        public const string BadRegion = "BadRegion";
        public const string UnknownRegion = "UnknownRegion";
        public const string BadMarker = "BadMarker";
        public const string DuplicateMarker = "DuplicateMarker";
        public const string BadBlocker = "BadBlocker";
        public const string MissingSpawn = "MissingSpawn";
        public const string UnusedSpawn = "UnusedSpawn";
        public const string NpcInLeash = "NpcInLeash";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Loads a world with the region tables of a loaded config set.</summary>
    public static WorldLoadResult Load(string directory, ConfigSet config) =>
        Load(directory, RowsOf<RegionDef>(config), RowsOf<RegionDefaults>(config));

    public static WorldLoadResult Load(string directory, IReadOnlyList<RegionDef> regionDefs,
        IReadOnlyList<RegionDefaults> regionDefaults)
    {
        var errors = new List<WorldLoadError>();
        var manifest = ReadJson<WorldManifest>(directory, WorldFiles.Manifest, errors);
        if (manifest == null)
            return new WorldLoadResult(null, errors);

        if (manifest.Format != WorldFiles.ManifestFormat)
            errors.Add(new(Codes.FormatMismatch, WorldFiles.Manifest, $"format '{manifest.Format}', expected '{WorldFiles.ManifestFormat}'"));
        if (manifest.FormatVersion != WorldFiles.ManifestVersion)
            errors.Add(new(Codes.VersionMismatch, WorldFiles.Manifest,
                $"format version {manifest.FormatVersion}, this server reads {WorldFiles.ManifestVersion}"));
        if (errors.Count > 0)
            return new WorldLoadResult(null, errors);

        CheckFiles(directory, manifest, errors);
        if (errors.Count > 0)
            return new WorldLoadResult(null, errors);

        var grid = ReadGrid(directory, manifest, errors);
        var regions = ReadRegions(directory, regionDefs, regionDefaults, grid, errors);
        var markers = ReadMarkers(directory, grid, errors);
        var blockers = ReadBlockers(directory, errors);
        if (errors.Count > 0 || grid == null)
            return new WorldLoadResult(null, errors);

        return new WorldLoadResult(new WorldMap(manifest, grid, regions, markers, blockers), errors);
    }

    /// <summary>Null when <paramref name="markerId"/> names a player spawn of the map; otherwise the error.</summary>
    public static WorldLoadError? CheckPlayerSpawn(WorldMap map, string markerId)
    {
        if (!map.TryGetMarker(markerId, out var marker))
            return new(Codes.MissingSpawn, WorldFiles.Markers, $"ServerLoop.PlayerSpawnMarkerId '{markerId}' is not a marker of this world");
        if (marker.Kind != MarkerKind.PlayerSpawn)
            return new(Codes.MissingSpawn, WorldFiles.Markers, $"marker '{markerId}' is a {WorldNames.Of(marker.Kind)}, not a playerSpawn");
        return null;
    }

    /// <summary>One error per <see cref="SpawnPoints"/> row whose marker is missing from the map or is not a monsterSpawn.</summary>
    public static List<WorldLoadError> CheckSpawnPoints(WorldMap map, IEnumerable<SpawnPoints> spawnPoints)
    {
        var errors = new List<WorldLoadError>();
        foreach (var point in spawnPoints)
        {
            if (!map.TryGetMarker(point.MarkerId, out var marker))
                errors.Add(new(Codes.MissingSpawn, WorldFiles.Markers, $"SpawnPoints '{point.MarkerId}' is not a marker of this world"));
            else if (marker.Kind != MarkerKind.MonsterSpawn)
                errors.Add(new(Codes.MissingSpawn, WorldFiles.Markers,
                    $"SpawnPoints marker '{point.MarkerId}' is a {WorldNames.Of(marker.Kind)}, not a monsterSpawn"));
        }
        return errors;
    }

    /// <summary>
    /// One error per monsterSpawn marker no <see cref="SpawnPoints"/> row names (spec 11.6, WP-M2-7): an authored spawn
    /// point that never spawns is a forgotten row, not a choice.
    /// </summary>
    public static List<WorldLoadError> CheckUnusedSpawnMarkers(WorldMap map, IEnumerable<SpawnPoints> spawnPoints)
    {
        var named = new HashSet<string>(spawnPoints.Select(p => p.MarkerId), StringComparer.Ordinal);
        var errors = new List<WorldLoadError>();
        foreach (var marker in map.Markers)
        {
            if (marker.Kind == MarkerKind.MonsterSpawn && !named.Contains(marker.Id))
                errors.Add(new(Codes.UnusedSpawn, WorldFiles.Markers, $"monsterSpawn marker '{marker.Id}' has no SpawnPoints row"));
        }
        return errors;
    }

    /// <summary>
    /// Spec 11.6 (npc markers): an NPC stands outside the reach of monster leashes, so no monster chases a player onto
    /// an outpost. One error per npc marker within <c>MonsterDef.leashRangeCm</c> of a spawn point of that monster
    /// (WP-M2-7). Spawn points whose marker or monster is missing are reported by the other checks.
    /// </summary>
    public static List<WorldLoadError> CheckNpcsOutsideLeashes(WorldMap map, IEnumerable<SpawnPoints> spawnPoints,
        IEnumerable<MonsterDef> monsters)
    {
        var leash = monsters.ToDictionary(m => m.Id, m => m.LeashRangeCm, StringComparer.Ordinal);
        var errors = new List<WorldLoadError>();
        foreach (var npc in map.Markers.Where(m => m.Kind == MarkerKind.Npc))
        {
            foreach (var point in spawnPoints)
            {
                if (!map.TryGetMarker(point.MarkerId, out var spawn) || !leash.TryGetValue(point.MonsterId, out var rangeCm))
                    continue;
                long dx = FixedPoint.ToCmFloor(npc.Position.X) - (long)FixedPoint.ToCmFloor(spawn.Position.X);
                long dy = FixedPoint.ToCmFloor(npc.Position.Y) - (long)FixedPoint.ToCmFloor(spawn.Position.Y);
                if (dx * dx + dy * dy <= (long)rangeCm * rangeCm)
                    errors.Add(new(Codes.NpcInLeash, WorldFiles.Markers,
                        $"npc marker '{npc.Id}' is within the {rangeCm} cm leash of '{point.MonsterId}' at '{point.MarkerId}'"));
            }
        }
        return errors;
    }

    /// <summary>One error per npc marker whose <c>defId</c> names no <see cref="NpcDef"/> row (spec 11.6, WP-M2-4).</summary>
    public static List<WorldLoadError> CheckNpcMarkers(WorldMap map, IEnumerable<NpcDef> npcs)
    {
        var known = new HashSet<string>(npcs.Select(n => n.Id), StringComparer.Ordinal);
        var errors = new List<WorldLoadError>();
        foreach (var marker in map.Markers)
        {
            if (marker.Kind == MarkerKind.Npc && !known.Contains(marker.DefId))
                errors.Add(new(Codes.BadMarker, WorldFiles.Markers, $"npc marker '{marker.Id}' names unknown NpcDef '{marker.DefId}'"));
        }
        return errors;
    }

    /// <summary>
    /// The markers the config tables name (spec 11.6: marker ids checked against the config tables): the
    /// <c>ServerLoop.PlayerSpawnMarkerId</c> player spawn, every <see cref="SpawnPoints"/> marker and a row for every
    /// monsterSpawn marker, npc markers outside every monster leash, and the <see cref="NpcDef"/> of every npc marker.
    /// Tables that are not loaded are skipped (the config validation reports them).
    /// </summary>
    public static List<WorldLoadError> CheckConfigMarkers(WorldMap map, ConfigSet config)
    {
        var errors = new List<WorldLoadError>();
        var loop = RowsOf<ServerLoop>(config);
        if (loop.Count == 1 && CheckPlayerSpawn(map, loop[0].PlayerSpawnMarkerId) is { } spawnError)
            errors.Add(spawnError);
        var spawnPoints = RowsOf<SpawnPoints>(config);
        errors.AddRange(CheckSpawnPoints(map, spawnPoints));
        if (config.TryGet(typeof(SpawnPoints), out _))
            errors.AddRange(CheckUnusedSpawnMarkers(map, spawnPoints));
        errors.AddRange(CheckNpcsOutsideLeashes(map, spawnPoints, RowsOf<MonsterDef>(config)));
        if (config.TryGet(typeof(NpcDef), out _))
            errors.AddRange(CheckNpcMarkers(map, RowsOf<NpcDef>(config)));
        return errors;
    }

    /// <summary>File list, sizes and hashes: the shared <see cref="WorldPackage"/> checks the client runs too.</summary>
    private static void CheckFiles(string directory, WorldManifest manifest, List<WorldLoadError> errors)
    {
        var listError = WorldPackage.CheckFileList(manifest.Files.Select(f => f.Name).ToList());
        if (listError != null)
        {
            errors.Add(new(Codes.FileMismatch, WorldFiles.Manifest, listError));
            return;
        }

        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(directory, file.Name);
            if (!File.Exists(path))
            {
                errors.Add(new(Codes.MissingFile, file.Name, "listed in the manifest but missing"));
                continue;
            }
            var fileError = WorldPackage.CheckFile(File.ReadAllBytes(path), file.Bytes, file.Sha256);
            if (fileError != null)
                errors.Add(new(Codes.FileMismatch, file.Name, fileError));
        }
    }

    private static WorldGrid? ReadGrid(string directory, WorldManifest manifest, List<WorldLoadError> errors)
    {
        var bytes = File.ReadAllBytes(Path.Combine(directory, WorldFiles.Grid));
        if (!WorldGridReader.TryRead(bytes, out var grid, out var error))
        {
            errors.Add(new(Codes.GridInvalid, WorldFiles.Grid, error!));
            return null;
        }

        var bounds = manifest.Bounds;
        if (!WorldPackage.GeometryMatches(grid!, manifest.CellSizeCm, manifest.WidthCells, manifest.HeightCells,
                bounds.MinXCm, bounds.MinYCm, bounds.MaxXCm, bounds.MaxYCm))
        {
            errors.Add(new(Codes.GridMismatch, WorldFiles.Grid, "grid geometry does not match the manifest's bounds, cell size or size in cells"));
            return null;
        }
        return grid;
    }

    private static List<WorldRegion> ReadRegions(string directory, IReadOnlyList<RegionDef> regionDefs,
        IReadOnlyList<RegionDefaults> regionDefaults, WorldGrid? grid, List<WorldLoadError> errors)
    {
        var result = new List<WorldRegion>();
        var file = ReadJson<RegionsFile>(directory, WorldFiles.Regions, errors);
        if (file == null)
            return result;

        var defs = regionDefs.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var defaults = regionDefaults.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < file.Regions.Count; i++)
        {
            var entry = file.Regions[i];
            // Indices are 1..n in order, so the grid's byte indexes the list directly.
            if (entry.Index != i + 1)
            {
                errors.Add(new(Codes.BadRegion, WorldFiles.Regions, $"entry {i} has index {entry.Index}, expected {i + 1}"));
                continue;
            }
            if (entry.Index > WorldGridFormat.MaxRegions)
            {
                errors.Add(new(Codes.BadRegion, WorldFiles.Regions, $"more than {WorldGridFormat.MaxRegions} regions"));
                break;
            }
            if (!seen.Add(entry.Id))
            {
                errors.Add(new(Codes.BadRegion, WorldFiles.Regions, $"region '{entry.Id}' is listed twice"));
                continue;
            }
            if (!defs.TryGetValue(entry.Id, out var def))
            {
                errors.Add(new(Codes.UnknownRegion, WorldFiles.Regions, $"region '{entry.Id}' does not exist in RegionDef"));
                continue;
            }
            if (!defaults.TryGetValue(def.AreaType, out var areaDefaults))
            {
                // DataValidator reports the broken RegionDef reference itself; the world cannot resolve flags without it.
                errors.Add(new(Codes.UnknownRegion, WorldFiles.Regions, $"region '{entry.Id}' has area type '{def.AreaType}', not in RegionDefaults"));
                continue;
            }
            result.Add(new WorldRegion((byte)entry.Index, entry.Id, def.AreaType, areaDefaults.ToFlags()));
        }

        if (grid != null && grid.MaxRegionIndex > file.Regions.Count)
            errors.Add(new(Codes.BadRegion, WorldFiles.Grid, $"cells use region index {grid.MaxRegionIndex}, regions.json lists {file.Regions.Count}"));
        return result;
    }

    private static List<WorldMarker> ReadMarkers(string directory, WorldGrid? grid, List<WorldLoadError> errors)
    {
        var result = new List<WorldMarker>();
        var file = ReadJson<MarkersFile>(directory, WorldFiles.Markers, errors);
        if (file == null)
            return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        foreach (var entry in file.Markers)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                errors.Add(new(Codes.BadMarker, WorldFiles.Markers, "a marker has an empty id"));
                continue;
            }
            if (!seen.Add(entry.Id))
            {
                errors.Add(new(Codes.DuplicateMarker, WorldFiles.Markers, $"marker id '{entry.Id}' is used twice"));
                continue;
            }
            if (previous != null && string.CompareOrdinal(previous, entry.Id) > 0)
                errors.Add(new(Codes.BadMarker, WorldFiles.Markers, $"markers are not sorted by id at '{entry.Id}'"));
            previous = entry.Id;
            if (!WorldNames.TryParseMarkerKind(entry.Kind, out var kind))
            {
                errors.Add(new(Codes.BadMarker, WorldFiles.Markers, $"marker '{entry.Id}' has unknown kind '{entry.Kind}'"));
                continue;
            }
            if (entry.Facing < 0 || entry.Facing > ushort.MaxValue)
            {
                errors.Add(new(Codes.BadMarker, WorldFiles.Markers, $"marker '{entry.Id}' facing {entry.Facing} is outside 0..65535"));
                continue;
            }
            if (entry.XCm < WorldGridFormat.MinCoordinateCm || entry.XCm > WorldGridFormat.MaxCoordinateCm
                || entry.YCm < WorldGridFormat.MinCoordinateCm || entry.YCm > WorldGridFormat.MaxCoordinateCm)
            {
                errors.Add(new(Codes.BadMarker, WorldFiles.Markers, $"marker '{entry.Id}' is outside the simulation range"));
                continue;
            }
            var position = FixVec2.FromCm(entry.XCm, entry.YCm);
            if (grid != null && !grid.IsWalkable(position))
            {
                errors.Add(new(Codes.BadMarker, WorldFiles.Markers, $"marker '{entry.Id}' at ({entry.XCm}, {entry.YCm}) cm is not on a walkable cell"));
                continue;
            }
            var defId = entry.DefId ?? string.Empty;
            if (WorldNames.NeedsDefId(kind) != (defId.Length > 0))
            {
                errors.Add(new(Codes.BadMarker, WorldFiles.Markers, WorldNames.NeedsDefId(kind)
                    ? $"{WorldNames.Of(kind)} marker '{entry.Id}' has no defId"
                    : $"{WorldNames.Of(kind)} marker '{entry.Id}' has a defId, which only npc markers carry"));
                continue;
            }
            result.Add(new WorldMarker(entry.Id, kind, position, (ushort)entry.Facing, defId));
        }
        return result;
    }

    private static List<WorldBlocker> ReadBlockers(string directory, List<WorldLoadError> errors)
    {
        var result = new List<WorldBlocker>();
        var file = ReadJson<BlockersFile>(directory, WorldFiles.Blockers, errors);
        if (file == null)
            return result;
        foreach (var b in file.Blockers)
        {
            if (b.MinXCm >= b.MaxXCm || b.MinYCm >= b.MaxYCm || b.MinHeightCm > b.MaxHeightCm)
            {
                errors.Add(new(Codes.BadBlocker, WorldFiles.Blockers, $"blocker ({b.MinXCm}, {b.MinYCm})-({b.MaxXCm}, {b.MaxYCm}) cm is empty"));
                continue;
            }
            result.Add(new WorldBlocker(b.MinXCm, b.MinYCm, b.MaxXCm, b.MaxYCm, b.MinHeightCm, b.MaxHeightCm));
        }
        return result;
    }

    private static T? ReadJson<T>(string directory, string name, List<WorldLoadError> errors) where T : class
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            errors.Add(new(Codes.MissingFile, name, $"not found in {Path.GetFullPath(directory)}"));
            return null;
        }
        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), JsonOptions);
            if (value == null)
                errors.Add(new(Codes.BadJson, name, "is null"));
            return value;
        }
        catch (JsonException e)
        {
            errors.Add(new(Codes.BadJson, name, e.Message));
            return null;
        }
    }

    private static IReadOnlyList<T> RowsOf<T>(ConfigSet config) =>
        config.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>().ToList() : [];

    private sealed class RegionsFile
    {
        public List<RegionEntry> Regions { get; set; } = [];
    }

    private sealed class RegionEntry
    {
        public int Index { get; set; }
        public string Id { get; set; } = string.Empty;
    }

    private sealed class MarkersFile
    {
        public List<MarkerEntry> Markers { get; set; } = [];
    }

    private sealed class MarkerEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public int XCm { get; set; }
        public int YCm { get; set; }
        public int Facing { get; set; }
        public string? DefId { get; set; }
    }

    private sealed class BlockersFile
    {
        public List<BlockerEntry> Blockers { get; set; } = [];
    }

    private sealed class BlockerEntry
    {
        public int MinXCm { get; set; }
        public int MinYCm { get; set; }
        public int MaxXCm { get; set; }
        public int MaxYCm { get; set; }
        public int MinHeightCm { get; set; }
        public int MaxHeightCm { get; set; }
    }
}
