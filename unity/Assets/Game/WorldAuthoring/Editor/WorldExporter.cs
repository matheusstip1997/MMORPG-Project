#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Shared.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>
    /// UnityExporter v0 (spec 11.6, backlog C2): turns the <see cref="ServerBlocker"/>s, <see cref="RegionVolume"/>s,
    /// <see cref="ServerMarker"/>s and ground colliders of a scene into the server's world files (see
    /// <c>Shared.World.WorldFiles</c>), validating as it goes. Output is deterministic: lists are sorted, coordinates are
    /// whole cm, and the files carry no timestamps, so the same scene always gives the same bytes.
    /// <list type="bullet">
    /// <item>Bounds: the union of the region volumes, rounded out to whole cells.</item>
    /// <item>Ground and height: a ray down through each cell center; the nearest hit on a collider that is not a
    /// blocker. No hit means no ground: the cell is not walkable.</item>
    /// <item>Blockers: every cell whose square touches a blocker's footprint grown by the agent radius is not walkable.</item>
    /// <item>Regions: the volume containing the cell center ([min, max) on both axes). Two volumes on one cell, or
    /// walkable ground in none, is an error.</item>
    /// </list>
    /// TODO(open): one walkable layer only (overlapping surfaces, see Shared.World.WorldGridFormat); blockers block at
    /// every height. Chunked heightmaps and lazy loading come with the main map.
    /// </summary>
    public static class WorldExporter
    {
        public const string ToolVersion = "unity-exporter 0.1.0";

        private static readonly Regex IdPattern = new Regex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

        public static WorldExportResult Export(Scene scene) => Export(scene.GetRootGameObjects(), scene.path);

        /// <param name="roots">The objects to export, with their children.</param>
        /// <param name="sourceScenePath">Project-relative scene path recorded (with its hash) in the manifest; empty for none.</param>
        public static WorldExportResult Export(IReadOnlyList<GameObject> roots, string sourceScenePath)
        {
            var result = new WorldExportResult { SourceScene = sourceScenePath };
            var settings = Settings(roots, result);
            if (settings == null)
                return result;
            result.WorldId = settings.worldId;
            int cell = settings.cellSizeCm;
            int radius = settings.agentRadiusCm;

            var regions = CollectRegions(roots, result);
            var blockers = CollectBlockers(roots, result);
            if (!result.Ok)
                return result;
            if (regions.Count == 0)
            {
                result.Error(ExportCodes.NoRegions, string.Empty, "the scene has no RegionVolume, so it has no bounds");
                return result;
            }

            // Bounds: the union of the regions, rounded out to whole cells.
            int originX = FloorTo(regions.Min(r => r.Box.MinX), cell);
            int originY = FloorTo(regions.Min(r => r.Box.MinY), cell);
            long maxX = CeilTo(regions.Max(r => r.Box.MaxX), cell);
            long maxY = CeilTo(regions.Max(r => r.Box.MaxY), cell);
            long width = (maxX - originX) / cell;
            long height = (maxY - originY) / cell;
            if (width * height > WorldGridFormat.MaxCells || originX < WorldGridFormat.MinCoordinateCm
                || originY < WorldGridFormat.MinCoordinateCm || maxX > WorldGridFormat.MaxCoordinateCm
                || maxY > WorldGridFormat.MaxCoordinateCm)
            {
                result.Error(ExportCodes.SizeLimit, string.Empty,
                    $"bounds {width} x {height} cells exceed the grid format limits ({WorldGridFormat.MaxCells} cells)");
                return result;
            }
            var grid = new GridBuilder(originX, originY, cell, (int)width, (int)height);
            result.WidthCells = grid.Width;
            result.HeightCells = grid.Height;

            // Region ids sorted ordinally get indices 1..n.
            var regionIds = regions.Select(r => r.Id).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList();
            if (regionIds.Count > WorldGridFormat.MaxRegions)
            {
                result.Error(ExportCodes.TooManyRegions, string.Empty, $"{regionIds.Count} region ids, the format holds {WorldGridFormat.MaxRegions}");
                return result;
            }
            result.RegionCount = regionIds.Count;

            RasterizeRegions(grid, regions, regionIds, result);
            SampleGround(grid, roots);
            RasterizeBlockers(grid, blockers, radius);
            CheckCells(grid, result);
            var markers = CollectMarkers(roots, grid, result);
            if (!result.Ok)
                return result;

            var gridFile = grid.Build(result);
            if (gridFile == null)
                return result;

            result.WalkableCells = grid.WalkableCount;
            result.MarkerCount = markers.Count;
            result.BlockerCount = blockers.Count;
            result.Files[WorldFiles.Grid] = WorldGridWriter.Write(gridFile);
            result.Files[WorldFiles.Regions] = RegionsJson(regionIds);
            result.Files[WorldFiles.Markers] = MarkersJson(markers);
            result.Files[WorldFiles.Blockers] = BlockersJson(blockers);
            result.Files[WorldFiles.Manifest] = ManifestJson(result, settings, gridFile, sourceScenePath);
            return result;
        }

        /// <summary>Writes the files of a successful export, the manifest last so a partial write never verifies.</summary>
        public static void WriteFiles(WorldExportResult result, string directory)
        {
            if (!result.Ok)
                throw new InvalidOperationException("Cannot write an export that has errors.");
            Directory.CreateDirectory(directory);
            foreach (var name in WorldFiles.DataFiles)
                File.WriteAllBytes(Path.Combine(directory, name), result.Files[name]);
            File.WriteAllBytes(Path.Combine(directory, WorldFiles.Manifest), result.Files[WorldFiles.Manifest]);
        }

        /// <summary>The validator's JSON report (spec 11.6).</summary>
        public static byte[] ReportJson(WorldExportResult result)
        {
            var json = new DeterministicJson().BeginObject()
                .Field("ok", result.Ok)
                .Field("tool", ToolVersion)
                .Field("scene", result.SourceScene)
                .Field("worldId", result.WorldId)
                .BeginObject("summary")
                .Field("widthCells", result.WidthCells)
                .Field("heightCells", result.HeightCells)
                .Field("walkableCells", result.WalkableCells)
                .Field("regions", result.RegionCount)
                .Field("markers", result.MarkerCount)
                .Field("blockers", result.BlockerCount)
                .EndObject()
                .BeginArray("errors");
            foreach (var error in result.Errors)
                json.BeginObject().Field("code", error.Code).Field("object", error.ObjectPath).Field("message", error.Message).EndObject();
            return json.EndArray().EndObject().ToBytes();
        }

        /// <summary>SHA-256 of a scene file with its line endings normalized to LF, so a Windows checkout hashes the same.</summary>
        public static string SourceHash(string projectRelativePath)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath)!;
            var text = File.ReadAllText(Path.Combine(projectRoot, projectRelativePath)).Replace("\r\n", "\n");
            return Sha256Hex(new UTF8Encoding(false).GetBytes(text));
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(data);
                var text = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    text.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        public static string PathOf(Transform transform)
        {
            var path = transform.name;
            for (var parent = transform.parent; parent != null; parent = parent.parent)
                path = parent.name + "/" + path;
            return path;
        }

        private static WorldExportSettings? Settings(IReadOnlyList<GameObject> roots, WorldExportResult result)
        {
            var all = Components<WorldExportSettings>(roots);
            if (all.Count != 1)
            {
                result.Error(ExportCodes.MissingSettings, string.Empty, $"expected one WorldExportSettings, found {all.Count}");
                return null;
            }
            var settings = all[0];
            var path = PathOf(settings.transform);
            if (!IdPattern.IsMatch(settings.worldId ?? string.Empty))
                result.Error(ExportCodes.BadSettings, path, $"world id '{settings.worldId}' must use letters, digits, '.', '_' or '-'");
            if (settings.cellSizeCm <= 0)
                result.Error(ExportCodes.BadSettings, path, $"cell size {settings.cellSizeCm} cm must be positive");
            if (settings.agentRadiusCm < 0)
                result.Error(ExportCodes.BadSettings, path, $"agent radius {settings.agentRadiusCm} cm must not be negative");
            return result.Ok ? settings : null;
        }

        private sealed class RegionShape
        {
            public RegionShape(string id, CmBox box, string path)
            {
                Id = id;
                Box = box;
                Path = path;
            }

            public string Id { get; }
            public CmBox Box { get; }
            public string Path { get; }
        }

        private static List<RegionShape> CollectRegions(IReadOnlyList<GameObject> roots, WorldExportResult result)
        {
            var shapes = new List<RegionShape>();
            foreach (var volume in Components<RegionVolume>(roots))
            {
                var path = PathOf(volume.transform);
                if (!IdPattern.IsMatch(volume.regionId ?? string.Empty))
                {
                    result.Error(ExportCodes.BadRegionId, path, $"region id '{volume.regionId}' must name a RegionDef row (letters, digits, '.', '_', '-')");
                    continue;
                }
                if (!CmBox.TryFromLocalBox(volume.transform, volume.center, volume.size, out var box, out var error))
                {
                    result.Error(ExportCodes.BadShape, path, error);
                    continue;
                }
                shapes.Add(new RegionShape(volume.regionId!, box, path));
            }
            return shapes;
        }

        private static List<CmBox> CollectBlockers(IReadOnlyList<GameObject> roots, WorldExportResult result)
        {
            var boxes = new List<CmBox>();
            foreach (var blocker in Components<ServerBlocker>(roots))
            {
                if (!CmBox.TryFromLocalBox(blocker.transform, blocker.center, blocker.size, out var box, out var error))
                {
                    result.Error(ExportCodes.BadShape, PathOf(blocker.transform), error);
                    continue;
                }
                boxes.Add(box);
            }
            boxes.Sort((a, b) =>
            {
                int c = a.MinX.CompareTo(b.MinX);
                if (c == 0) c = a.MinY.CompareTo(b.MinY);
                if (c == 0) c = a.MaxX.CompareTo(b.MaxX);
                if (c == 0) c = a.MaxY.CompareTo(b.MaxY);
                if (c == 0) c = a.MinH.CompareTo(b.MinH);
                if (c == 0) c = a.MaxH.CompareTo(b.MaxH);
                return c;
            });
            return boxes;
        }

        private static void RasterizeRegions(GridBuilder grid, List<RegionShape> regions, List<string> regionIds, WorldExportResult result)
        {
            var overlaps = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var region in regions.OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Box.MinX).ThenBy(r => r.Box.MinY))
            {
                byte index = (byte)(regionIds.IndexOf(region.Id) + 1);
                var box = region.Box;
                for (int y = 0; y < grid.Height; y++)
                {
                    long centerY2 = grid.CenterTwice(grid.OriginY, y);
                    if (centerY2 < 2L * box.MinY || centerY2 >= 2L * box.MaxY)
                        continue;
                    for (int x = 0; x < grid.Width; x++)
                    {
                        long centerX2 = grid.CenterTwice(grid.OriginX, x);
                        if (centerX2 < 2L * box.MinX || centerX2 >= 2L * box.MaxX)
                            continue;
                        int i = grid.Index(x, y);
                        if (grid.RegionOwner[i] != null)
                        {
                            var key = string.CompareOrdinal(grid.RegionOwner[i], region.Path) < 0
                                ? grid.RegionOwner[i] + " and " + region.Path
                                : region.Path + " and " + grid.RegionOwner[i];
                            overlaps[key] = overlaps.TryGetValue(key, out var n) ? n + 1 : 1;
                            continue;
                        }
                        grid.RegionOwner[i] = region.Path;
                        grid.Regions[i] = index;
                    }
                }
            }
            foreach (var pair in overlaps)
                result.Error(ExportCodes.RegionOverlap, pair.Key, $"region volumes overlap on {pair.Value} cell(s); every cell belongs to one region");
        }

        private static void SampleGround(GridBuilder grid, IReadOnlyList<GameObject> roots)
        {
            var ground = new HashSet<Collider>();
            float top = float.MinValue;
            float bottom = float.MaxValue;
            foreach (var collider in Components<Collider>(roots))
            {
                if (!collider.enabled || collider.isTrigger || collider.GetComponentInParent<ServerBlocker>() != null)
                    continue;
                ground.Add(collider);
                top = Mathf.Max(top, collider.bounds.max.y);
                bottom = Mathf.Min(bottom, collider.bounds.min.y);
            }
            if (ground.Count == 0)
                return;

            Physics.SyncTransforms();
            var physics = Physics.defaultPhysicsScene;
            var hits = new RaycastHit[64];
            float startY = top + 1f;
            float distance = top - bottom + 2f;
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    var origin = new Vector3(grid.CenterTwice(grid.OriginX, x) / 200f, startY, grid.CenterTwice(grid.OriginY, y) / 200f);
                    int count = physics.Raycast(origin, Vector3.down, hits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    float best = float.MaxValue;
                    float hitY = 0f;
                    for (int h = 0; h < count; h++)
                    {
                        if (!ground.Contains(hits[h].collider) || hits[h].distance >= best)
                            continue;
                        best = hits[h].distance;
                        hitY = hits[h].point.y;
                    }
                    if (best == float.MaxValue)
                        continue;
                    int i = grid.Index(x, y);
                    grid.HasGround[i] = true;
                    grid.HeightCm[i] = CmBox.ToCm(hitY);
                }
            }
        }

        private static void RasterizeBlockers(GridBuilder grid, List<CmBox> blockers, int radius)
        {
            foreach (var box in blockers)
            {
                long minX = (long)box.MinX - radius, maxX = (long)box.MaxX + radius;
                long minY = (long)box.MinY - radius, maxY = (long)box.MaxY + radius;
                // A cell is blocked when its square [start, start + cell) overlaps the grown footprint.
                int x0 = Math.Max(0, (int)FloorDiv(minX - grid.OriginX, grid.Cell));
                int x1 = Math.Min(grid.Width - 1, (int)(CeilDiv(maxX - grid.OriginX, grid.Cell) - 1));
                int y0 = Math.Max(0, (int)FloorDiv(minY - grid.OriginY, grid.Cell));
                int y1 = Math.Min(grid.Height - 1, (int)(CeilDiv(maxY - grid.OriginY, grid.Cell) - 1));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        grid.Blocked[grid.Index(x, y)] = true;
            }
        }

        private static void CheckCells(GridBuilder grid, WorldExportResult result)
        {
            int orphans = 0;
            int firstX = 0, firstY = 0;
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int i = grid.Index(x, y);
                    if (!grid.IsWalkable(i) || grid.Regions[i] != 0)
                        continue;
                    if (orphans++ == 0)
                    {
                        firstX = x;
                        firstY = y;
                    }
                }
            }
            if (orphans > 0)
                result.Error(ExportCodes.WalkableWithoutRegion, string.Empty,
                    $"{orphans} walkable cell(s) are in no RegionVolume, first at cell ({firstX}, {firstY}) = " +
                    $"({grid.OriginX + firstX * grid.Cell}, {grid.OriginY + firstY * grid.Cell}) cm");
        }

        private sealed class MarkerEntry
        {
            public MarkerEntry(string id, MarkerKind kind, int xCm, int yCm, int facing)
            {
                Id = id;
                Kind = kind;
                XCm = xCm;
                YCm = yCm;
                Facing = facing;
            }

            public string Id { get; }
            public MarkerKind Kind { get; }
            public int XCm { get; }
            public int YCm { get; }
            public int Facing { get; }
        }

        private static List<MarkerEntry> CollectMarkers(IReadOnlyList<GameObject> roots, GridBuilder grid, WorldExportResult result)
        {
            var entries = new List<MarkerEntry>();
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var marker in Components<ServerMarker>(roots))
            {
                var path = PathOf(marker.transform);
                var id = marker.markerId ?? string.Empty;
                if (!IdPattern.IsMatch(id))
                {
                    result.Error(ExportCodes.BadMarkerId, path, $"marker id '{id}' must use letters, digits, '.', '_' or '-'");
                    continue;
                }
                if (seen.TryGetValue(id, out var other))
                {
                    result.Error(ExportCodes.DuplicateMarker, path, $"marker id '{id}' is also used by {other}");
                    continue;
                }
                seen.Add(id, path);
                if (!Enum.IsDefined(typeof(MarkerKind), marker.kind))
                {
                    result.Error(ExportCodes.BadMarkerKind, path, $"marker kind {(int)marker.kind} is not defined");
                    continue;
                }

                var position = marker.transform.position;
                int xCm = CmBox.ToCm(position.x);
                int yCm = CmBox.ToCm(position.z);
                int column = (int)FloorDiv((long)xCm - grid.OriginX, grid.Cell);
                int row = (int)FloorDiv((long)yCm - grid.OriginY, grid.Cell);
                if (column < 0 || column >= grid.Width || row < 0 || row >= grid.Height)
                {
                    result.Error(ExportCodes.MarkerOutOfBounds, path, $"marker at ({xCm}, {yCm}) cm is outside the world bounds");
                    continue;
                }
                if (!grid.IsWalkable(grid.Index(column, row)))
                {
                    result.Error(ExportCodes.MarkerNotWalkable, path, $"marker at ({xCm}, {yCm}) cm is on a cell that is not walkable");
                    continue;
                }
                // 16-bit binary angle, 0 = +Z, clockwise seen from above: Unity's yaw has the same sense.
                int facing = Mathf.RoundToInt(Mathf.Repeat(marker.transform.eulerAngles.y, 360f) * 65536f / 360f) & 0xFFFF;
                entries.Add(new MarkerEntry(id, marker.kind, xCm, yCm, facing));
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return entries;
        }

        private static byte[] RegionsJson(List<string> regionIds)
        {
            var json = new DeterministicJson().BeginObject().BeginArray("regions");
            for (int i = 0; i < regionIds.Count; i++)
                json.BeginObject().Field("index", i + 1).Field("id", regionIds[i]).EndObject();
            return json.EndArray().EndObject().ToBytes();
        }

        private static byte[] MarkersJson(List<MarkerEntry> markers)
        {
            var json = new DeterministicJson().BeginObject().BeginArray("markers");
            foreach (var m in markers)
                json.BeginObject().Field("id", m.Id).Field("kind", WorldNames.Of(m.Kind)).Field("xCm", m.XCm)
                    .Field("yCm", m.YCm).Field("facing", m.Facing).EndObject();
            return json.EndArray().EndObject().ToBytes();
        }

        private static byte[] BlockersJson(List<CmBox> blockers)
        {
            var json = new DeterministicJson().BeginObject().BeginArray("blockers");
            foreach (var b in blockers)
                json.BeginObject().Field("minXCm", b.MinX).Field("minYCm", b.MinY).Field("maxXCm", b.MaxX).Field("maxYCm", b.MaxY)
                    .Field("minHeightCm", b.MinH).Field("maxHeightCm", b.MaxH).EndObject();
            return json.EndArray().EndObject().ToBytes();
        }

        private static byte[] ManifestJson(WorldExportResult result, WorldExportSettings settings, WorldGrid grid, string sourceScenePath)
        {
            var json = new DeterministicJson().BeginObject()
                .Field("format", WorldFiles.ManifestFormat)
                .Field("formatVersion", WorldFiles.ManifestVersion)
                .Field("toolVersion", ToolVersion)
                .Field("worldId", settings.worldId)
                .Field("cellSizeCm", grid.CellSizeCm)
                .Field("agentRadiusCm", settings.agentRadiusCm)
                .BeginObject("bounds")
                .Field("minXCm", grid.OriginXCm)
                .Field("minYCm", grid.OriginYCm)
                .Field("maxXCm", (long)grid.OriginXCm + (long)grid.Width * grid.CellSizeCm)
                .Field("maxYCm", (long)grid.OriginYCm + (long)grid.Height * grid.CellSizeCm)
                .EndObject()
                .Field("widthCells", grid.Width)
                .Field("heightCells", grid.Height)
                .BeginArray("sources");
            if (sourceScenePath.Length > 0)
                json.BeginObject().Field("path", sourceScenePath).Field("sha256", SourceHash(sourceScenePath)).EndObject();
            json.EndArray().BeginArray("files");
            foreach (var name in WorldFiles.DataFiles)
            {
                var bytes = result.Files[name];
                json.BeginObject().Field("name", name).Field("bytes", bytes.LongLength).Field("sha256", Sha256Hex(bytes)).EndObject();
            }
            return json.EndArray().EndObject().ToBytes();
        }

        /// <summary>Components on active objects under the roots, in hierarchy order.</summary>
        private static List<T> Components<T>(IReadOnlyList<GameObject> roots) where T : Component
        {
            var list = new List<T>();
            foreach (var root in roots)
            {
                if (root != null)
                    list.AddRange(root.GetComponentsInChildren<T>(includeInactive: false));
            }
            return list;
        }

        private static int FloorTo(int value, int step) => (int)(FloorDiv(value, step) * step);
        private static long CeilTo(int value, int step) => CeilDiv(value, step) * step;

        private static long FloorDiv(long value, long divisor)
        {
            long q = value / divisor;
            if (value % divisor != 0 && value < 0)
                q--;
            return q;
        }

        private static long CeilDiv(long value, long divisor) => -FloorDiv(-value, divisor);
    }
}
