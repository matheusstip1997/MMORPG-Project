using System;
using System.Collections.Generic;
using Shared.Simulation;

namespace Shared.World
{
    /// <summary>
    /// Where a walker can get on foot: a 4-neighbour flood over the walkable cells under the same per-step rule as
    /// <see cref="GridCollision"/> (walls and the mover's level gate, R-2.3-06). For world checks, tests and tools, not for the
    /// tick: it allocates one flag per cell.
    /// </summary>
    public static class GridReach
    {
        /// <summary>
        /// The cells (row * width + column) reachable from <paramref name="starts"/> for a mover held to
        /// <paramref name="gate"/>. A start outside the grid is ignored; a start cell is reached even when it is not walkable,
        /// the same as <see cref="GridCollision"/> lets an entity walk out of such a cell.
        /// </summary>
        public static bool[] Flood(WorldGrid grid, in MoverGate gate, IEnumerable<FixVec2> starts)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (starts == null)
                throw new ArgumentNullException(nameof(starts));
            int width = grid.Width;
            var reached = new bool[grid.CellCount];
            var queue = new Queue<int>();
            foreach (var start in starts)
            {
                int column = grid.ColumnOf(start.X);
                int row = grid.RowOf(start.Y);
                if (!grid.InBounds(column, row) || reached[row * width + column])
                    continue;
                reached[row * width + column] = true;
                queue.Enqueue(row * width + column);
            }

            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                int column = cell % width;
                int row = cell / width;
                Visit(grid, gate, reached, queue, column, row, column + 1, row);
                Visit(grid, gate, reached, queue, column, row, column - 1, row);
                Visit(grid, gate, reached, queue, column, row, column, row + 1);
                Visit(grid, gate, reached, queue, column, row, column, row - 1);
            }
            return reached;
        }

        private static void Visit(WorldGrid grid, in MoverGate gate, bool[] reached, Queue<int> queue, int fromColumn,
            int fromRow, int column, int row)
        {
            if (!grid.IsWalkableCell(column, row))
                return;
            int cell = row * grid.Width + column;
            if (reached[cell] || GridCollision.GateBlocks(grid, gate, fromColumn, fromRow, column, row))
                return;
            reached[cell] = true;
            queue.Enqueue(cell);
        }
    }
}
