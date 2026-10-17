using System;
using System.Buffers.Binary;

namespace Shared.World
{
    /// <summary>
    /// Decodes <c>grid.bin</c> (<see cref="WorldGridFormat"/>). Every length and field is checked before use, the exact
    /// file size must match the header, and bad input returns an error message instead of throwing.
    /// </summary>
    public static class WorldGridReader
    {
        public static bool TryRead(ReadOnlySpan<byte> data, out WorldGrid? grid, out string? error)
        {
            grid = null;
            if (data.Length < WorldGridFormat.HeaderBytes)
            {
                error = $"file is {data.Length} bytes, shorter than the {WorldGridFormat.HeaderBytes}-byte header";
                return false;
            }

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (magic != WorldGridFormat.Magic)
            {
                error = "not a world grid file (bad magic)";
                return false;
            }

            int version = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4));
            if (version != WorldGridFormat.Version)
            {
                error = $"format version {version}, this build reads {WorldGridFormat.Version}";
                return false;
            }

            int layers = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6));
            if (layers < 1 || layers > WorldGridFormat.MaxLayers)
            {
                error = $"layer count {layers}, v{WorldGridFormat.Version} supports 1 to {WorldGridFormat.MaxLayers}";
                return false;
            }

            int originX = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8));
            int originY = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(12));
            int cellSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(16));
            int width = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(20));
            int height = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(24));
            int baseHeight = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(28));

            error = CheckGeometry(originX, originY, cellSize, width, height);
            if (error != null)
                return false;

            long expected = WorldGridFormat.FileBytes(width, height, layers);
            if (data.Length != expected)
            {
                error = $"file is {data.Length} bytes, the header describes {expected}";
                return false;
            }

            int cells = width * height;
            var flags = new byte[cells];
            var regions = new byte[cells];
            var heights = new short[cells];

            int offset = WorldGridFormat.HeaderBytes;
            data.Slice(offset, cells).CopyTo(flags);
            offset += cells;
            data.Slice(offset, cells).CopyTo(regions);
            offset += cells;
            for (int i = 0; i < cells; i++, offset += 2)
                heights[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset));

            for (int i = 0; i < cells; i++)
            {
                if ((flags[i] & ~WorldGridFormat.KnownFlags) != 0)
                {
                    error = $"cell {i} sets unknown flag bits 0x{flags[i]:X2}";
                    return false;
                }
            }

            long minHeight = (long)baseHeight + short.MinValue;
            long maxHeight = (long)baseHeight + short.MaxValue;
            if (minHeight < int.MinValue || maxHeight > int.MaxValue)
            {
                error = $"base height {baseHeight} cm leaves no room for the height offsets";
                return false;
            }

            grid = new WorldGrid(originX, originY, cellSize, width, height, baseHeight, flags, regions, heights);
            return true;
        }

        /// <summary>Null when the geometry is valid; otherwise why not. Shared with <see cref="WorldGrid"/>'s constructor.</summary>
        internal static string? CheckGeometry(int originX, int originY, int cellSize, int width, int height)
        {
            if (cellSize <= 0)
                return $"cell size {cellSize} cm must be positive";
            if (width <= 0 || height <= 0)
                return $"grid size {width} x {height} must be positive";
            if ((long)width * height > WorldGridFormat.MaxCells)
                return $"grid has {(long)width * height} cells, the format limit is {WorldGridFormat.MaxCells}";
            if (!EdgesFit(originX, cellSize, width) || !EdgesFit(originY, cellSize, height))
                return $"grid edges leave the simulation range [{WorldGridFormat.MinCoordinateCm}, {WorldGridFormat.MaxCoordinateCm}] cm";
            return null;
        }

        private static bool EdgesFit(int origin, int cellSize, int count) =>
            origin >= WorldGridFormat.MinCoordinateCm &&
            (long)origin + (long)cellSize * count <= WorldGridFormat.MaxCoordinateCm;
    }
}
