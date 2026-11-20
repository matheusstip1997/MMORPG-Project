#nullable enable
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Shared.Simulation;
using Shared.World;

namespace Game.Dev.Qa.EditorTests
{
    /// <summary>
    /// QA M4 wave 2 F1: the QA walk stuck on the wall corners of the gateway between the city and m1.combat (walls at x
    /// -1050..-950, a 400 cm gap at y -200..200: six open cells, y -150..150, on the committed grid). The old steering aimed at
    /// the farthest path point in a straight line, the goal itself from the corner (-900, -150); off its path only points behind
    /// were in sight, so it walked back to the corner and re-planned the same path until its time ran out (reproduced on the
    /// committed grid from every start south-east of that corner before the fix). <see cref="QaWalk"/> is played tick by tick
    /// through <see cref="MovementSim"/> at the M1 walk speed, as the run's walks steer it.
    /// </summary>
    public sealed class QaGatewayWalkTests
    {
        private const int SpeedCmPerSec = 400; // StartingValuesM1.moveSpeedCmPerSec
        private const int TickHz = 20;

        private static WorldGrid Grid()
        {
            Assert.That(Game.Gameplay.Streaming.ClientWorld.TryLoadGrid("m1", out var grid, out var error) && grid != null, Is.True, error);
            return grid!;
        }

        /// <summary>Walks with <see cref="QaWalk"/>; <paramref name="rounded"/> steers from the position rounded to whole cm.</summary>
        private static FixVec2 Walk(WorldGrid grid, FixVec2 start, FixVec2 goal, int maxTicks, bool rounded, out QaWalk walk, out int ticks)
        {
            var state = new MovementState(start, 0);
            walk = new QaWalk(grid, goal);
            for (ticks = 0; ticks < maxTicks; ticks++)
            {
                if (Distance(state.Position, goal) <= QaTimeouts.WaypointReachedCm)
                    break;
                var steer = rounded
                    ? FixVec2.FromCm((int)System.Math.Round(QaNavigation.Cm(state.Position.X)), (int)System.Math.Round(QaNavigation.Cm(state.Position.Y)))
                    : state.Position;
                int direction = walk.Step(steer, ticks / (double)TickHz);
                Assert.That(direction, Is.GreaterThanOrEqualTo(0), "a path from " + Describe(state.Position));
                state = MovementSim.Step(state, new MovementInput(MoveSteering.KeysFor(direction), 0), false, SpeedCmPerSec, TickHz, grid);
            }
            return state.Position;
        }

        /// <summary>Spots around a logged stuck point, with sub-cm offsets (the snapshot shows whole cm).</summary>
        private static IEnumerable<FixVec2> Around(int xCm, int yCm)
        {
            for (int dx = -60; dx <= 60; dx += 20)
            {
                for (int dy = -60; dy <= 60; dy += 20)
                {
                    foreach (int fraction in new[] { 0, 1, 64, 128, 255 })
                        yield return new FixVec2(FixedPoint.FromCm(xCm + dx) + fraction, FixedPoint.FromCm(yCm + dy) - fraction);
                }
            }
        }

        private static void WalkAll(IEnumerable<FixVec2> starts, FixVec2 goal, int maxTicks)
        {
            var grid = Grid();
            var failures = new List<string>();
            int walks = 0;
            foreach (var start in starts.Where(grid.IsWalkable))
            {
                foreach (bool rounded in new[] { false, true })
                {
                    walks++;
                    var end = Walk(grid, start, goal, maxTicks, rounded, out var walk, out int ticks);
                    if (Distance(end, goal) > QaTimeouts.WaypointReachedCm)
                        failures.Add($"from {Describe(start)}{(rounded ? " (rounded)" : "")} stopped at {Describe(end)} after {ticks} ticks, " +
                                     $"{walk.Replans} re-plans, {walk.Unsticks} step-aways");
                }
            }
            Assert.That(walks, Is.GreaterThan(0));
            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)) + $"\n{failures.Count} of {walks} failed");
        }

        [Test]
        public void The_walk_home_passes_the_gateway_from_its_south_corner()
        {
            // Archer B: "walk home: no progress at (-900, -150)" 33 times, then "did not get back to m1.spawn.city in 120 s".
            WalkAll(Around(-900, -150), FixVec2.FromCm(-3000, 0), 60 * TickHz);
        }

        [Test]
        public void The_walk_to_camp_a_passes_the_gateway_from_its_north_corner()
        {
            // Warrior B, walk-to-demon: "stuck at (-1100, 150) for 3 s" on the way from the mailbox.
            WalkAll(Around(-1100, 150), FixVec2.FromCm(200, 400), 60 * TickHz);
        }

        [Test]
        public void The_whole_walks_of_the_failed_runs_arrive()
        {
            // Archer B logged in at (2080, -6480) and walked home; warrior B walked from the mailbox (-2800, 600) to camp a.
            WalkAll(new[] { FixVec2.FromCm(2080, -6480) }, FixVec2.FromCm(-3000, 0), 120 * TickHz);
            WalkAll(new[] { FixVec2.FromCm(-2800, 600) }, FixVec2.FromCm(200, 400), 120 * TickHz);
            WalkAll(new[] { FixVec2.FromCm(200, 400) }, FixVec2.FromCm(-2800, 600), 120 * TickHz);
        }

        [Test]
        public void The_path_through_the_gateway_keeps_a_cell_from_its_walls()
        {
            var grid = Grid();
            var path = QaNavigation.FindPath(grid, FixVec2.FromCm(-500, -100), FixVec2.FromCm(-1500, -100))!;
            var inGap = path.Where(p => QaNavigation.Cm(p.X) > -1100 && QaNavigation.Cm(p.X) < -900).ToList();
            Assert.That(inGap, Is.Not.Empty);
            Assert.That(inGap.All(p => QaNavigation.Cm(p.Y) > -100 && QaNavigation.Cm(p.Y) < 100), Is.True,
                "through the middle rows: " + string.Join(" ", inGap.Select(Describe)));
        }

        [Test]
        public void A_walk_never_turns_back_through_the_gap_it_passed()
        {
            // A wall across x = 1000..1100 cm with a gap at y = 1500..1700 cm: from the gap the goal is in a straight line; the walk
            // reaches it without crossing back west of the wall.
            var grid = QaNavigationTests.Grid((x, y) => x >= 1000 && x < 1100 && !(y >= 1500 && y < 1700));
            var goal = FixVec2.FromCm(1800, 300);
            var walk = new QaWalk(grid, goal);
            var position = FixVec2.FromCm(300, 1600);
            for (int tick = 0; tick < 400 && Distance(position, goal) > QaTimeouts.WaypointReachedCm; tick++)
            {
                int direction = walk.Step(position, tick / (double)TickHz);
                Assert.That(direction, Is.GreaterThanOrEqualTo(0));
                var before = position;
                position = MovementSim.Step(new MovementState(position, 0), new MovementInput(MoveSteering.KeysFor(direction), 0), false,
                    SpeedCmPerSec, TickHz, grid).Position;
                if (QaNavigation.Cm(before.X) >= 1100)
                    Assert.That(QaNavigation.Cm(position.X), Is.GreaterThanOrEqualTo(1100), "back through the gap at tick " + tick);
            }
            Assert.That(Distance(position, goal), Is.LessThanOrEqualTo(QaTimeouts.WaypointReachedCm), "stopped at " + Describe(position));
        }

        [Test]
        public void The_respawn_wait_watches_the_camp_of_the_demon_that_died()
        {
            // QA M4 wave 2 F4 (mage C): lock-demon killed camp a's demon at (-409, 207), kill-demon then camp b's at (1598, 197);
            // wait-respawn watched camp a with camp b's death tick.
            Assert.That(Game.Gameplay.Entities.ClientConfig.TryLoad(out var config, out var error), Is.True, error);
            var markers = QaMarkers.Load(Game.Gameplay.Streaming.ClientWorld.Directory("m1"));
            Assert.That(config.TryGet(typeof(Shared.Data.Tables.SpawnPoints), out var table), Is.True);
            var points = table.Rows.Cast<Shared.Data.Tables.SpawnPoints>().ToList();
            Assert.That(QaMarkers.CampOf(markers, points, "m1.demon", -409, 207)?.id, Is.EqualTo("m1.spawn.combat.a"));
            Assert.That(QaMarkers.CampOf(markers, points, "m1.demon", 1598, 197)?.id, Is.EqualTo("m1.spawn.combat.b"));
            Assert.That(QaMarkers.CampOf(markers, points, "m1.alien", 1598, 197)?.id, Is.Not.EqualTo("m1.spawn.combat.b"), "only that monster's camps");
            Assert.That(QaMarkers.CampOf(markers, points, "no.such.monster", 0, 0), Is.Null);
        }

        private static string Describe(FixVec2 p) => $"({QaNavigation.Cm(p.X):0.00}, {QaNavigation.Cm(p.Y):0.00})";

        private static double Distance(FixVec2 a, FixVec2 b) =>
            QaNavigation.Distance(QaNavigation.Cm(a.X), QaNavigation.Cm(a.Y), QaNavigation.Cm(b.X), QaNavigation.Cm(b.Y));
    }
}
