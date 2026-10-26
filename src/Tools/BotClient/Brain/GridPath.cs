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
    /// A walkable path of cell centers from <paramref name="from"/> to <paramref name="to"/> (breadth-first over the 8
    /// neighbours, no corner cutting), ending exactly at <paramref name="to"/>; null when there is none.
    /// </summary>
    public static List<FixVec2>? Find(WorldGrid grid, FixVec2 from, FixVec2 to)
    {
        int startColumn = grid.ColumnOf(from.X), startRow = grid.RowOf(from.Y);
        int endColumn = grid.ColumnOf(to.X), endRow = grid.RowOf(to.Y);
        if (!grid.InBounds(startColumn, startRow) || !grid.InBounds(endColumn, endRow))
            return null;
        int start = startRow * grid.Width + startColumn;
        int end = endRow * grid.Width + endColumn;
        var previous = new int[grid.CellCount];
        Array.Fill(previous, -1);
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

    private static FixVec2 CellCenter(WorldGrid grid, int cell)
    {
        int column = cell % grid.Width, row = cell / grid.Width;
        int half = FixedPoint.FromCm(grid.CellSizeCm) / 2;
        return new FixVec2(grid.ColumnStartRaw(column) + half, grid.RowStartRaw(row) + half);
    }
}
