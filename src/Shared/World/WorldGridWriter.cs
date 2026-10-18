using System;
using System.Buffers.Binary;

namespace Shared.World
{
    /// <summary>
    /// Encodes a <see cref="WorldGrid"/> as <c>grid.bin</c> (<see cref="WorldGridFormat"/>). Used by UnityExporter and by
    /// tests; the same grid always gives the same bytes.
    /// </summary>
    public static class WorldGridWriter
    {
        public static byte[] Write(WorldGrid grid)
        {
            if (grid == null)
                throw new ArgumentNullException(nameof(grid));
            int cells = grid.CellCount;
            var data = new byte[WorldGridFormat.FileBytes(grid.Width, grid.Height, layers: 1)];
            var span = data.AsSpan();

            BinaryPrimitives.WriteUInt32LittleEndian(span, WorldGridFormat.Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4), WorldGridFormat.Version);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6), 1);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), grid.OriginXCm);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12), grid.OriginYCm);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16), grid.CellSizeCm);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(20), grid.Width);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24), grid.Height);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28), grid.BaseHeightCm);

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
    }
}
