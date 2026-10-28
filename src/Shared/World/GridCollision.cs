using Shared.Simulation;

namespace Shared.World
{
    /// <summary>
    /// Collision against the exported walkability grid (backlog C3), shared by the server and client prediction so both
    /// resolve a blocked move to the same raw position. Integer only, fixed processing order (rule 6).
    /// <para>
    /// The mover is a point: the exporter already grew every blocker by the agent radius when it marked cells
    /// (the radius is in the world manifest). The move is resolved one axis at a time, X first and then Y from the
    /// new X. Each axis stops just before the first non-walkable cell it would enter, so pushing diagonally into a
    /// wall keeps the free axis: the mover slides along the wall. Corners cannot be cut, because each axis is
    /// checked in the row or column the mover actually stands in.
    /// </para>
    /// <para>
    /// Cells outside the grid are not walkable. Only cells being entered are checked, so an entity standing in a
    /// non-walkable cell (spawned there, or the world changed under it) can still walk out. An entity outside the
    /// grid does not move.
    /// </para>
    /// <para>
    /// R-2.3-06: with a <see cref="MoverGate"/>, a cell is also refused when the gate blocks the step from the cell before it
    /// (a region the mover's level cannot enter from there), so a level gate stops and slides exactly like a wall.
    /// </para>
    /// </summary>
    public static class GridCollision
    {
        /// <summary>Where a move from <paramref name="from"/> towards <paramref name="to"/> ends.</summary>
        public static FixVec2 Move(WorldGrid grid, FixVec2 from, FixVec2 to) => Move(grid, from, to, default);

        /// <summary>
        /// Where a move from <paramref name="from"/> towards <paramref name="to"/> ends for a mover held to
        /// <paramref name="gate"/> (R-2.3-06).
        /// </summary>
        public static FixVec2 Move(WorldGrid grid, FixVec2 from, FixVec2 to, in MoverGate gate)
        {
            int column = grid.ColumnOf(from.X);
            int row = grid.RowOf(from.Y);
            if (!grid.InBounds(column, row))
                return from;

            int x = MoveX(grid, from.X, to.X, row, gate);
            int y = MoveY(grid, from.Y, to.Y, grid.ColumnOf(x), gate);
            return new FixVec2(x, y);
        }

        /// <summary>
        /// Whether <paramref name="gate"/> blocks the step from cell (<paramref name="fromColumn"/>, <paramref name="fromRow"/>)
        /// into the neighbouring cell (<paramref name="column"/>, <paramref name="row"/>).
        /// </summary>
        public static bool GateBlocks(WorldGrid grid, in MoverGate gate, int fromColumn, int fromRow, int column, int row) =>
            gate.Active && gate.Blocks(grid.RegionIndexOfCell(fromColumn, fromRow), grid.RegionIndexOfCell(column, row));

        private static int MoveX(WorldGrid grid, int fromX, int toX, int row, in MoverGate gate)
        {
            int start = grid.ColumnOf(fromX);
            int end = grid.ColumnOf(toX);
            if (end > start)
            {
                for (int c = start + 1; c <= end; c++)
                {
                    if (!grid.IsWalkableCell(c, row) || GateBlocks(grid, gate, c - 1, row, c, row))
                        return grid.ColumnStartRaw(c) - 1;
                }
            }
            else if (end < start)
            {
                for (int c = start - 1; c >= end; c--)
                {
                    if (!grid.IsWalkableCell(c, row) || GateBlocks(grid, gate, c + 1, row, c, row))
                        return grid.ColumnStartRaw(c + 1);
                }
            }
            return toX;
        }

        private static int MoveY(WorldGrid grid, int fromY, int toY, int column, in MoverGate gate)
        {
            int start = grid.RowOf(fromY);
            int end = grid.RowOf(toY);
            if (end > start)
            {
                for (int r = start + 1; r <= end; r++)
                {
                    if (!grid.IsWalkableCell(column, r) || GateBlocks(grid, gate, column, r - 1, column, r))
                        return grid.RowStartRaw(r) - 1;
                }
            }
            else if (end < start)
            {
                for (int r = start - 1; r >= end; r--)
                {
                    if (!grid.IsWalkableCell(column, r) || GateBlocks(grid, gate, column, r + 1, column, r))
                        return grid.RowStartRaw(r + 1);
                }
            }
            return toY;
        }
    }
}
