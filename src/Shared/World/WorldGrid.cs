using System;
using Shared.Simulation;

namespace Shared.World
{
    /// <summary>
    /// The exported walkability grid with its per-cell region and height layers (spec 2.3, 11.6). Read-only once built,
    /// so the server's world thread and the client's prediction can share one instance. Integer math only (rule 6).
    /// <para>
    /// Cell (x, y) covers ground-plane centimeters [OriginX + x * CellSize, OriginX + (x + 1) * CellSize) on X, and the
    /// same on Y. Anything outside the grid is not walkable, has no region, and has no height.
    /// </para>
    /// </summary>
    public sealed class WorldGrid
    {
        private readonly byte[] _flags;
        private readonly byte[] _regions;
        private readonly short[] _heights;

        /// <summary>Builds a grid from its layers; the arrays are kept, not copied. Bounds are checked like the reader does.</summary>
        public WorldGrid(int originXCm, int originYCm, int cellSizeCm, int width, int height, int baseHeightCm,
            byte[] flags, byte[] regions, short[] heights)
        {
            string? error = WorldGridReader.CheckGeometry(originXCm, originYCm, cellSizeCm, width, height);
            if (error != null)
                throw new ArgumentException(error);
            int cells = width * height;
            if (flags == null || regions == null || heights == null)
                throw new ArgumentNullException(flags == null ? nameof(flags) : regions == null ? nameof(regions) : nameof(heights));
            if (flags.Length != cells || regions.Length != cells || heights.Length != cells)
                throw new ArgumentException("every layer must hold width * height cells");

            OriginXCm = originXCm;
            OriginYCm = originYCm;
            CellSizeCm = cellSizeCm;
            Width = width;
            Height = height;
            BaseHeightCm = baseHeightCm;
            _flags = flags;
            _regions = regions;
            _heights = heights;

            int maxRegion = 0;
            for (int i = 0; i < cells; i++)
            {
                if (regions[i] > maxRegion)
                    maxRegion = regions[i];
            }
            MaxRegionIndex = maxRegion;
        }

        public int OriginXCm { get; }
        public int OriginYCm { get; }
        public int CellSizeCm { get; }
        public int Width { get; }
        public int Height { get; }
        public int BaseHeightCm { get; }

        /// <summary>The highest region index any cell uses; the world's region list must be at least this long.</summary>
        public int MaxRegionIndex { get; }

        public int CellCount => Width * Height;

        /// <summary>Column of a raw Q24.8 X, possibly outside [0, Width).</summary>
        public int ColumnOf(int rawX) => FloorDiv(FixedPoint.ToCmFloor(rawX) - OriginXCm, CellSizeCm);

        /// <summary>Row of a raw Q24.8 Y, possibly outside [0, Height).</summary>
        public int RowOf(int rawY) => FloorDiv(FixedPoint.ToCmFloor(rawY) - OriginYCm, CellSizeCm);

        /// <summary>Raw Q24.8 X of the low edge of a column (columns 0..Width are valid edges).</summary>
        public int ColumnStartRaw(int column) => FixedPoint.FromCm(OriginXCm + column * CellSizeCm);

        /// <summary>Raw Q24.8 Y of the low edge of a row (rows 0..Height are valid edges).</summary>
        public int RowStartRaw(int row) => FixedPoint.FromCm(OriginYCm + row * CellSizeCm);

        public bool InBounds(int column, int row) => (uint)column < (uint)Width && (uint)row < (uint)Height;

        public bool IsWalkableCell(int column, int row) =>
            InBounds(column, row) && (_flags[row * Width + column] & WorldGridFormat.FlagWalkable) != 0;

        public bool IsWalkable(FixVec2 position) => IsWalkableCell(ColumnOf(position.X), RowOf(position.Y));

        /// <summary>Region index of a cell (0 = none, also outside the grid).</summary>
        public byte RegionIndexOfCell(int column, int row) => InBounds(column, row) ? _regions[row * Width + column] : (byte)0;

        /// <summary>One array read (spec 2.3: per-cell region lookup).</summary>
        public byte RegionIndexAt(FixVec2 position) => RegionIndexOfCell(ColumnOf(position.X), RowOf(position.Y));

        /// <summary>Ground height in cm at a position; false outside the grid.</summary>
        public bool TryGetHeightCm(FixVec2 position, out int heightCm)
        {
            int column = ColumnOf(position.X);
            int row = RowOf(position.Y);
            if (!InBounds(column, row))
            {
                heightCm = 0;
                return false;
            }
            heightCm = BaseHeightCm + _heights[row * Width + column];
            return true;
        }

        internal byte FlagsAt(int index) => _flags[index];
        internal byte RegionAt(int index) => _regions[index];
        internal short HeightOffsetAt(int index) => _heights[index];

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            if ((value % divisor != 0) && (value < 0))
                quotient--;
            return quotient;
        }
    }
}
