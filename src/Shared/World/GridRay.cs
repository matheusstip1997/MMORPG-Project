using System;
using Shared.Simulation;

namespace Shared.World
{
    /// <summary>
    /// Where a straight segment first meets a non-walkable cell of the world grid (spec 3.2: projectiles stop on the world),
    /// shared by the server and the client so both stop a projectile at the same raw point. Integer only, no allocations
    /// (rule 6).
    /// <para>
    /// The segment visits every cell it passes through, in order (a grid traversal in the style of Amanatides and Woo, with
    /// the crossings compared by cross-multiplication). Where it passes exactly through a cell corner it steps along X
    /// first, so it also visits the cell beside the corner: a projectile cannot slip between two blocked cells that only
    /// touch at a corner. Like <see cref="GridCollision"/>, only cells being entered are checked (a projectile fired from a
    /// non-walkable cell still flies out), cells outside the grid are blocked, and a segment that starts outside the grid
    /// is blocked at its start.
    /// </para>
    /// <para>
    /// The walkable layer is grown by the agent radius at export, so a projectile stops about that far in front of a wall,
    /// and it also stops at non-walkable cells such as water. TODO: a projectile-blocking layer if those should differ.
    /// </para>
    /// </summary>
    public static class GridRay
    {
        /// <summary>
        /// True when the segment from <paramref name="from"/> to <paramref name="to"/> enters a non-walkable cell;
        /// <paramref name="stop"/> is then the last point of the segment before it (inside the last walkable cell), else
        /// <paramref name="to"/>.
        /// </summary>
        public static bool Cast(WorldGrid grid, FixVec2 from, FixVec2 to, out FixVec2 stop)
        {
            int column = grid.ColumnOf(from.X);
            int row = grid.RowOf(from.Y);
            if (!grid.InBounds(column, row))
            {
                stop = from;
                return true;
            }

            int endColumn = grid.ColumnOf(to.X);
            int endRow = grid.RowOf(to.Y);
            long dx = (long)to.X - from.X;
            long dy = (long)to.Y - from.Y;
            long absDx = Math.Abs(dx);
            long absDy = Math.Abs(dy);
            int stepX = Math.Sign(dx);
            int stepY = Math.Sign(dy);
            long cellRaw = FixedPoint.FromCm(grid.CellSizeCm);
            // Distance along each axis from the start to where the segment enters the next column or row.
            long toNextX = stepX > 0 ? grid.ColumnStartRaw(column + 1) - (long)from.X
                : stepX < 0 ? (long)from.X - grid.ColumnStartRaw(column) + 1 : long.MaxValue;
            long toNextY = stepY > 0 ? grid.RowStartRaw(row + 1) - (long)from.Y
                : stepY < 0 ? (long)from.Y - grid.RowStartRaw(row) + 1 : long.MaxValue;

            int crossings = Math.Abs(endColumn - column) + Math.Abs(endRow - row);
            for (int i = 0; i < crossings; i++)
            {
                // Which boundary comes first along the segment: toNextX / |dx| against toNextY / |dy|. Ties step X.
                bool alongX = stepY == 0 || (stepX != 0 && toNextX * absDy <= toNextY * absDx);
                if (alongX)
                {
                    int next = column + stepX;
                    if (!grid.IsWalkableCell(next, row))
                    {
                        int x = stepX > 0 ? grid.ColumnStartRaw(column + 1) - 1 : grid.ColumnStartRaw(column);
                        stop = new FixVec2(x, PointAt(from.Y, dy, x - (long)from.X, dx));
                        return true;
                    }
                    column = next;
                    toNextX += cellRaw;
                }
                else
                {
                    int next = row + stepY;
                    if (!grid.IsWalkableCell(column, next))
                    {
                        int y = stepY > 0 ? grid.RowStartRaw(row + 1) - 1 : grid.RowStartRaw(row);
                        stop = new FixVec2(PointAt(from.X, dx, y - (long)from.Y, dy), y);
                        return true;
                    }
                    row = next;
                    toNextY += cellRaw;
                }
            }
            stop = to;
            return false;
        }

        /// <summary>The other coordinate where the segment has moved <paramref name="travelled"/> along the axis of <paramref name="axisDelta"/>.</summary>
        private static int PointAt(int start, long delta, long travelled, long axisDelta) =>
            (int)(start + delta * travelled / axisDelta);
    }
}
