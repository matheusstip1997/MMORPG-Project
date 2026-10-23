#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Input;
using NUnit.Framework;
using Shared.Protocol;
using Shared.Simulation;
using Shared.World;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace Game.Dev.Qa.EditorTests
{
    public sealed class QaNavigationTests
    {
        private const int CellCm = 50;
        private const int Cells = 40; // 20 m square from (0, 0)

        [Test]
        public void Screen_keys_walk_the_world_direction_at_every_camera_step()
        {
            // World direction index clockwise from +Y and the world keys MovementSim gets for it.
            var world = new[]
            {
                MoveKeys.W, MoveKeys.W | MoveKeys.D, MoveKeys.D, MoveKeys.S | MoveKeys.D,
                MoveKeys.S, MoveKeys.S | MoveKeys.A, MoveKeys.A, MoveKeys.W | MoveKeys.A,
            };
            for (int step = -8; step <= 8; step++)
            {
                for (int direction = 0; direction < QaNavigation.Directions; direction++)
                {
                    double angle = direction * System.Math.PI / 4;
                    var screen = QaNavigation.ScreenKeysToward(System.Math.Sin(angle) * 500, System.Math.Cos(angle) * 500, step);
                    Assert.That(ScreenRelativeKeys.ToWorld(screen, step), Is.EqualTo(world[direction]), $"step {step} direction {direction}");
                }
            }
        }

        [Test]
        public void Screen_keys_pick_the_nearest_of_eight_directions()
        {
            Assert.That(QaNavigation.ScreenKeysToward(300, 1000, 0), Is.EqualTo(MoveKeys.W)); // 17 degrees: still north
            Assert.That(QaNavigation.ScreenKeysToward(800, 1000, 0), Is.EqualTo(MoveKeys.W | MoveKeys.D)); // 39 degrees
            Assert.That(QaNavigation.ScreenKeysToward(0, 0, 0), Is.EqualTo(MoveKeys.None));
        }

        [Test]
        public void Path_goes_around_a_wall_through_its_gap()
        {
            // A wall across x = 1000..1100 cm with a gap at y = 1500..1700 cm.
            var grid = Grid((x, y) => x >= 1000 && x < 1100 && !(y >= 1500 && y < 1700));
            var from = FixVec2.FromCm(300, 300);
            var to = FixVec2.FromCm(1800, 300);
            Assert.That(QaNavigation.Clear(grid, from, to), Is.False);

            var path = QaNavigation.FindPath(grid, from, to);

            Assert.That(path, Is.Not.Null);
            Assert.That(path![path.Count - 1], Is.EqualTo(to));
            var position = from;
            for (int i = 0; i < 100 && position != to; i++)
            {
                var next = QaNavigation.NextWaypoint(grid, position, path);
                Assert.That(QaNavigation.Clear(grid, position, next), Is.True, $"leg {i} crosses the wall");
                position = next;
            }
            Assert.That(position, Is.EqualTo(to));
        }

        [Test]
        public void No_path_through_a_closed_wall()
        {
            var grid = Grid((x, y) => x >= 1000 && x < 1100);
            Assert.That(QaNavigation.FindPath(grid, FixVec2.FromCm(300, 300), FixVec2.FromCm(1800, 300)), Is.Null);
        }

        [Test]
        public void Wall_finder_picks_the_nearest_wall_and_avoids_monsters()
        {
            // Walls: x >= 1500 (5 m east of the shooter) and y >= 1800 (8 m north).
            var grid = Grid((x, y) => x >= 1500 || y >= 1800);
            var from = FixVec2.FromCm(1000, 1000);

            Assert.That(QaNavigation.TryFindWall(grid, from, 100, 1500, 72, new List<FixVec2>(), 200, out var stop, out int cm), Is.True);
            Assert.That(cm, Is.InRange(450, 500));
            Assert.That(QaNavigation.Cm(stop.X), Is.InRange(1450, 1500));

            // A monster 3 m east blocks the shots near east: the nearest clear one goes north-east (about 7 m).
            var monster = new List<FixVec2> { FixVec2.FromCm(1300, 1000) };
            Assert.That(QaNavigation.TryFindWall(grid, from, 100, 1500, 72, monster, 200, out stop, out cm), Is.True);
            Assert.That(cm, Is.InRange(600, 800));
            Assert.That(QaNavigation.Cm(stop.Y), Is.GreaterThan(1300), "not the shot past the monster");
        }

        [Test]
        public void Wall_finder_fails_in_the_open()
        {
            var grid = Grid((x, y) => false);
            Assert.That(QaNavigation.TryFindWall(grid, FixVec2.FromCm(1000, 1000), 100, 500, 36, new List<FixVec2>(), 200, out _, out _), Is.False);
        }

        private static WorldGrid Grid(System.Func<int, int, bool> wall)
        {
            var flags = new byte[Cells * Cells];
            var regions = new byte[Cells * Cells];
            var heights = new short[Cells * Cells];
            for (int row = 0; row < Cells; row++)
            {
                for (int column = 0; column < Cells; column++)
                {
                    int i = row * Cells + column;
                    flags[i] = wall(column * CellCm, row * CellCm) ? (byte)0 : WorldGridFormat.FlagWalkable;
                    regions[i] = 1;
                }
            }
            return new WorldGrid(0, 0, CellCm, Cells, Cells, 0, flags, regions, heights);
        }
    }
}
