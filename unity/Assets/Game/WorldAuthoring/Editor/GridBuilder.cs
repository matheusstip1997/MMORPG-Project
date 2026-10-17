#nullable enable
using Shared.World;

namespace Game.WorldAuthoring.Editor
{
    /// <summary>Per-cell working state of one export, turned into a <see cref="WorldGrid"/> at the end.</summary>
    internal sealed class GridBuilder
    {
        public GridBuilder(int originX, int originY, int cell, int width, int height)
        {
            OriginX = originX;
            OriginY = originY;
            Cell = cell;
            Width = width;
            Height = height;
            int cells = width * height;
            HasGround = new bool[cells];
            Blocked = new bool[cells];
            HeightCm = new int[cells];
            Regions = new byte[cells];
            RegionOwner = new string?[cells];
        }

        public int OriginX { get; }
        public int OriginY { get; }
        public int Cell { get; }
        public int Width { get; }
        public int Height { get; }

        public bool[] HasGround { get; }
        public bool[] Blocked { get; }
        public int[] HeightCm { get; }
        public byte[] Regions { get; }

        /// <summary>Hierarchy path of the volume that claimed each cell, for overlap reports.</summary>
        public string?[] RegionOwner { get; }

        public int WalkableCount { get; private set; }

        public int Index(int x, int y) => y * Width + x;

        public bool IsWalkable(int index) => HasGround[index] && !Blocked[index];

        /// <summary>Twice the cm coordinate of a cell center, so odd cell sizes stay integer.</summary>
        public long CenterTwice(int origin, int cellIndex) => 2L * origin + (2L * cellIndex + 1) * Cell;

        /// <summary>
        /// The grid with heights relative to the lowest ground (cells without ground store 0). Null, with an error, when
        /// the height range does not fit the format's 16-bit offsets.
        /// </summary>
        public WorldGrid? Build(WorldExportResult result)
        {
            int cells = Width * Height;
            int baseHeight = int.MaxValue;
            int top = int.MinValue;
            for (int i = 0; i < cells; i++)
            {
                if (!HasGround[i])
                    continue;
                if (HeightCm[i] < baseHeight) baseHeight = HeightCm[i];
                if (HeightCm[i] > top) top = HeightCm[i];
            }
            if (baseHeight == int.MaxValue)
                baseHeight = 0;
            else if ((long)top - baseHeight > short.MaxValue)
            {
                result.Error(ExportCodes.HeightRange, string.Empty,
                    $"ground heights span {(long)top - baseHeight} cm, the format holds {short.MaxValue} cm per grid");
                return null;
            }

            var flags = new byte[cells];
            var heights = new short[cells];
            int walkable = 0;
            for (int i = 0; i < cells; i++)
            {
                if (IsWalkable(i))
                {
                    flags[i] = WorldGridFormat.FlagWalkable;
                    walkable++;
                }
                heights[i] = HasGround[i] ? (short)(HeightCm[i] - baseHeight) : (short)0;
            }
            WalkableCount = walkable;
            return new WorldGrid(OriginX, OriginY, Cell, Width, Height, baseHeight, flags, (byte[])Regions.Clone(), heights);
        }
    }
}
