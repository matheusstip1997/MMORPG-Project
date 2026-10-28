#nullable enable
using System.Linq;
using Game.Gameplay.Dev;
using Game.Gameplay.Entities;
using Game.Gameplay.Streaming;
using NUnit.Framework;
using Shared.World;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Tests.EditMode.Dev
{
    /// <summary>QA wave 3 F3: the dev scene draws the server's walls and region borders from the exported world.</summary>
    public sealed class WorldGeometryTests
    {
        private const string DevScene = "Assets/Game/Dev/PredictionDev.unity";

        [Test]
        public void Blocked_cells_merge_into_rectangles_that_cover_each_once()
        {
            // 4 x 3, '#' blocked:   row 2: . # # .
            //                       row 1: . # # .
            //                       row 0: # . . .
            var grid = Grid(4, 3, "#...", ".##.", ".##.");

            var rects = WorldGeometry.BlockedRects(grid);

            Assert.That(rects.Select(r => r.ToString()), Is.EquivalentTo(new[] { "(0, 0) 1x1", "(1, 1) 2x2" }));
        }

        [Test]
        public void Region_borders_run_between_different_regions_and_round_the_world_edge()
        {
            // 2 x 2: column 0 is region 1, column 1 is region 2.
            var grid = Grid(2, 2, "..", "..", regions: new byte[] { 1, 2, 1, 2 });

            var edges = WorldGeometry.RegionEdges(grid).Select(e => e.ToString()).ToList();

            Assert.That(edges, Is.EquivalentTo(new[]
            {
                "x line 0 from 0 for 2", "x line 1 from 0 for 2", "x line 2 from 0 for 2", // west edge, the border, east edge
                "y line 0 from 0 for 2", "y line 2 from 0 for 2", // south and north edges
            }));
        }

        [Test]
        public void The_m1_world_draws_every_blocked_cell_and_the_city_border()
        {
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out var error), Is.True, error);
            int blocked = 0;
            for (int row = 0; row < grid!.Height; row++)
            {
                for (int column = 0; column < grid.Width; column++)
                {
                    if (!grid.IsWalkableCell(column, row))
                        blocked++;
                }
            }

            var rects = WorldGeometry.BlockedRects(grid);
            var edges = WorldGeometry.RegionEdges(grid);

            Assert.That(blocked, Is.GreaterThan(0));
            Assert.That(rects.Sum(r => r.Width * r.Height), Is.EqualTo(blocked), "every blocked cell drawn once");
            Assert.That(edges.Any(e => e.Vertical && e.Line > 0 && e.Line < grid.Width), Is.True, "a border inside the world (city wall)");
        }

        [Test]
        public void Committed_dev_scene_draws_the_world_and_loads_the_authored_world_scene()
        {
            var previous = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.OpenScene(DevScene, OpenSceneMode.Single);
                var view = Object.FindAnyObjectByType<WorldGeometryView>();
                Assert.That(view, Is.Not.Null, "WorldGeometryView in " + DevScene + " (rebuild it with DevSceneBuilder)");

                // WP-M2-7: the authored world scene is loaded at play, so the player walks on what the world authors built;
                // the dev ground plane and its reference blocks (which could stand on the city spawn) are gone.
                var loader = Object.FindAnyObjectByType<Game.Gameplay.Streaming.WorldSceneLoader>();
                Assert.That(loader, Is.Not.Null, "WorldSceneLoader in " + DevScene);
                Assert.That(loader!.ScenePath, Is.EqualTo("Assets/Scenes/M1Sample.unity"));
                Assert.That(GameObject.Find("Reference Blocks"), Is.Null);
                Assert.That(Object.FindObjectsByType<Light>(FindObjectsSortMode.None), Is.Empty, "the world scene brings the light");
            }
            finally
            {
                if (previous.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(previous);
            }
        }

        /// <summary>A grid from rows written bottom row first ('#' blocked); every cell in region 1 unless given.</summary>
        private static WorldGrid Grid(int width, int height, string row0, string row1, string? row2 = null, byte[]? regions = null)
        {
            var rows = row2 == null ? new[] { row0, row1 } : new[] { row0, row1, row2 };
            var flags = new byte[width * height];
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                    flags[row * width + column] = rows[row][column] == '#' ? (byte)0 : WorldGridFormat.FlagWalkable;
            }
            regions ??= Enumerable.Repeat((byte)1, width * height).ToArray();
            return new WorldGrid(0, 0, 50, width, height, 0, flags, regions, new short[width * height]);
        }
    }
}
