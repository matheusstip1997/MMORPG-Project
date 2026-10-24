#nullable enable
using System.Collections.Generic;
using Shared.World;

namespace Game.Gameplay.Dev
{
    /// <summary>A rectangle of whole grid cells: <see cref="Width"/> columns by <see cref="Height"/> rows from a corner cell.</summary>
    public readonly struct CellRect
    {
        public CellRect(int column, int row, int width, int height)
        {
            Column = column;
            Row = row;
            Width = width;
            Height = height;
        }

        public int Column { get; }
        public int Row { get; }
        public int Width { get; }
        public int Height { get; }

        public override string ToString() => $"({Column}, {Row}) {Width}x{Height}";
    }

    /// <summary>
    /// A run of cell boundary between two different regions. Vertical: the line between columns
    /// <see cref="Line"/> - 1 and <see cref="Line"/>, from row <see cref="Start"/> for <see cref="Length"/> rows.
    /// Horizontal: the line between rows <see cref="Line"/> - 1 and <see cref="Line"/>, along columns.
    /// </summary>
    public readonly struct CellEdge
    {
        public CellEdge(bool vertical, int line, int start, int length)
        {
            Vertical = vertical;
            Line = line;
            Start = start;
            Length = length;
        }

        public bool Vertical { get; }
        public int Line { get; }
        public int Start { get; }
        public int Length { get; }

        public override string ToString() => $"{(Vertical ? "x" : "y")} line {Line} from {Start} for {Length}";
    }

    /// <summary>
    /// What the dev scene draws of the exported world (QA wave 3 F3): the grid's blocked cells, which are what server
    /// collision and projectile rays test (spec 2.2 collision; blockers grown by the agent radius), merged into
    /// rectangles, and the borders between regions (spec 2.3), merged into runs. Cells outside the grid count as region
    /// 0, so the world's edge is a border too. Pure: no Unity types, so it has EditMode tests.
    /// </summary>
    public static class WorldGeometry
    {
        /// <summary>The blocked cells as rectangles, greedily merged: along a row first, then down while the span stays blocked.</summary>
        public static List<CellRect> BlockedRects(WorldGrid grid)
        {
            var rects = new List<CellRect>();
            var used = new bool[grid.Width * grid.Height];
            for (int row = 0; row < grid.Height; row++)
            {
                for (int column = 0; column < grid.Width; column++)
                {
                    if (!Free(grid, used, column, row))
                        continue;
                    int width = 1;
                    while (column + width < grid.Width && Free(grid, used, column + width, row))
                        width++;
                    int height = 1;
                    while (row + height < grid.Height && SpanFree(grid, used, column, row + height, width))
                        height++;
                    for (int r = row; r < row + height; r++)
                    {
                        for (int c = column; c < column + width; c++)
                            used[r * grid.Width + c] = true;
                    }
                    rects.Add(new CellRect(column, row, width, height));
                }
            }
            return rects;
        }

        /// <summary>Every cell boundary with a different region on each side, merged into straight runs.</summary>
        public static List<CellEdge> RegionEdges(WorldGrid grid)
        {
            var edges = new List<CellEdge>();
            for (int line = 0; line <= grid.Width; line++)
                AddRuns(edges, true, line, grid.Height, along => Region(grid, line - 1, along) != Region(grid, line, along));
            for (int line = 0; line <= grid.Height; line++)
                AddRuns(edges, false, line, grid.Width, along => Region(grid, along, line - 1) != Region(grid, along, line));
            return edges;
        }

        private static void AddRuns(List<CellEdge> edges, bool vertical, int line, int count, System.Func<int, bool> differs)
        {
            int start = -1;
            for (int along = 0; along <= count; along++)
            {
                bool border = along < count && differs(along);
                if (border && start < 0)
                    start = along;
                else if (!border && start >= 0)
                {
                    edges.Add(new CellEdge(vertical, line, start, along - start));
                    start = -1;
                }
            }
        }

        private static int Region(WorldGrid grid, int column, int row) => grid.RegionIndexOfCell(column, row);

        private static bool Free(WorldGrid grid, bool[] used, int column, int row) =>
            !grid.IsWalkableCell(column, row) && !used[row * grid.Width + column];

        private static bool SpanFree(WorldGrid grid, bool[] used, int column, int row, int width)
        {
            for (int c = column; c < column + width; c++)
            {
                if (!Free(grid, used, c, row))
                    return false;
            }
            return true;
        }
    }
}
