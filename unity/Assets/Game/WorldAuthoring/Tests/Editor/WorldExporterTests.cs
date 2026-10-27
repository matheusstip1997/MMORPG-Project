#nullable enable
using System.IO;
using System.Linq;
using System.Text;
using Game.WorldAuthoring.Editor;
using NUnit.Framework;
using Shared.Simulation;
using Shared.World;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.WorldAuthoring.Tests
{
    /// <summary>UnityExporter v0 (spec 11.6, backlog C2): deterministic output, rasterization and the validator.</summary>
    public sealed class WorldExporterTests
    {
        private GameObject _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("ExportTestRoot");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
        }

        // A 20 x 20 m flat ground (top at height 0) inside one region, 50 cm cells.
        private void FlatWorld(int agentRadiusCm = 40, string regionId = "m1.city")
        {
            var settings = Child("Settings").AddComponent<WorldExportSettings>();
            settings.worldId = "test";
            settings.cellSizeCm = 50;
            settings.agentRadiusCm = agentRadiusCm;
            Cube("Ground", new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));
            Region("Region", regionId, Vector3.zero, new Vector3(20f, 10f, 20f));
        }

        private GameObject Child(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            return go;
        }

        private GameObject Cube(string name, Vector3 position, Vector3 scale, bool blocker = false)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(_root.transform, false);
            cube.transform.position = position;
            cube.transform.localScale = scale;
            if (blocker)
                cube.AddComponent<ServerBlocker>();
            return cube;
        }

        private RegionVolume Region(string name, string id, Vector3 position, Vector3 size)
        {
            var volume = Child(name).AddComponent<RegionVolume>();
            volume.regionId = id;
            volume.transform.position = position;
            volume.size = size;
            return volume;
        }

        private ServerMarker Marker(string id, Vector3 position, float yaw = 0f)
        {
            var marker = Child("Marker " + id).AddComponent<ServerMarker>();
            marker.markerId = id;
            marker.kind = MarkerKind.PlayerSpawn;
            marker.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            return marker;
        }

        private WorldExportResult Export() => WorldExporter.Export(new[] { _root }, string.Empty);

        private static WorldGrid Grid(WorldExportResult result)
        {
            Assert.IsTrue(WorldGridReader.TryRead(result.Files[WorldFiles.Grid], out var grid, out var error), error);
            return grid!;
        }

        private static string Text(WorldExportResult result, string name) => Encoding.UTF8.GetString(result.Files[name]);

        private static void AssertError(WorldExportResult result, string code)
        {
            Assert.IsFalse(result.Ok);
            Assert.IsTrue(result.Errors.Any(e => e.Code == code), string.Join("\n", result.Errors));
            Assert.IsEmpty(result.Files);
        }

        [Test]
        public void Exporting_the_same_objects_twice_gives_identical_bytes()
        {
            FlatWorld();
            Cube("Wall", new Vector3(2f, 1f, 0f), new Vector3(1f, 2f, 6f), blocker: true);
            Marker("b.spawn", new Vector3(-5f, 0f, 0f), 90f);
            Marker("a.spawn", new Vector3(-5f, 0f, 3f));

            var first = Export();
            var second = Export();

            Assert.IsTrue(first.Ok, string.Join("\n", first.Errors));
            CollectionAssert.AreEqual(first.Files.Keys, second.Files.Keys);
            foreach (var name in first.Files.Keys)
                CollectionAssert.AreEqual(first.Files[name], second.Files[name], name);
        }

        [Test]
        public void The_committed_sample_world_matches_a_fresh_export_of_the_sample_scene()
        {
            var scene = EditorSceneManager.OpenScene(SampleWorldBuilder.ScenePath, OpenSceneMode.Additive);
            WorldExportResult first, second;
            try
            {
                first = WorldExporter.Export(scene);
                second = WorldExporter.Export(scene);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, removeScene: true);
            }

            Assert.IsTrue(first.Ok, string.Join("\n", first.Errors));
            var committed = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "data", "world", "m1"));
            foreach (var name in first.Files.Keys)
            {
                CollectionAssert.AreEqual(first.Files[name], second.Files[name], "export is not deterministic: " + name);
                CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(committed, name)), first.Files[name],
                    $"data/world/m1/{name} is stale: run WorldExporterBatch.Export and commit the result");
            }
        }

        [Test]
        public void The_sample_spawn_is_walkable_and_in_the_city()
        {
            var scene = EditorSceneManager.OpenScene(SampleWorldBuilder.ScenePath, OpenSceneMode.Additive);
            WorldExportResult result;
            try
            {
                result = WorldExporter.Export(scene);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, removeScene: true);
            }

            var grid = Grid(result);
            var spawn = FixVec2.FromCm(-3_000, 0);
            Assert.IsTrue(grid.IsWalkable(spawn));
            StringAssert.Contains("\"id\": \"m1.city\"", Text(result, WorldFiles.Regions));
            Assert.AreEqual(1, grid.RegionIndexAt(spawn)); // m1.city sorts before m1.combat
            Assert.AreEqual(2, grid.RegionIndexAt(FixVec2.FromCm(2_000, 0)));
            StringAssert.Contains("\"id\": \"m1.spawn.city\"", Text(result, WorldFiles.Markers));
            StringAssert.Contains("\"facing\": 16384", Text(result, WorldFiles.Markers));
            // The raised platform is ground 50 cm up; the wall is not walkable; the gate is.
            Assert.IsTrue(grid.TryGetHeightCm(FixVec2.FromCm(4_000, -2_000), out var platform));
            Assert.AreEqual(50, platform);
            Assert.IsFalse(grid.IsWalkable(FixVec2.FromCm(-1_000, 1_000)));
            Assert.IsTrue(grid.IsWalkable(FixVec2.FromCm(-1_000, 0)));
        }

        [Test]
        public void Blockers_block_every_cell_they_touch_grown_by_the_agent_radius()
        {
            FlatWorld(agentRadiusCm: 40);
            Cube("Post", new Vector3(0f, 1f, 0f), new Vector3(1f, 2f, 1f), blocker: true); // -50..50 cm

            var result = Export();

            Assert.IsTrue(result.Ok, string.Join("\n", result.Errors));
            var grid = Grid(result);
            // Grown to -90..90 cm: the cells [-100, 100) are blocked, the next ones are not.
            Assert.IsFalse(grid.IsWalkable(FixVec2.FromCm(-99, 0)));
            Assert.IsFalse(grid.IsWalkable(FixVec2.FromCm(99, 0)));
            Assert.IsTrue(grid.IsWalkable(FixVec2.FromCm(-101, 0)));
            Assert.IsTrue(grid.IsWalkable(FixVec2.FromCm(100, 0)));
            StringAssert.Contains("\"minXCm\": -50", Text(result, WorldFiles.Blockers));
        }

        [Test]
        public void The_shared_collision_cannot_cross_an_exported_wall()
        {
            FlatWorld();
            Cube("Wall", new Vector3(0f, 1f, 0f), new Vector3(0.2f, 2f, 20f), blocker: true);

            var grid = Grid(Export());
            var state = new MovementState(FixVec2.FromCm(-300, 0), 0);
            for (int tick = 0; tick < 100; tick++)
                state = MovementSim.Step(state, new MovementInput(MoveKeys.D, 0), false, 400, 20, grid);

            Assert.Less(state.Position.X, FixVec2.FromCm(0, 0).X);
            Assert.IsTrue(grid.IsWalkable(state.Position));
        }

        [Test]
        public void Bounds_are_the_regions_rounded_out_to_cells_and_the_world_ends_there()
        {
            FlatWorld();

            var result = Export();

            Assert.AreEqual((40, 40), (result.WidthCells, result.HeightCells));
            StringAssert.Contains("\"minXCm\": -1000", Text(result, WorldFiles.Manifest));
            var grid = Grid(result);
            Assert.IsFalse(grid.IsWalkable(FixVec2.FromCm(1_000, 0)));
            Assert.IsTrue(grid.IsWalkable(FixVec2.FromCm(999, 0)));
        }

        [Test]
        public void The_manifest_lists_every_file_with_its_size_and_hash()
        {
            FlatWorld();

            var result = Export();
            var manifest = Text(result, WorldFiles.Manifest);

            StringAssert.Contains($"\"formatVersion\": {WorldFiles.ManifestVersion}", manifest);
            foreach (var name in WorldFiles.DataFiles)
            {
                StringAssert.Contains($"\"name\": \"{name}\"", manifest);
                StringAssert.Contains(WorldExporter.Sha256Hex(result.Files[name]), manifest);
            }
            Assert.IsFalse(manifest.Contains("\r"));
        }

        [Test]
        public void Overlapping_region_volumes_are_refused()
        {
            FlatWorld();
            Region("Overlap", "m1.combat", new Vector3(5f, 0f, 0f), new Vector3(4f, 10f, 4f));

            AssertError(Export(), ExportCodes.RegionOverlap);
        }

        [Test]
        public void Walkable_ground_outside_every_region_is_refused()
        {
            FlatWorld();
            Region("Second", "m1.combat", new Vector3(15f, 0f, 0f), new Vector3(10f, 10f, 2f));
            // The bounds now reach X = 20 m with ground only up to 10 m, and Z +-10 m is outside the second region:
            Cube("Extra Ground", new Vector3(15f, -0.5f, 5f), new Vector3(10f, 1f, 4f));

            AssertError(Export(), ExportCodes.WalkableWithoutRegion);
        }

        [Test]
        public void Duplicate_marker_ids_are_refused()
        {
            FlatWorld();
            Marker("m1.spawn", new Vector3(1f, 0f, 1f));
            Marker("m1.spawn", new Vector3(2f, 0f, 2f));

            AssertError(Export(), ExportCodes.DuplicateMarker);
        }

        [Test]
        public void A_marker_on_a_blocker_or_outside_the_bounds_is_refused()
        {
            FlatWorld();
            Cube("Rock", new Vector3(3f, 1f, 3f), new Vector3(2f, 2f, 2f), blocker: true);
            Marker("on.rock", new Vector3(3f, 0f, 3f));
            Marker("far.away", new Vector3(50f, 0f, 0f));

            var result = Export();

            AssertError(result, ExportCodes.MarkerNotWalkable);
            AssertError(result, ExportCodes.MarkerOutOfBounds);
        }

        [Test]
        public void An_npc_marker_exports_its_defId_and_the_other_markers_none()
        {
            FlatWorld();
            Marker("a.spawn", new Vector3(-5f, 0f, 0f));
            var npc = Marker("b.npc", new Vector3(5f, 0f, 0f), 270f);
            npc.kind = MarkerKind.Npc;
            npc.defId = "m1.outpost";

            var result = Export();

            Assert.IsTrue(result.Ok, string.Join("\n", result.Errors));
            string markers = Text(result, WorldFiles.Markers);
            StringAssert.Contains("\"kind\": \"npc\"", markers);
            StringAssert.Contains("\"defId\": \"m1.outpost\"", markers);
            Assert.AreEqual(1, markers.Split("defId").Length - 1); // only the npc marker carries one (WP-M2-4)
        }

        [Test]
        public void An_npc_marker_without_a_defId_or_a_defId_on_another_kind_is_refused()
        {
            FlatWorld();
            var npc = Marker("a.npc", new Vector3(5f, 0f, 0f));
            npc.kind = MarkerKind.Npc;
            var spawn = Marker("b.spawn", new Vector3(-5f, 0f, 0f));
            spawn.defId = "m1.outpost";

            var result = Export();

            AssertError(result, ExportCodes.MissingMarkerDef);
            AssertError(result, ExportCodes.UnexpectedMarkerDef);
        }

        [Test]
        public void Blockers_turned_off_the_90_degree_grid_are_refused()
        {
            FlatWorld();
            var wall = Cube("Diagonal Wall", new Vector3(0f, 1f, 0f), new Vector3(1f, 2f, 5f), blocker: true);
            wall.transform.rotation = Quaternion.Euler(0f, 45f, 0f);

            AssertError(Export(), ExportCodes.BadShape);
        }

        [Test]
        public void A_scene_needs_exactly_one_settings_object_and_valid_region_ids()
        {
            Cube("Ground", new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));
            AssertError(Export(), ExportCodes.MissingSettings);

            Object.DestroyImmediate(_root);
            _root = new GameObject("ExportTestRoot");
            FlatWorld(regionId: "bad id!");
            AssertError(Export(), ExportCodes.BadRegionId);
        }

        [Test]
        public void The_report_lists_the_errors()
        {
            FlatWorld();
            Marker("x", new Vector3(1f, 0f, 1f));
            Marker("x", new Vector3(2f, 0f, 2f));

            var report = Encoding.UTF8.GetString(WorldExporter.ReportJson(Export()));

            StringAssert.Contains("\"ok\": false", report);
            StringAssert.Contains(ExportCodes.DuplicateMarker, report);
        }
    }
}
