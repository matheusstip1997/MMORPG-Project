using Shared.Protocol;
using Shared.Simulation;
using Shared.World;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace BotClient.Brain;

/// <summary>
/// How a bot walks like a player: wire WASD keys toward a point and walkable paths over the shared world grid. Ported
/// from the QA run's <c>QaNavigation</c> (which uses UnityEngine and cannot be linked). TODO(J): one engine-free copy for
/// QA and bots. Ground positions are in raw fixed point; +Y is W (as in <see cref="MovementSim"/>).
/// </summary>
public static class GridPath
{
    private const int Directions = 8;

    // Direction index clockwise from +Y: 0 W, 1 WD, 2 D, ... 7 WA. Wire keys are world axes (no camera).
    private static readonly MoveKeys[] KeysForDirection =
    [
        MoveKeys.W, MoveKeys.W | MoveKeys.D, MoveKeys.D, MoveKeys.S | MoveKeys.D,
        MoveKeys.S, MoveKeys.S | MoveKeys.A, MoveKeys.A, MoveKeys.W | MoveKeys.A,
    ];

    /// <summary>The nearest of the 8 key directions toward (<paramref name="dxCm"/>, <paramref name="dyCm"/>); None for none.</summary>
    public static MoveKeys KeysToward(double dxCm, double dyCm)
    {
        if (dxCm * dxCm + dyCm * dyCm < 1e-6)
            return MoveKeys.None;
        double degrees = Math.Atan2(dxCm, dyCm) * 180.0 / Math.PI; // clockwise from +Y
        int direction = (((int)Math.Round(degrees / (360.0 / Directions)) % Directions) + Directions) % Directions;
        return KeysForDirection[direction];
    }

    /// <summary>The binary-angle facing (65536 per turn, clockwise from +Y) toward (<paramref name="dxCm"/>, <paramref name="dyCm"/>).</summary>
    public static ushort FacingToward(double dxCm, double dyCm)
    {
        double turns = Math.Atan2(dxCm, dyCm) / (2 * Math.PI);
        if (turns < 0)
            turns += 1;
        return (ushort)((int)Math.Round(turns * 65_536) & 0xFFFF);
    }

    public static double Cm(int raw) => raw / (double)FixedPoint.RawPerCm;

    public static double DistanceCm(FixVec2 a, FixVec2 b)
    {
        double dx = Cm(a.X - b.X), dy = Cm(a.Y - b.Y);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>True when a straight walk from <paramref name="from"/> to <paramref name="to"/> stays on walkable cells.</summary>
    public static bool Clear(WorldGrid grid, FixVec2 from, FixVec2 to) => !GridRay.Cast(grid, from, to, out _);

    /// <summary>
    /// <see cref="Clear(WorldGrid, FixVec2, FixVec2)"/> for a walker held to <paramref name="gate"/> (R-2.3-06): the line also
    /// crosses no region the walker's level cannot enter (sampled every half cell; one already inside is allowed).
    /// </summary>
    public static bool Clear(WorldGrid grid, FixVec2 from, FixVec2 to, in MoverGate gate)
    {
        if (!Clear(grid, from, to))
            return false;
        if (!gate.Active)
            return true;
        int allowed = Math.Max(gate.Level, gate.Gates!.MinLevelOf(grid.RegionIndexAt(from)));
        double lengthCm = DistanceCm(from, to);
        int samples = (int)(lengthCm * 2 / grid.CellSizeCm) + 1;
        for (int i = 1; i <= samples; i++)
        {
            var at = new FixVec2(from.X + (int)((long)(to.X - from.X) * i / samples), from.Y + (int)((long)(to.Y - from.Y) * i / samples));
            if (gate.Gates.MinLevelOf(grid.RegionIndexAt(at)) > allowed)
                return false;
        }
        return true;
    }

    /// <summary>
    /// A walkable path of cell centers from <paramref name="from"/> to <paramref name="to"/> (weighted A* over the 8
    /// neighbours with octile costs, no corner cutting), ending exactly at <paramref name="to"/>; null when there is none.
    /// <para>
    /// Not a breadth-first search: in the field zone (about 700 000 cells) it flooded the grid around a far goal with a
    /// 2.8 MB array per call, 27 to 55 ms a call, and a goal inside a ridge (a back-off leg) flooded the whole grid. Fifty
    /// bots replanning on the one bot thread stalled every bot for up to seconds; the server ran those ticks without their
    /// input frames and each one during an action came back as a prediction correction (QA M2 wave 1, O6: m2-zone about 1.6
    /// per bot-second). The heuristic's weight trades a path about 1 % longer for a search about 2.5 times smaller.
    /// </para>
    /// <para>
    /// With <paramref name="gate"/> (R-2.3-06, the tutorial gate of R-12.3-01) a step into a region the bot's level cannot
    /// enter is refused as the shared movement refuses it (each axis of a diagonal step in the order <c>GridCollision</c>
    /// moves: X, then Y), and a goal in such a region has no path at once (no flood of the tutorial side).
    /// </para>
    /// </summary>
    public static List<FixVec2>? Find(WorldGrid grid, FixVec2 from, FixVec2 to, in MoverGate gate = default)
    {
        LastExpandedCells = 0;
        int startColumn = grid.ColumnOf(from.X), startRow = grid.RowOf(from.Y);
        int endColumn = grid.ColumnOf(to.X), endRow = grid.RowOf(to.Y);
        if (!grid.InBounds(startColumn, startRow) || !grid.InBounds(endColumn, endRow))
            return null;
        if (gate.Active && gate.Gates!.MinLevelOf(grid.RegionIndexOfCell(endColumn, endRow))
            > Math.Max(gate.Level, gate.Gates.MinLevelOf(grid.RegionIndexOfCell(startColumn, startRow))))
            return null;
        if (!grid.IsWalkableCell(endColumn, endRow) && !grid.IsWalkableCell(endColumn - 1, endRow) && !grid.IsWalkableCell(endColumn + 1, endRow)
            && !grid.IsWalkableCell(endColumn, endRow - 1) && !grid.IsWalkableCell(endColumn, endRow + 1))
            return null; // a blocked goal is entered from a walkable side, and a diagonal step in needs two of them (corners)
        int start = startRow * grid.Width + startColumn;
        int end = endRow * grid.Width + endColumn;
        var search = _search ??= new Search();
        search.Begin(grid.CellCount);
        search.Open(start, start, 0, Heuristic(startColumn, startRow, endColumn, endRow));
        while (search.TryTakeBest(out int cell) && cell != end)
        {
            int column = cell % grid.Width, row = cell / grid.Width;
            int cost = search.Cost(cell);
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
                    if (gate.Active && (GridCollision.GateBlocks(grid, gate, column, row, c, row)
                                        || GridCollision.GateBlocks(grid, gate, c, row, c, r)))
                        continue;
                    int nextCost = cost + (dx != 0 && dy != 0 ? DiagonalCost : StraightCost);
                    if (search.IsClosed(next) || (search.IsSeen(next) && search.Cost(next) <= nextCost))
                        continue;
                    search.Open(next, cell, nextCost, nextCost + Heuristic(c, r, endColumn, endRow));
                }
            }
        }
        LastExpandedCells = search.Expanded;
        if (!search.IsClosed(end))
            return null;
        var path = new List<FixVec2> { to };
        for (int cell = search.Previous(end); cell != start; cell = search.Previous(cell))
            path.Add(CellCenter(grid, cell));
        path.Reverse();
        return path;
    }

    // A* costs: a straight step 10, a diagonal 14 (about 10 * sqrt 2), and the octile distance to the goal weighted by 3/2
    // (measured over the 812 searches between the city and the field zone's camps: 2.5 times fewer cells, paths 0.7 % longer).
    private const int StraightCost = 10;
    private const int DiagonalCost = 14;
    private const int HeuristicWeightNumerator = 3;
    private const int HeuristicWeightDenominator = 2;

    private static int Heuristic(int column, int row, int endColumn, int endRow)
    {
        int dx = Math.Abs(column - endColumn), dy = Math.Abs(row - endRow);
        int octile = StraightCost * Math.Max(dx, dy) + (DiagonalCost - StraightCost) * Math.Min(dx, dy);
        return octile * HeuristicWeightNumerator / HeuristicWeightDenominator;
    }

    /// <summary>Cells the last <see cref="Find"/> on this thread expanded (tests check that a search stays near its line).</summary>
    [ThreadStatic] internal static int LastExpandedCells;

    // One search state per thread, kept between calls: the field zone's grid has about 700 000 cells, and a fresh array per
    // search (as the breadth-first search had) cost every bot replan a large-object allocation (m2-zone stalls, see Find).
    [ThreadStatic] private static Search? _search;

    /// <summary>
    /// A* bookkeeping over the grid's cells without clearing them per search: a cell's entries count only when its stamp is
    /// the current search's. Grows to the largest grid used, never shrinks.
    /// </summary>
    private sealed class Search
    {
        private int[] _stamp = Array.Empty<int>();
        private int[] _cost = Array.Empty<int>();
        private int[] _previous = Array.Empty<int>();
        private bool[] _closed = Array.Empty<bool>();
        private readonly PriorityQueue<int, long> _open = new();
        private int _current;

        public int Expanded { get; private set; }

        public void Begin(int cellCount)
        {
            if (_stamp.Length < cellCount)
            {
                _stamp = new int[cellCount];
                _cost = new int[cellCount];
                _previous = new int[cellCount];
                _closed = new bool[cellCount];
                _current = 0;
            }
            if (_current == int.MaxValue)
            {
                Array.Clear(_stamp);
                _current = 0;
            }
            _current++;
            _open.Clear();
            Expanded = 0;
        }

        public bool IsSeen(int cell) => _stamp[cell] == _current;

        public bool IsClosed(int cell) => IsSeen(cell) && _closed[cell];

        public int Cost(int cell) => _cost[cell];

        public int Previous(int cell) => _previous[cell];

        /// <summary>Records the cheaper way to <paramref name="cell"/>; ties on the estimate go to the cell further along.</summary>
        public void Open(int cell, int previous, int cost, int estimate)
        {
            _stamp[cell] = _current;
            _cost[cell] = cost;
            _previous[cell] = previous;
            _closed[cell] = false;
            _open.Enqueue(cell, ((long)estimate << 32) - cost);
        }

        /// <summary>The open cell with the lowest estimate, closed now; entries left behind by a cheaper way are skipped.</summary>
        public bool TryTakeBest(out int cell)
        {
            while (_open.TryDequeue(out cell, out _))
            {
                if (_closed[cell])
                    continue;
                _closed[cell] = true;
                Expanded++;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The farthest point of <paramref name="path"/> walkable in a straight line from <paramref name="position"/> (and, with
    /// <paramref name="gate"/>, not across a region the walker's level cannot enter).
    /// </summary>
    public static FixVec2 NextWaypoint(WorldGrid grid, FixVec2 position, IReadOnlyList<FixVec2> path, in MoverGate gate = default)
    {
        for (int i = path.Count - 1; i > 0; i--)
        {
            if (Clear(grid, position, path[i], gate))
                return path[i];
        }
        return path[0];
    }

    private static FixVec2 CellCenter(WorldGrid grid, int cell)
    {
        int column = cell % grid.Width, row = cell / grid.Width;
        int half = FixedPoint.FromCm(grid.CellSizeCm) / 2;
        return new FixVec2(grid.ColumnStartRaw(column) + half, grid.RowStartRaw(row) + half);
    }
}
