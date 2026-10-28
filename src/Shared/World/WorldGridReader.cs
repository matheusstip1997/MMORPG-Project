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
            if (version != WorldGridFormat.Version && version != WorldGridFormat.RawVersion)
            {
                error = $"format version {version}, this build reads {WorldGridFormat.RawVersion} and {WorldGridFormat.Version}";
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

            int cells = width * height;
            byte[] flags, regions;
            short[] heights;
            if (version == WorldGridFormat.RawVersion)
            {
                if (!TryReadRaw(data, width, height, layers, out flags, out regions, out heights, out error))
                    return false;
            }
            else if (!TryReadRuns(data, cells, out flags, out regions, out heights, out error))
                return false;

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

        private static bool TryReadRaw(ReadOnlySpan<byte> data, int width, int height, int layers,
            out byte[] flags, out byte[] regions, out short[] heights, out string? error)
        {
            flags = regions = Array.Empty<byte>();
            heights = Array.Empty<short>();
            long expected = WorldGridFormat.RawFileBytes(width, height, layers);
            if (data.Length != expected)
            {
                error = $"file is {data.Length} bytes, the header describes {expected}";
                return false;
            }

            int cells = width * height;
            flags = new byte[cells];
            regions = new byte[cells];
            heights = new short[cells];
            int offset = WorldGridFormat.HeaderBytes;
            data.Slice(offset, cells).CopyTo(flags);
            offset += cells;
            data.Slice(offset, cells).CopyTo(regions);
            offset += cells;
            for (int i = 0; i < cells; i++, offset += 2)
                heights[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset));
            error = null;
            return true;
        }

        /// <summary>v2: the run count, then runs that cover exactly the cells; the file size must match the run count.</summary>
        private static bool TryReadRuns(ReadOnlySpan<byte> data, int cells,
            out byte[] flags, out byte[] regions, out short[] heights, out string? error)
        {
            flags = regions = Array.Empty<byte>();
            heights = Array.Empty<short>();
            if (data.Length < WorldGridFormat.HeaderBytes + WorldGridFormat.RunCountBytes)
            {
                error = $"file is {data.Length} bytes, too short for the run count";
                return false;
            }
            long runs = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(WorldGridFormat.HeaderBytes));
            if (runs < 1 || runs > cells)
            {
                error = $"run count {runs} must be between 1 and the cell count {cells}";
                return false;
            }
            long expected = WorldGridFormat.RunFileBytes(runs);
            if (data.Length != expected)
            {
                error = $"file is {data.Length} bytes, the header describes {expected}";
                return false;
            }

            // The lengths first, so a corrupt header never allocates layers its runs do not fill.
            int first = WorldGridFormat.HeaderBytes + WorldGridFormat.RunCountBytes;
            long covered = 0;
            for (int run = 0, at = first; run < runs; run++, at += WorldGridFormat.BytesPerRun)
            {
                long length = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at));
                if (length < 1 || covered + length > cells)
                {
                    error = $"run {run} of length {length} at cell {covered} leaves the {cells} cells";
                    return false;
                }
                covered += length;
            }
            if (covered != cells)
            {
                error = $"runs cover {covered} cells, the grid has {cells}";
                return false;
            }

            flags = new byte[cells];
            regions = new byte[cells];
            heights = new short[cells];
            int offset = first;
            long cell = 0;
            for (long run = 0; run < runs; run++, offset += WorldGridFormat.BytesPerRun)
            {
                long length = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset));
                byte runFlags = data[offset + 4];
                byte region = data[offset + 5];
                short heightOffset = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset + 6));
                int end = (int)(cell + length);
                for (int i = (int)cell; i < end; i++)
                {
                    flags[i] = runFlags;
                    regions[i] = region;
                    heights[i] = heightOffset;
                }
                cell = end;
            }
            error = null;
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
