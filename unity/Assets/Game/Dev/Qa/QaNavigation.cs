#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.World;
using MoveKeys = Shared.Protocol.MoveKeys;
using UnityEngine;

namespace Game.Dev.Qa
{
    /// <summary>
    /// How the QA run moves and aims like a player: which screen-relative WASD keys walk toward a point, a walkable path
    /// over the world grid, and a wall to shoot at. Pure functions over the shared world grid (no Unity scene state), so
    /// they have EditMode tests. Ground positions are in cm (+Y is W at camera yaw 0, as in <c>MovementSim</c>).
    /// </summary>
    public static class QaNavigation
    {
        public const int Directions = MoveSteering.Directions;

        /// <summary>
        /// The screen-relative keys a player holds to walk along the ground direction (<paramref name="dxCm"/>,
        /// <paramref name="dyCm"/>) with the camera at <paramref name="cameraStep"/> (45-degree steps clockwise, see
        /// <c>CameraYaw.Step</c>): the nearest of the 8 directions. None for no direction.
        /// </summary>
        public static MoveKeys ScreenKeysToward(double dxCm, double dyCm, int cameraStep)
        {
            if (dxCm * dxCm + dyCm * dyCm < 1e-6)
                return MoveKeys.None;
            double degrees = Math.Atan2(dxCm, dyCm) * 180.0 / Math.PI; // clockwise from +Y
            int world = (int)Math.Round(degrees / (360.0 / Directions));
            // Screen direction index clockwise from screen-up: the simulation's table (MoveSteering: 0 W, 1 W+D, ... 7 W+A).
            return (MoveKeys)(byte)MoveSteering.KeysFor(world - cameraStep);
        }

        /// <summary>
        /// The unit ground direction (cm axes, +Y is W at camera yaw 0) that the screen direction
        /// <paramref name="screenIndex"/> (clockwise from screen-up in 45-degree steps: 0 W, 2 D, 4 S, 6 A) walks along with
        /// the camera at <paramref name="cameraStep"/>; the inverse of <see cref="ScreenKeysToward"/>.
        /// </summary>
        public static void ScreenDirection(int screenIndex, int cameraStep, out double dx, out double dy)
        {
            double radians = (screenIndex + cameraStep) * (2.0 * Math.PI / Directions);
            dx = Math.Sin(radians);
            dy = Math.Cos(radians);
        }

        /// <summary>True when a straight walk from <paramref name="from"/> to <paramref name="to"/> stays on walkable cells.</summary>
        public static bool Clear(WorldGrid grid, FixVec2 from, FixVec2 to) => !GridRay.Cast(grid, from, to, out _);

        /// <summary>The cost of a straight step between cell centers in <see cref="FindPath"/> (a diagonal costs <see cref="DiagonalCost"/>).</summary>
        public const int StraightCost = 10;

        public const int DiagonalCost = 14;

        /// <summary>
        /// QA M4 wave 2 F1: the extra cost of entering a cell beside a closed one, so a path runs through the middle of a passage
        /// with room (the city gateway is six cells wide) instead of hugging the wall and turning round its corner cell.
        /// </summary>
        public const int BesideWallCost = 20;

        private static int[] _previous = Array.Empty<int>();
        private static int[] _cost = Array.Empty<int>();

        /// <summary>
        /// A walkable path of cell centers from <paramref name="from"/> to <paramref name="to"/> (the cheapest over the 8
        /// neighbours, no corner cutting, cells beside a closed cell cost <see cref="BesideWallCost"/> more), ending exactly at
        /// <paramref name="to"/>; null when there is none.
        /// </summary>
        public static List<FixVec2>? FindPath(WorldGrid grid, FixVec2 from, FixVec2 to)
        {
            int startColumn = grid.ColumnOf(from.X), startRow = grid.RowOf(from.Y);
            int endColumn = grid.ColumnOf(to.X), endRow = grid.RowOf(to.Y);
            if (!grid.InBounds(startColumn, startRow) || !grid.InBounds(endColumn, endRow))
                return null;
            int start = startRow * grid.Width + startColumn;
            int end = endRow * grid.Width + endColumn;
            // One pair of arrays reused by every search on the main thread (a walk re-plans as the demon it follows moves).
            if (_previous.Length != grid.CellCount)
            {
                _previous = new int[grid.CellCount];
                _cost = new int[grid.CellCount];
            }
            var previous = _previous;
            var cost = _cost;
            for (int i = 0; i < previous.Length; i++)
            {
                previous[i] = -1;
                cost[i] = int.MaxValue;
            }
            previous[start] = start;
            cost[start] = 0;
            var open = new CellHeap();
            open.Push(0, start);
            while (open.TryPop(out int at, out int cell))
            {
                if (at > cost[cell])
                    continue; // a stale entry
                if (cell == end)
                    break;
                int column = cell % grid.Width, row = cell / grid.Width;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        int c = column + dx, r = row + dy;
                        if (!grid.InBounds(c, r))
                            continue;
                        int next = r * grid.Width + c;
                        if (!grid.IsWalkableCell(c, r) && next != end)
                            continue;
                        if (dx != 0 && dy != 0 && (!grid.IsWalkableCell(column + dx, row) || !grid.IsWalkableCell(column, row + dy)))
                            continue;
                        int step = (dx != 0 && dy != 0 ? DiagonalCost : StraightCost) + (BesideWall(grid, c, r) ? BesideWallCost : 0);
                        int total = at + step;
                        if (total >= cost[next])
                            continue;
                        cost[next] = total;
                        previous[next] = cell;
                        open.Push(total, next);
                    }
                }
            }
            if (previous[end] < 0)
                return null;
            var path = new List<FixVec2> { to };
            for (int cell = previous[end]; cell != start; cell = previous[cell])
                path.Add(CellCenter(grid, cell));
            path.Reverse();
            return path;
        }


        /// <summary>
        /// The walkable cell centre nearest <paramref name="at"/> on its own grid row, searching outward column by column (the
        /// west one first at an equal distance); <paramref name="at"/> itself when its cell is walkable, null when the row has none.
        /// QA M4 wave 2: parry-boss walks north to a lane from where the player stands, which was inside a wall when the step began
        /// in the city gateway.
        /// </summary>
        public static FixVec2? NearestWalkableOnRow(WorldGrid grid, FixVec2 at)
        {
            int column = grid.ColumnOf(at.X), row = grid.RowOf(at.Y);
            if (grid.IsWalkableCell(column, row))
                return at;
            for (int step = 1; step < grid.Width; step++)
            {
                foreach (int c in new[] { column - step, column + step })
                {
                    if (grid.IsWalkableCell(c, row))
                        return CellCenter(grid, row * grid.Width + c);
                }
            }
            return null;
        }
        /// <summary>True when one of the 8 neighbours of the cell is closed (or off the grid).</summary>
        private static bool BesideWall(WorldGrid grid, int column, int row)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx != 0 || dy != 0) && !grid.IsWalkableCell(column + dx, row + dy))
                        return true;
                }
            }
            return false;
        }

        /// <summary>A binary min-heap of (cost, cell) for <see cref="FindPath"/> (netstandard2.1 has no PriorityQueue).</summary>
        private sealed class CellHeap
        {
            private readonly List<(int Cost, int Cell)> _items = new List<(int, int)>();

            public void Push(int cost, int cell)
            {
                _items.Add((cost, cell));
                int i = _items.Count - 1;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_items[parent].Cost <= _items[i].Cost)
                        break;
                    (_items[parent], _items[i]) = (_items[i], _items[parent]);
                    i = parent;
                }
            }

            public bool TryPop(out int cost, out int cell)
            {
                if (_items.Count == 0)
                {
                    cost = cell = 0;
                    return false;
                }
                (cost, cell) = _items[0];
                int last = _items.Count - 1;
                _items[0] = _items[last];
                _items.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int left = 2 * i + 1, right = left + 1, smallest = i;
                    if (left < _items.Count && _items[left].Cost < _items[smallest].Cost)
                        smallest = left;
                    if (right < _items.Count && _items[right].Cost < _items[smallest].Cost)
                        smallest = right;
                    if (smallest == i)
                        break;
                    (_items[smallest], _items[i]) = (_items[i], _items[smallest]);
                    i = smallest;
                }
                return true;
            }
        }

        /// <summary>
        /// The farthest point of <paramref name="path"/> walkable in a straight line from <paramref name="position"/>. When none
        /// is (the walk drifted into a cell <paramref name="grid"/> closes, such as a threat circle of
        /// <see cref="AvoidingThreats"/>), the nearest point of the path, so the walk goes back onto it: the path's first point
        /// can lie far behind, past a block the walk then pushes into (wave 1 QA F5).
        /// </summary>
        public static FixVec2 NextWaypoint(WorldGrid grid, FixVec2 position, IReadOnlyList<FixVec2> path)
        {
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (Clear(grid, position, path[i]))
                    return path[i];
            }
            var nearest = path[0];
            double best = double.MaxValue;
            foreach (var point in path)
            {
                double cm = Distance(Cm(position.X), Cm(position.Y), Cm(point.X), Cm(point.Y));
                if (cm < best)
                {
                    best = cm;
                    nearest = point;
                }
            }
            return nearest;
        }

        /// <summary>
        /// The point of <paramref name="path"/> after the one nearest <paramref name="position"/> (the last when that is the last).
        /// <see cref="NextWaypoint"/> looks along a centre line and so can cut a wall's corner the body then catches on (fixer
        /// round 3: a level-12 run stood 60 s at a corner of the city exit, re-planning the same cut); cell by cell the path keeps
        /// clear of the corner.
        /// </summary>
        public static FixVec2 FollowingPoint(FixVec2 position, IReadOnlyList<FixVec2> path)
        {
            int nearest = 0;
            double best = double.MaxValue;
            for (int i = 0; i < path.Count; i++)
            {
                double cm = Distance(Cm(position.X), Cm(position.Y), Cm(path[i].X), Cm(path[i].Y));
                if (cm < best)
                {
                    best = cm;
                    nearest = i;
                }
            }
            return path[Math.Min(nearest + 1, path.Count - 1)];
        }

        /// <summary>
        /// The world direction (0..7 clockwise from +Y, <see cref="ScreenDirection"/> at camera step 0) the keys walk toward
        /// (<paramref name="dxCm"/>, <paramref name="dyCm"/>) from <paramref name="position"/>: the nearest of the 8, unless a
        /// <paramref name="probeCm"/> step along it moves less than a centimetre on <paramref name="grid"/> (a straight key
        /// pressed into a wall face: <see cref="GridCollision"/> slides only a diagonal along it), then the neighbouring direction
        /// on the target's side, or the other one, whichever still moves. -1 for no direction.
        /// </summary>
        public static int WalkDirection(WorldGrid grid, FixVec2 position, double dxCm, double dyCm, int probeCm)
        {
            if (dxCm * dxCm + dyCm * dyCm < 1e-6)
                return -1;
            double sector = Math.Atan2(dxCm, dyCm) * 180.0 / Math.PI / (360.0 / Directions);
            int nearest = (((int)Math.Round(sector) % Directions) + Directions) % Directions;
            if (Moves(grid, position, nearest, probeCm))
                return nearest;
            int side = sector - Math.Round(sector) >= 0 ? 1 : -1;
            foreach (int turn in new[] { side, -side })
            {
                int direction = (nearest + turn + Directions) % Directions;
                if (Moves(grid, position, direction, probeCm))
                    return direction;
            }
            return nearest;
        }

        private static bool Moves(WorldGrid grid, FixVec2 position, int direction, int probeCm)
        {
            ScreenDirection(direction, 0, out double dx, out double dy);
            var to = FixVec2.FromCm((int)Math.Round(Cm(position.X) + dx * probeCm), (int)Math.Round(Cm(position.Y) + dy * probeCm));
            var end = GridCollision.Move(grid, position, to);
            return Distance(Cm(position.X), Cm(position.Y), Cm(end.X), Cm(end.Y)) >= 1.0;
        }

        /// <summary>
        /// The nearest wall a straight shot from <paramref name="from"/> reaches within <paramref name="maxCm"/>, trying
        /// <paramref name="rays"/> directions and skipping shots that pass within <paramref name="clearanceCm"/> of any
        /// point in <paramref name="avoid"/> (monsters) or stop closer than <paramref name="minCm"/>. <paramref name="stop"/>
        /// is where the shot meets the wall (<see cref="GridRay"/>, as the projectile itself).
        /// </summary>
        public static bool TryFindWall(WorldGrid grid, FixVec2 from, int minCm, int maxCm, int rays,
            IReadOnlyList<FixVec2> avoid, int clearanceCm, out FixVec2 stop, out int distanceCm)
        {
            stop = from;
            distanceCm = int.MaxValue;
            double fromX = Cm(from.X), fromY = Cm(from.Y);
            for (int i = 0; i < rays; i++)
            {
                double angle = 2.0 * Math.PI * i / rays;
                var to = FixVec2.FromCm((int)Math.Round(fromX + Math.Sin(angle) * maxCm), (int)Math.Round(fromY + Math.Cos(angle) * maxCm));
                if (!GridRay.Cast(grid, from, to, out var hit))
                    continue;
                double hitX = Cm(hit.X), hitY = Cm(hit.Y);
                int distance = (int)Math.Round(Distance(fromX, fromY, hitX, hitY));
                if (distance < minCm || distance >= distanceCm || PassesNear(fromX, fromY, hitX, hitY, avoid, clearanceCm))
                    continue;
                stop = hit;
                distanceCm = distance;
            }
            return distanceCm != int.MaxValue;
        }

        /// <summary>
        /// Where a back-off walks to (QA wave 5 H2): <paramref name="distanceCm"/> from <paramref name="from"/>, as directly
        /// away from <paramref name="threat"/> as the world allows. The straight walk there stays on walkable cells and out
        /// of no-entry regions (a monster drops a target that stands in one and walks home to full HP, spec 3.1 "Chase and
        /// attack"), and its end stays within <paramref name="maxFromHomeCm"/> of the monster's <paramref name="home"/> (its
        /// leash) and outside every <paramref name="avoid"/> circle (other monsters' aggro around their spawn markers).
        /// Directions turn away from straight-back by <see cref="BackOffTurnDegrees"/> steps, both sides; false when none fits.
        /// </summary>
        public static bool TryBackOffTarget(WorldGrid grid, bool[] noEntry, FixVec2 from, FixVec2 threat, FixVec2 home,
            int distanceCm, int maxFromHomeCm, out FixVec2 target, IReadOnlyList<(FixVec2 At, int RadiusCm)>? avoid = null)
        {
            target = from;
            double fromX = Cm(from.X), fromY = Cm(from.Y);
            double away = Math.Atan2(fromX - Cm(threat.X), fromY - Cm(threat.Y)); // clockwise from +Y
            for (int turn = 0; turn <= 180; turn += BackOffTurnDegrees)
            {
                for (int side = 1; side >= -1; side -= 2)
                {
                    if (turn == 0 && side < 0 || turn == 180 && side < 0)
                        continue;
                    double angle = away + side * turn * Math.PI / 180.0;
                    var to = FixVec2.FromCm((int)Math.Round(fromX + Math.Sin(angle) * distanceCm),
                        (int)Math.Round(fromY + Math.Cos(angle) * distanceCm));
                    if (Distance(Cm(to.X), Cm(to.Y), Cm(home.X), Cm(home.Y)) > maxFromHomeCm)
                        continue;
                    if (!Clear(grid, from, to) || CrossesNoEntry(grid, noEntry, from, to) || InsideAny(to, avoid))
                        continue;
                    target = to;
                    return true;
                }
            }
            return false;
        }

        private static bool InsideAny(FixVec2 point, IReadOnlyList<(FixVec2 At, int RadiusCm)>? circles)
        {
            if (circles == null)
                return false;
            foreach (var (at, radius) in circles)
            {
                if (Distance(Cm(point.X), Cm(point.Y), Cm(at.X), Cm(at.Y)) < radius)
                    return true;
            }
            return false;
        }

        /// <summary>The step between the directions a back-off tries.</summary>
        public const int BackOffTurnDegrees = 15;

        /// <summary>True when a straight walk from <paramref name="from"/> to <paramref name="to"/> touches a no-entry region.</summary>
        public static bool CrossesNoEntry(WorldGrid grid, bool[] noEntry, FixVec2 from, FixVec2 to)
        {
            double fromX = Cm(from.X), fromY = Cm(from.Y), toX = Cm(to.X), toY = Cm(to.Y);
            double length = Distance(fromX, fromY, toX, toY);
            int samples = Math.Max(1, (int)Math.Ceiling(length / (grid.CellSizeCm / 2.0)));
            for (int i = 0; i <= samples; i++)
            {
                double t = (double)i / samples;
                var point = FixVec2.FromCm((int)Math.Round(fromX + (toX - fromX) * t), (int)Math.Round(fromY + (toY - fromY) * t));
                if (noEntry[grid.RegionIndexAt(point)])
                    return true;
            }
            return false;
        }

        public static double Cm(int raw) => raw / (double)FixedPoint.FromCm(1);

        public static double Distance(double ax, double ay, double bx, double by) =>
            Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

        private static bool PassesNear(double ax, double ay, double bx, double by, IReadOnlyList<FixVec2> points, int clearanceCm)
        {
            double lengthSquared = (bx - ax) * (bx - ax) + (by - ay) * (by - ay);
            foreach (var point in points)
            {
                double px = Cm(point.X), py = Cm(point.Y);
                double t = lengthSquared <= 0 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * (bx - ax) + (py - ay) * (by - ay)) / lengthSquared));
                if (Distance(px, py, ax + t * (bx - ax), ay + t * (by - ay)) < clearanceCm)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The crossings of the level gates (R-2.3-06, R-12.3-01) in the middle of a pass: a walkable cell of an ungated
        /// region with <paramref name="depthCells"/> walkable gated cells straight ahead of it and as many ungated ones behind,
        /// two free cells to each side. <c>Before</c> is the cell <paramref name="depthCells"/> - 1 behind the boundary,
        /// <c>Beyond</c> the cell <paramref name="depthCells"/> past it.
        /// </summary>
        public static List<(FixVec2 Before, FixVec2 Beyond)> GateCrossings(WorldGrid grid, RegionGates gates, int depthCells)
        {
            var crossings = new List<(FixVec2, FixVec2)>();
            var steps = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
            for (int row = 0; row < grid.Height; row++)
            {
                for (int column = 0; column < grid.Width; column++)
                {
                    if (!Open(grid, gates, column, row, gated: false))
                        continue;
                    foreach (var (dc, dr) in steps)
                    {
                        if (!Open(grid, gates, column + dc, row + dr, gated: true))
                            continue;
                        bool middle = true;
                        for (int k = -depthCells + 1; k <= depthCells && middle; k++)
                        {
                            for (int side = -2; side <= 2 && middle; side++)
                                middle = Open(grid, gates, column + k * dc + side * dr, row + k * dr + side * dc, gated: k > 0);
                        }
                        if (middle)
                            crossings.Add((CellCenter(grid, (row - (depthCells - 1) * dr) * grid.Width + column - (depthCells - 1) * dc),
                                CellCenter(grid, (row + depthCells * dr) * grid.Width + column + depthCells * dc)));
                    }
                }
            }
            return crossings;
        }

        /// <summary>
        /// A copy of <paramref name="grid"/> where every cell within a threat's radius is not walkable: the QA run's paths around
        /// monster aggro (the tutorial-gate step walks a level-1 character past the zone's camps).
        /// </summary>
        public static WorldGrid AvoidingThreats(WorldGrid grid, IEnumerable<(FixVec2 At, int RadiusCm)> threats)
        {
            var flags = new byte[grid.CellCount];
            var regions = new byte[grid.CellCount];
            for (int row = 0; row < grid.Height; row++)
            {
                for (int column = 0; column < grid.Width; column++)
                {
                    int cell = row * grid.Width + column;
                    flags[cell] = grid.IsWalkableCell(column, row) ? WorldGridFormat.FlagWalkable : (byte)0;
                    regions[cell] = grid.RegionIndexOfCell(column, row);
                }
            }
            foreach (var (at, radiusCm) in threats)
            {
                int reach = radiusCm / grid.CellSizeCm + 1;
                int centerColumn = grid.ColumnOf(at.X), centerRow = grid.RowOf(at.Y);
                for (int row = centerRow - reach; row <= centerRow + reach; row++)
                {
                    for (int column = centerColumn - reach; column <= centerColumn + reach; column++)
                    {
                        if (!grid.InBounds(column, row))
                            continue;
                        var center = CellCenter(grid, row * grid.Width + column);
                        if (Distance(Cm(center.X), Cm(center.Y), Cm(at.X), Cm(at.Y)) <= radiusCm)
                            flags[row * grid.Width + column] = 0;
                    }
                }
            }
            return new WorldGrid(grid.OriginXCm, grid.OriginYCm, grid.CellSizeCm, grid.Width, grid.Height, grid.BaseHeightCm,
                flags, regions, new short[grid.CellCount]);
        }

        /// <summary>
        /// QA M4 wave 1 F1: where the loot bag steps die so a bag drops (R-4.3-03): the walkable cell nearest the line from
        /// <paramref name="camp"/> toward <paramref name="toward"/> (the city, where the player comes from), at least
        /// <paramref name="clearCm"/> from the camp, in a region whose <c>deathDrop</c> is on (<paramref name="deathDrop"/> by region
        /// index) and outside every threat circle. It searches outward ring by ring (one cell apart, up to
        /// <paramref name="searchCm"/> beyond <paramref name="clearCm"/>), each ring in 16 directions, the direction toward the city
        /// first. False when no such point exists.
        /// </summary>
        public static bool TryDeathDropSpot(WorldGrid grid, bool[] deathDrop, FixVec2 camp, int clearCm, FixVec2 toward,
            IReadOnlyList<(FixVec2 At, int RadiusCm)> threats, int searchCm, out FixVec2 spot)
        {
            const int Rays = 16;
            double baseAngle = Math.Atan2(Cm(toward.Y - camp.Y), Cm(toward.X - camp.X));
            for (int distance = clearCm; distance <= clearCm + searchCm; distance += grid.CellSizeCm)
            {
                for (int ray = 0; ray < Rays; ray++)
                {
                    // 0, +1, -1, +2, -2, ...: the line toward the city first, then turning away from it on both sides.
                    int turn = (ray + 1) / 2 * (ray % 2 == 1 ? 1 : -1);
                    double angle = baseAngle + turn * 2 * Math.PI / Rays;
                    var at = camp + FixVec2.FromCm((int)Math.Round(distance * Math.Cos(angle)), (int)Math.Round(distance * Math.Sin(angle)));
                    int column = grid.ColumnOf(at.X), row = grid.RowOf(at.Y);
                    if (!grid.InBounds(column, row) || !grid.IsWalkableCell(column, row))
                        continue;
                    int region = grid.RegionIndexOfCell(column, row);
                    if (region >= deathDrop.Length || !deathDrop[region])
                        continue;
                    bool threatened = false;
                    foreach (var (threat, radiusCm) in threats)
                        threatened |= Distance(Cm(at.X), Cm(at.Y), Cm(threat.X), Cm(threat.Y)) <= radiusCm;
                    if (threatened)
                        continue;
                    spot = at;
                    return true;
                }
            }
            spot = camp;
            return false;
        }

        private static bool Open(WorldGrid grid, RegionGates gates, int column, int row, bool gated) =>
            grid.IsWalkableCell(column, row) && (gates.MinLevelOf(grid.RegionIndexOfCell(column, row)) > 0) == gated;

        private static FixVec2 CellCenter(WorldGrid grid, int cell)
        {
            int column = cell % grid.Width, row = cell / grid.Width;
            int half = FixedPoint.FromCm(grid.CellSizeCm) / 2;
            return new FixVec2(grid.ColumnStartRaw(column) + half, grid.RowStartRaw(row) + half);
        }
    }

    /// <summary>A marker of an exported world (<c>markers.json</c>, see <see cref="WorldFiles"/>).</summary>
    [Serializable]
    public sealed class QaMarker
    {
        public string id = "";
        public string kind = "";
        public int xCm;
        public int yCm;
        public int facing;
    }

    /// <summary>The markers of an exported world, read with the same file names the server uses.</summary>
    public static class QaMarkers
    {
        [Serializable]
        private sealed class File
        {
            public QaMarker[] markers = Array.Empty<QaMarker>();
        }

        public static IReadOnlyList<QaMarker> Load(string worldDirectory) =>
            JsonUtility.FromJson<File>(System.IO.File.ReadAllText(Path.Combine(worldDirectory, WorldFiles.Markers))).markers;

        /// <summary>
        /// QA M4 wave 2 F4: the camp a monster of <paramref name="monsterId"/> belongs to, from where it was last seen (it died
        /// there; a monster never leaves its leash around its spawn marker): the nearest marker of the <paramref name="points"/>
        /// that spawn that monster. Null when none does.
        /// </summary>
        public static QaMarker? CampOf(IReadOnlyList<QaMarker> markers, IEnumerable<SpawnPoints> points, string monsterId, int xCm, int yCm)
        {
            QaMarker? best = null;
            double bestCm = double.MaxValue;
            foreach (var point in points)
            {
                if (point.MonsterId != monsterId)
                    continue;
                var marker = markers.FirstOrDefault(m => m.id == point.MarkerId);
                if (marker == null)
                    continue;
                double cm = QaNavigation.Distance(marker.xCm, marker.yCm, xCm, yCm);
                if (cm < bestCm)
                {
                    best = marker;
                    bestCm = cm;
                }
            }
            return best;
        }

        /// <summary>
        /// QA M4 wave 2 (mage B): the tick of the newest death of a <paramref name="monsterId"/> (net id <paramref name="defId"/>) of the
        /// camp <paramref name="camp"/>: a despawn at HP bucket 0 (a despawn alive is the monster leaving the client's interest) whose
        /// spot belongs to that camp (<see cref="CampOf"/>). Null when none was seen.
        /// </summary>
        public static uint? LastDeathAtCamp(IEnumerable<QaEntityEvent> events, ushort defId, IReadOnlyList<QaMarker> markers,
            IEnumerable<SpawnPoints> points, string monsterId, QaMarker camp)
        {
            var spawns = points.ToList();
            uint? last = null;
            foreach (var e in events)
            {
                if (e.Spawned || e.Kind != Shared.Protocol.EntityKind.Monster || e.DefId != defId || e.HpBucket != 0)
                    continue;
                if (CampOf(markers, spawns, monsterId, e.XCm, e.YCm)?.id != camp.id)
                    continue;
                if (last == null || (int)(e.Tick - last.Value) > 0)
                    last = e.Tick;
            }
            return last;
        }
    }

    /// <summary>
    /// The regions monsters never enter (<c>MonsterAiSettings.noEntryAreaTypes</c> over the <c>RegionDef</c> area types of
    /// the exported <c>regions.json</c>), by grid region index, like the server's <c>MonsterAiRules</c>.
    /// </summary>
    public static class QaRegions
    {
        [Serializable]
        private sealed class Entry
        {
            public int index;
            public string id = "";
        }

        [Serializable]
        private sealed class File
        {
            public Entry[] regions = Array.Empty<Entry>();
        }

        public static bool[] NoEntry(string worldDirectory, ConfigSet config)
        {
            var noEntry = new bool[256];
            var types = config.Single<MonsterAiSettings>().NoEntryAreaTypes;
            var file = JsonUtility.FromJson<File>(System.IO.File.ReadAllText(Path.Combine(worldDirectory, WorldFiles.Regions)));
            foreach (var region in file.regions)
            {
                var def = config.TryGet(typeof(RegionDef), out var table)
                    ? table.Rows.Cast<RegionDef>().FirstOrDefault(r => r.Id == region.id)
                    : null;
                if (def != null && region.index is > 0 and < 256 && Array.IndexOf(types, def.AreaType) >= 0)
                    noEntry[region.index] = true;
            }
            return noEntry;
        }

        /// <summary>
        /// The regions whose death drops a loot bag (QA M4 wave 1 F1, R-4.3-03), by grid region index: their <c>RegionDef</c> area
        /// type's <c>RegionDefaults.deathDrop</c> is on, as the server's <c>WorldLoader</c> resolves the flags.
        /// </summary>
        public static bool[] DeathDrop(string worldDirectory, ConfigSet config)
        {
            var drops = new bool[256];
            var file = JsonUtility.FromJson<File>(System.IO.File.ReadAllText(Path.Combine(worldDirectory, WorldFiles.Regions)));
            foreach (var region in file.regions)
            {
                if (region.index is > 0 and < 256 && AreaFlags(config, region.id) is { } flags)
                    drops[region.index] = flags.DeathDrop == Shared.World.DeathDrop.On;
            }
            return drops;
        }

        /// <summary>The area type of a <c>RegionDef</c> id and its <c>RegionDefaults</c> row; null when either is missing.</summary>
        public static RegionDefaults? AreaFlags(ConfigSet config, string regionId)
        {
            var def = config.TryGet(typeof(RegionDef), out var regions) ? regions.Rows.Cast<RegionDef>().FirstOrDefault(r => r.Id == regionId) : null;
            if (def == null || !config.TryGet(typeof(RegionDefaults), out var defaults))
                return null;
            return defaults.Rows.Cast<RegionDefaults>().FirstOrDefault(d => d.Id == def.AreaType);
        }

        /// <summary>The <c>RegionDef</c> id of a region index of the exported world; "" for none.</summary>
        public static string IdOf(string worldDirectory, int index)
        {
            var file = JsonUtility.FromJson<File>(System.IO.File.ReadAllText(Path.Combine(worldDirectory, WorldFiles.Regions)));
            return file.regions.FirstOrDefault(r => r.index == index)?.id ?? "";
        }
    }
}
