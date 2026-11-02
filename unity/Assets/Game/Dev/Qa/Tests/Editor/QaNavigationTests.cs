#nullable enable
using System.Collections.Generic;
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
        public void Screen_directions_are_unit_ground_directions_turned_by_the_camera_step()
        {
            // Where the QA mouse aims (the keys no longer depend on the camera, R-2.2-01, M3 decision 14).
            for (int step = -8; step <= 8; step++)
            {
                for (int screen = 0; screen < QaNavigation.Directions; screen++)
                {
                    QaNavigation.ScreenDirection(screen, step, out double dx, out double dy);
                    Assert.That(dx * dx + dy * dy, Is.EqualTo(1.0).Within(1e-9));
                    double radians = (screen + step) * System.Math.PI / 4;
                    Assert.That(dx, Is.EqualTo(System.Math.Sin(radians)).Within(1e-9), $"step {step} screen {screen}");
                    Assert.That(dy, Is.EqualTo(System.Math.Cos(radians)).Within(1e-9), $"step {step} screen {screen}");
                }
            }
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

        // Wave 1 QA F5: a 3 x 3 m block (x 10-13 m, y 10-13 m) with a threat circle against its west face, a walk from the
        // south-east to the north-west that drifted into the circle (the threat grid closes its cells, the real one does not).
        private static bool Block(int x, int y) => x >= 1000 && x < 1300 && y >= 1000 && y < 1300;

        private static readonly (FixVec2 At, int RadiusCm)[] Threat = { (FixVec2.FromCm(850, 1150), 200) };

        [Test]
        public void A_walk_that_drifted_into_a_closed_cell_heads_for_the_nearest_path_point_not_back_past_the_block()
        {
            var grid = Grid(Block);
            var safe = QaNavigation.AvoidingThreats(grid, Threat);
            var path = QaNavigation.FindPath(safe, FixVec2.FromCm(1600, 900), FixVec2.FromCm(300, 1800))!;
            var drifted = FixVec2.FromCm(960, 1150);
            Assert.That(QaNavigation.Clear(grid, drifted, path[0]), Is.False, "the path's first point lies past the block");

            var next = QaNavigation.NextWaypoint(safe, drifted, path);

            double nearest = double.MaxValue;
            foreach (var point in path)
                nearest = System.Math.Min(nearest, Distance(drifted, point));
            Assert.That(next, Is.Not.EqualTo(path[0]));
            Assert.That(Distance(drifted, next), Is.EqualTo(nearest).Within(0.01), "the nearest point of the path");
            Assert.That(Distance(drifted, next), Is.LessThan(Distance(drifted, path[0])));
        }

        [Test]
        public void A_W_pressed_dead_into_a_wall_face_turns_to_a_neighbouring_key()
        {
            // R-2.2-01 (M3 decision 14): the mouse on the target, so W runs at it; straight into the west face it would not move,
            // so a key 45 degrees off that still slides (wave 1 QA F5).
            var grid = Grid(Block);
            var atFace = FixVec2.FromCm(999, 1150); // against the west face

            Assert.That(QaNavigation.AimedWalkKeys(grid, atFace, 500, 0, 16_384, 10), Is.EqualTo(MoveKeys.W | MoveKeys.D),
                "east, dead into the face: W+D (south-east), which slides south");
            Assert.That(QaNavigation.AimedWalkKeys(grid, atFace, 500, 150, 13_344, 10), Is.EqualTo(MoveKeys.W),
                "east-north-east: W still slides north");
            Assert.That(QaNavigation.AimedWalkKeys(grid, atFace, 0, 500, 0, 10), Is.EqualTo(MoveKeys.W), "north moves: kept");
            Assert.That(QaNavigation.AimedWalkKeys(grid, FixVec2.FromCm(500, 500), 500, 0, 16_384, 10), Is.EqualTo(MoveKeys.W),
                "in the open: W");
            Assert.That(QaNavigation.AimedWalkKeys(grid, atFace, 0, 0, 0, 10), Is.EqualTo(MoveKeys.None));
        }

        [Test]
        public void The_aimed_walk_reaches_its_goal_through_the_shared_step_with_the_mouse_on_each_waypoint()
        {
            // R-2.2-01 (M3 decision 14): QaScenario.Walk holds the mouse on the waypoint, so W runs at it; stepped through the
            // real MovementSim and the grid, as server and prediction do, from the spot that drifted into the threat circle.
            var grid = Grid(Block);
            var safe = QaNavigation.AvoidingThreats(grid, Threat);
            var goal = FixVec2.FromCm(300, 1800);
            var path = QaNavigation.FindPath(safe, FixVec2.FromCm(1600, 900), goal)!;
            var state = new MovementState(FixVec2.FromCm(960, 1150), 0);
            int ticks = 0;
            for (; ticks < 400 && Distance(state.Position, goal) > 100; ticks++)
            {
                var waypoint = QaNavigation.NextWaypoint(safe, state.Position, path);
                double dx = QaNavigation.Cm(waypoint.X) - QaNavigation.Cm(state.Position.X), dy = QaNavigation.Cm(waypoint.Y) - QaNavigation.Cm(state.Position.Y);
                double turns = System.Math.Atan2(dx, dy) / (2 * System.Math.PI);
                ushort facing = (ushort)((int)System.Math.Round((turns < 0 ? turns + 1 : turns) * 65_536) & 0xFFFF); // the mouse
                var keys = QaNavigation.AimedWalkKeys(grid, state.Position, dx, dy, facing, 10);
                Assert.That(keys, Is.Not.EqualTo(MoveKeys.None));
                var input = MovementInputMapping.FromFrame(new InputFrame { Keys = keys, Facing = facing });
                state = MovementSim.Step(state, input, false, 500, 20, grid); // 25 cm a tick
            }

            Assert.That(Distance(state.Position, goal), Is.LessThanOrEqualTo(100),
                $"stalled at ({QaNavigation.Cm(state.Position.X)}, {QaNavigation.Cm(state.Position.Y)}) after {ticks} ticks");
        }

        [Test]
        public void With_the_mouse_elsewhere_the_aimed_walk_presses_the_nearest_key_offset()
        {
            var grid = Grid((_, _) => false);
            ushort twenty = 3_641; // the mouse 20 degrees clockwise of north
            static double Sin(double d) => System.Math.Sin(d * System.Math.PI / 180) * 500;
            static double Cos(double d) => System.Math.Cos(d * System.Math.PI / 180) * 500;
            var open = FixVec2.FromCm(1000, 1000);
            Assert.That(QaNavigation.AimedWalkKeys(grid, open, Sin(20), Cos(20), twenty, 10), Is.EqualTo(MoveKeys.W), "at the mouse: W");
            Assert.That(QaNavigation.AimedWalkKeys(grid, open, Sin(200), Cos(200), twenty, 10), Is.EqualTo(MoveKeys.S), "away: S");
            Assert.That(QaNavigation.AimedWalkKeys(grid, open, Sin(110), Cos(110), twenty, 10), Is.EqualTo(MoveKeys.D), "right: D");
            Assert.That(QaNavigation.AimedWalkKeys(grid, open, Sin(290), Cos(290), twenty, 10), Is.EqualTo(MoveKeys.A), "left: A");
        }

        private static double Distance(FixVec2 a, FixVec2 b) =>
            QaNavigation.Distance(QaNavigation.Cm(a.X), QaNavigation.Cm(a.Y), QaNavigation.Cm(b.X), QaNavigation.Cm(b.Y));

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

        [Test]
        public void A_back_off_turns_away_from_a_no_entry_region_and_stays_inside_the_leash()
        {
            // QA wave 5 H2: the city (region 2, no entry) fills y < 500; the demon stands north of the player, so straight
            // back (south) would walk into the city and reset the demon.
            var grid = Grid((x, y) => false, (x, y) => y < 500 ? (byte)2 : (byte)1);
            var noEntry = new bool[256];
            noEntry[2] = true;
            var player = FixVec2.FromCm(1000, 900);
            var demon = FixVec2.FromCm(1000, 1100);
            var home = FixVec2.FromCm(1000, 1200);
            Assert.That(QaNavigation.CrossesNoEntry(grid, noEntry, player, FixVec2.FromCm(1000, 100)), Is.True);

            Assert.That(QaNavigation.TryBackOffTarget(grid, noEntry, player, demon, home, 600, 1100, out var target), Is.True);

            Assert.That(QaNavigation.CrossesNoEntry(grid, noEntry, player, target), Is.False);
            Assert.That(QaNavigation.Distance(QaNavigation.Cm(target.X), QaNavigation.Cm(target.Y), 1000, 1200), Is.LessThanOrEqualTo(1100));
            Assert.That(QaNavigation.Distance(QaNavigation.Cm(target.X), QaNavigation.Cm(target.Y), 1000, 1100),
                Is.GreaterThan(QaNavigation.Distance(1000, 900, 1000, 1100)), "it still gets farther from the demon");

            // Without the city, it backs straight away.
            Assert.That(QaNavigation.TryBackOffTarget(grid, new bool[256], player, demon, home, 600, 2000, out var straight), Is.True);
            Assert.That((QaNavigation.Cm(straight.X), QaNavigation.Cm(straight.Y)), Is.EqualTo((1000.0, 300.0)));
        }

        [Test]
        public void A_back_off_ends_outside_other_monsters_aggro_circles()
        {
            // Smoke fix5c-4: legs straight away from the demon kited the fight into the second demon's aggro.
            var grid = Grid((x, y) => false);
            var player = FixVec2.FromCm(1000, 900);
            var demon = FixVec2.FromCm(1000, 1100);
            var other = (FixVec2.FromCm(1000, 300), 400); // right behind the player
            Assert.That(QaNavigation.TryBackOffTarget(grid, new bool[256], player, demon, demon, 600, 2000, out var target,
                new[] { other }), Is.True);
            Assert.That(QaNavigation.Distance(QaNavigation.Cm(target.X), QaNavigation.Cm(target.Y), 1000, 300), Is.GreaterThanOrEqualTo(400));
        }

        [Test]
        public void No_back_off_when_every_direction_is_blocked()
        {
            var grid = Grid((x, y) => false, (x, y) => 2);
            var noEntry = new bool[256];
            noEntry[2] = true;
            Assert.That(QaNavigation.TryBackOffTarget(grid, noEntry, FixVec2.FromCm(1000, 1000), FixVec2.FromCm(1000, 1100),
                FixVec2.FromCm(1000, 1100), 500, 2000, out _), Is.False);
        }

        private static WorldGrid Grid(System.Func<int, int, bool> wall) => Grid(wall, (x, y) => 1);

        private static WorldGrid Grid(System.Func<int, int, bool> wall, System.Func<int, int, byte> region)
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
                    regions[i] = region(column * CellCm, row * CellCm);
                }
            }
            return new WorldGrid(0, 0, CellCm, Cells, Cells, 0, flags, regions, heights);
        }
    }
}
