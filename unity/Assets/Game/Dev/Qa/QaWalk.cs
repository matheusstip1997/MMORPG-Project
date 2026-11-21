#nullable enable
using System;
using System.Collections.Generic;
using Shared.Simulation;
using Shared.World;

namespace Game.Dev.Qa
{
    /// <summary>
    /// One QA walk to a goal along a grid path, the steering every walk of the run shares (<c>QaScenario.WalkTo</c>, the walk
    /// home, <c>Approach</c>): each frame <see cref="Step"/> takes the position and the clock and gives the world direction to
    /// hold (0..7 clockwise from +Y, <see cref="QaNavigation.ScreenDirection"/> at camera step 0; -1 for none).
    /// <para>
    /// QA M4 wave 2 F1: the walk only ever moves forward along its path. The old steering aimed at the farthest path point a
    /// straight line reached, the goal itself when it looked clear from the city gateway's corner; off the path, only points
    /// behind were in sight, so it walked back to the corner, re-planned the same path and went round in that loop until its
    /// time ran out. Here a point behind the path's progress is never a target: when nothing ahead is in sight the walk plans
    /// again from where it stands. Without <see cref="QaTimeouts.StuckCm"/> of progress in <see cref="QaTimeouts.StuckSeconds"/>
    /// it plans again and follows the new path cell by cell (<see cref="QaNavigation.FollowingPoint"/>) for a while; a second
    /// stall in a row first steps away from the nearest closed cells for <see cref="QaTimeouts.UnstickSeconds"/>.
    /// </para>
    /// Pure (no Unity state): EditMode tests play it tick by tick through <see cref="MovementSim"/> on the committed world.
    /// </summary>
    public sealed class QaWalk
    {
        /// <summary>How many path points ahead of the progress a straight line is tried to (a long path is not cast in full).</summary>
        public const int LookAhead = 24;

        /// <summary>A walk that lost sight of its path plans again at most this often (a plan searches the grid).</summary>
        public const double LostSightReplanSeconds = 0.5;

        private readonly WorldGrid _grid;
        private readonly WorldGrid _paths;
        private readonly List<string> _notes = new List<string>();
        private List<FixVec2>? _path;
        private int _next;
        private bool _started;
        private double _progressAt;
        private FixVec2 _progressFrom;
        private double _strictUntil = -1;
        private double _unstickUntil = -1;
        private int _unstickDirection = -1;
        private int _stalls;
        private int _lastDirection = -1;
        private double _plannedAt = double.NegativeInfinity;

        /// <param name="grid">The world's collision grid (the key probe, and the plan when <paramref name="paths"/> has none).</param>
        /// <param name="goal">Where the walk ends.</param>
        /// <param name="paths">Where paths are planned (the tutorial gate's grid around the camps' aggro); null: <paramref name="grid"/>.</param>
        public QaWalk(WorldGrid grid, FixVec2 goal, WorldGrid? paths = null)
        {
            _grid = grid;
            _paths = paths ?? grid;
            Goal = goal;
        }

        public FixVec2 Goal { get; private set; }

        /// <summary>The point steered to by the last <see cref="Step"/> (for the aim and the log).</summary>
        public FixVec2 Waypoint { get; private set; }

        /// <summary>Times the walk planned again after its first path (stalls and lost sight).</summary>
        public int Replans { get; private set; }

        /// <summary>Times it stepped away from a wall after two stalls in a row.</summary>
        public int Unsticks { get; private set; }

        /// <summary>Lines for the step log since the last call (stalls, re-plans, step-aways).</summary>
        public List<string> TakeNotes()
        {
            var notes = new List<string>(_notes);
            _notes.Clear();
            return notes;
        }

        /// <summary>A new goal (a demon that moved): the next <see cref="Step"/> plans from where the walk stands.</summary>
        public void SetGoal(FixVec2 goal)
        {
            Goal = goal;
            _path = null;
        }

        /// <summary>
        /// The direction to hold this frame from <paramref name="position"/> (the predicted position, fixed point) at
        /// <paramref name="nowSeconds"/>; -1 when no path leads to the goal from here (the caller fails the walk).
        /// </summary>
        public int Step(FixVec2 position, double nowSeconds)
        {
            if (!_started)
            {
                _started = true;
                _progressAt = nowSeconds;
                _progressFrom = position;
            }
            if (nowSeconds - _progressAt > QaTimeouts.StuckSeconds)
            {
                if (Distance(position, _progressFrom) < QaTimeouts.StuckCm)
                {
                    _stalls++;
                    if (_stalls >= 2 && AwayFromWalls(position) is var away && away >= 0)
                    {
                        _unstickDirection = away;
                        _unstickUntil = nowSeconds + QaTimeouts.UnstickSeconds;
                        Unsticks++;
                        _notes.Add($"walk: no progress at ({Cm(position.X):0}, {Cm(position.Y):0}) again, a step away from the wall (direction {away})");
                    }
                    else
                    {
                        _notes.Add($"walk: no progress at ({Cm(position.X):0}, {Cm(position.Y):0}) for {QaTimeouts.StuckSeconds:0} s, path again cell by cell");
                    }
                    Plan(position, nowSeconds);
                    _strictUntil = nowSeconds + QaTimeouts.StuckSeconds;
                }
                else
                {
                    _stalls = 0;
                }
                _progressAt = nowSeconds;
                _progressFrom = position;
            }
            if (nowSeconds < _unstickUntil && _unstickDirection >= 0)
            {
                QaNavigation.ScreenDirection(_unstickDirection, 0, out double ux, out double uy);
                Waypoint = FixVec2.FromCm((int)Math.Round(Cm(position.X) + ux * _grid.CellSizeCm), (int)Math.Round(Cm(position.Y) + uy * _grid.CellSizeCm));
                return _lastDirection = _unstickDirection;
            }
            if (_unstickUntil >= 0)
            {
                _unstickUntil = -1;
                Plan(position, nowSeconds); // after the step away: from where it now stands
            }
            if (_path == null && !Plan(position, nowSeconds))
                return _lastDirection = -1;

            var waypoint = nowSeconds < _strictUntil ? Following(position) : Ahead(position);
            if (waypoint == null)
            {
                // Nothing ahead in sight (the walk drifted off its path): plan again from here, never back to a point behind
                // (at most every LostSightReplanSeconds; in between, cell by cell).
                if (nowSeconds - _plannedAt >= LostSightReplanSeconds && Plan(position, nowSeconds))
                    waypoint = Ahead(position);
                waypoint ??= Following(position);
            }
            Waypoint = waypoint!.Value;
            int direction = QaNavigation.WalkDirection(_grid, position, Cm(Waypoint.X) - Cm(position.X), Cm(Waypoint.Y) - Cm(position.Y),
                QaTimeouts.WalkProbeCm);
            return _lastDirection = direction;
        }

        /// <summary>A path from <paramref name="position"/> (on the plain grid when it stands in a cell the path grid closes).</summary>
        private bool Plan(FixVec2 position, double nowSeconds)
        {
            var path = QaNavigation.FindPath(_paths, position, Goal) ?? QaNavigation.FindPath(_grid, position, Goal);
            if (path == null)
                return false;
            if (_path != null)
                Replans++;
            _path = path;
            _next = 0;
            _plannedAt = nowSeconds;
            return true;
        }

        /// <summary>The farthest point of the path ahead of the progress (up to <see cref="LookAhead"/>) in a straight line; null for none.</summary>
        private FixVec2? Ahead(FixVec2 position)
        {
            var path = _path!;
            for (int i = Math.Min(path.Count - 1, _next + LookAhead); i >= _next; i--)
            {
                if (QaNavigation.Clear(_paths, position, path[i]) && QaNavigation.Clear(_grid, position, path[i]))
                {
                    _next = i;
                    return path[i];
                }
            }
            return null;
        }

        /// <summary>The point after the one nearest <paramref name="position"/> among those ahead of the progress (cell by cell).</summary>
        private FixVec2 Following(FixVec2 position)
        {
            var path = _path!;
            int last = Math.Min(path.Count - 1, _next + LookAhead);
            int nearest = _next;
            double best = double.MaxValue;
            for (int i = _next; i <= last; i++)
            {
                double cm = Distance(position, path[i]);
                if (cm < best)
                {
                    best = cm;
                    nearest = i;
                }
            }
            _next = nearest;
            return path[Math.Min(nearest + 1, path.Count - 1)];
        }

        /// <summary>
        /// The direction away from the closed cells within a cell and a half of <paramref name="position"/> (the nearest of the
        /// 8 that moves), or the reverse of the last direction held when none is that close; -1 when nothing moves.
        /// </summary>
        private int AwayFromWalls(FixVec2 position)
        {
            int column = _grid.ColumnOf(position.X), row = _grid.RowOf(position.Y);
            double ax = 0, ay = 0;
            double half = _grid.CellSizeCm / 2.0;
            for (int r = row - 2; r <= row + 2; r++)
            {
                for (int c = column - 2; c <= column + 2; c++)
                {
                    if (_grid.IsWalkableCell(c, r))
                        continue;
                    double cx = Cm(_grid.ColumnStartRaw(c)) + half, cy = Cm(_grid.RowStartRaw(r)) + half;
                    double dx = Cm(position.X) - cx, dy = Cm(position.Y) - cy;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d > _grid.CellSizeCm * 1.5 || d < 1e-6)
                        continue;
                    ax += dx / (d * d);
                    ay += dy / (d * d);
                }
            }
            if (ax * ax + ay * ay < 1e-12)
            {
                if (_lastDirection < 0)
                    return -1;
                QaNavigation.ScreenDirection(_lastDirection + QaNavigation.Directions / 2, 0, out ax, out ay);
            }
            return QaNavigation.WalkDirection(_grid, position, ax, ay, QaTimeouts.WalkProbeCm);
        }

        private static double Cm(int raw) => QaNavigation.Cm(raw);

        private static double Distance(FixVec2 a, FixVec2 b) => QaNavigation.Distance(Cm(a.X), Cm(a.Y), Cm(b.X), Cm(b.Y));
    }
}
