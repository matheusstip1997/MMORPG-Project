using System;
using System.Buffers.Binary;

namespace Shared.World
{
    /// <summary>
    /// Encodes a <see cref="WorldGrid"/> as <c>grid.bin</c> (<see cref="WorldGridFormat"/>): v2 (run-length) by default,
    /// v1 (raw) where a test needs the old layout. Used by UnityExporter and by tests; the same grid always gives the
    /// same bytes.
    /// </summary>
    public static class WorldGridWriter
    {
        public static byte[] Write(WorldGrid grid) => Write(grid, WorldGridFormat.Version);

        /// <param name="version"><see cref="WorldGridFormat.Version"/> or <see cref="WorldGridFormat.RawVersion"/>.</param>
        public static byte[] Write(WorldGrid grid, int version)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            if (version == WorldGridFormat.RawVersion)
                return WriteRaw(grid);
            if (version != WorldGridFormat.Version)
                throw new ArgumentOutOfRangeException(nameof(version), version, "not a grid format version");

            int cells = grid.CellCount;
            int runs = 0;
            for (int i = 0; i < cells; i++)
            {
                if (i == 0 || !SameCell(grid, i, i - 1))
                    runs++;
            }
            var data = new byte[WorldGridFormat.RunFileBytes(runs)];
            var span = data.AsSpan();
            WriteHeader(span, grid, WorldGridFormat.Version);
            int offset = WorldGridFormat.HeaderBytes;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset), (uint)runs);
            offset += WorldGridFormat.RunCountBytes;
            int start = 0;
            for (int i = 1; i <= cells; i++)
            {
                if (i < cells && SameCell(grid, i, start))
                    continue;
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset), (uint)(i - start));
                data[offset + 4] = grid.FlagsAt(start);
                data[offset + 5] = grid.RegionAt(start);
                BinaryPrimitives.WriteInt16LittleEndian(span.Slice(offset + 6), grid.HeightOffsetAt(start));
                offset += WorldGridFormat.BytesPerRun;
                start = i;
            }
            return data;
        }

        private static bool SameCell(WorldGrid grid, int a, int b) =>
            grid.FlagsAt(a) == grid.FlagsAt(b) && grid.RegionAt(a) == grid.RegionAt(b)
            && grid.HeightOffsetAt(a) == grid.HeightOffsetAt(b);

        private static byte[] WriteRaw(WorldGrid grid)
        {
            int cells = grid.CellCount;
            var data = new byte[WorldGridFormat.RawFileBytes(grid.Width, grid.Height, layers: 1)];
            var span = data.AsSpan();
            WriteHeader(span, grid, WorldGridFormat.RawVersion);

            int offset = WorldGridFormat.HeaderBytes;
            for (int i = 0; i < cells; i++)
                data[offset + i] = grid.FlagsAt(i);
            offset += cells;
            for (int i = 0; i < cells; i++)
                data[offset + i] = grid.RegionAt(i);
            offset += cells;
            for (int i = 0; i < cells; i++, offset += 2)
                BinaryPrimitives.WriteInt16LittleEndian(span.Slice(offset), grid.HeightOffsetAt(i));
            return data;
        }

        private static void WriteHeader(Span<byte> span, WorldGrid grid, int version)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span, WorldGridFormat.Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4), (ushort)version);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6), 1);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), grid.OriginXCm);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12), grid.OriginYCm);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16), grid.CellSizeCm);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(20), grid.Width);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24), grid.Height);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28), grid.BaseHeightCm);
        }
    }
}
