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
    /// </summary>
    public static class GridCollision
    {
        /// <summary>Where a move from <paramref name="from"/> towards <paramref name="to"/> ends.</summary>
        public static FixVec2 Move(WorldGrid grid, FixVec2 from, FixVec2 to)
        {
            int column = grid.ColumnOf(from.X);
            int row = grid.RowOf(from.Y);
            if (!grid.InBounds(column, row))
                return from;

            int x = MoveX(grid, from.X, to.X, row);
            int y = MoveY(grid, from.Y, to.Y, grid.ColumnOf(x));
            return new FixVec2(x, y);
        }

        private static int MoveX(WorldGrid grid, int fromX, int toX, int row)
        {
            int start = grid.ColumnOf(fromX);
            int end = grid.ColumnOf(toX);
            if (end > start)
            {
                for (int c = start + 1; c <= end; c++)
                {
                    if (!grid.IsWalkableCell(c, row))
                        return grid.ColumnStartRaw(c) - 1;
                }
            }
            else if (end < start)
            {
                for (int c = start - 1; c >= end; c--)
                {
                    if (!grid.IsWalkableCell(c, row))
                        return grid.ColumnStartRaw(c + 1);
                }
            }
            return toX;
        }

        private static int MoveY(WorldGrid grid, int fromY, int toY, int column)
        {
            int start = grid.RowOf(fromY);
            int end = grid.RowOf(toY);
            if (end > start)
            {
                for (int r = start + 1; r <= end; r++)
                {
                    if (!grid.IsWalkableCell(column, r))
                        return grid.RowStartRaw(r) - 1;
                }
            }
            else if (end < start)
            {
                for (int r = start - 1; r >= end; r--)
                {
                    if (!grid.IsWalkableCell(column, r))
                        return grid.RowStartRaw(r + 1);
                }
            }
            return toY;
        }
    }
}
