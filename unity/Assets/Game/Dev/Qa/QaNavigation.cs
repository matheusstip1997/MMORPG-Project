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
        public const int Directions = 8;

        // Screen direction index clockwise from screen-up, as in ScreenRelativeKeys: 0 W, 1 WD, 2 D, ... 7 WA.
        private static readonly MoveKeys[] KeysForDirection =
        {
            MoveKeys.W, MoveKeys.W | MoveKeys.D, MoveKeys.D, MoveKeys.S | MoveKeys.D,
            MoveKeys.S, MoveKeys.S | MoveKeys.A, MoveKeys.A, MoveKeys.W | MoveKeys.A,
        };

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
            int screen = (((world - cameraStep) % Directions) + Directions) % Directions;
            return KeysForDirection[screen];
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

        /// <summary>
        /// A walkable path of cell centers from <paramref name="from"/> to <paramref name="to"/> (breadth-first over the
        /// 8 neighbours, no corner cutting), ending exactly at <paramref name="to"/>; null when there is none.
        /// </summary>
        public static List<FixVec2>? FindPath(WorldGrid grid, FixVec2 from, FixVec2 to)
        {
            int startColumn = grid.ColumnOf(from.X), startRow = grid.RowOf(from.Y);
            int endColumn = grid.ColumnOf(to.X), endRow = grid.RowOf(to.Y);
            if (!grid.InBounds(startColumn, startRow) || !grid.InBounds(endColumn, endRow))
                return null;
            int start = startRow * grid.Width + startColumn;
            int end = endRow * grid.Width + endColumn;
            var previous = new int[grid.CellCount];
            for (int i = 0; i < previous.Length; i++)
                previous[i] = -1;
            previous[start] = start;
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.Count > 0 && previous[end] < 0)
            {
                int cell = queue.Dequeue();
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
                        if (previous[next] >= 0)
                            continue;
                        previous[next] = cell;
                        queue.Enqueue(next);
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

        /// <summary>The farthest point of <paramref name="path"/> walkable in a straight line from <paramref name="position"/>.</summary>
        public static FixVec2 NextWaypoint(WorldGrid grid, FixVec2 position, IReadOnlyList<FixVec2> path)
        {
            for (int i = path.Count - 1; i > 0; i--)
            {
                if (Clear(grid, position, path[i]))
                    return path[i];
            }
            return path[0];
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
    }
}
