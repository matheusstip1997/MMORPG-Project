namespace Shared.World
{
    /// <summary>
    /// Binary layout of <c>grid.bin</c>, the walkability, region and height layers exported by UnityExporter (spec 11.6).
    /// One definition for the exporter (<see cref="WorldGridWriter"/>), the server and client prediction
    /// (<see cref="WorldGridReader"/>). Little-endian; changing the layout bumps <see cref="Version"/>.
    /// <code>
    ///  0  4  magic "MMWG"
    ///  4  2  format version (u16)
    ///  6  2  layer count (u16), 1 in v1
    ///  8  4  origin X cm (i32): ground-plane X of the low edge of column 0
    /// 12  4  origin Y cm (i32): ground-plane Y of the low edge of row 0
    /// 16  4  cell size cm (i32)
    /// 20  4  width in cells (i32)
    /// 24  4  height in cells (i32)
    /// 28  4  base height cm (i32)
    /// 32     per layer, cells in row-major order (index = y * width + x):
    ///        flags u8[cells] (bit 0 walkable, other bits zero), region index u8[cells] (0 = none),
    ///        height offset i16[cells] (cm above the base height)
    /// </code>
    /// <para>
    /// Overlapping walkable surfaces (bridges, floors, caves; decision log 2026-10-16) are what the layer count is for.
    /// TODO(open): how an entity picks and changes layer (stairs, ramps, bridge ends) is not decided, so v1 files hold
    /// exactly one layer and the reader refuses more.
    /// </para>
    /// </summary>
    public static class WorldGridFormat
    {
        public const int Version = 1;

        /// <summary>"MMWG" read as a little-endian u32.</summary>
        public const uint Magic = 0x47574D4D;

        public const int HeaderBytes = 32;

        /// <summary>Layers a v1 file may hold (see the TODO on overlapping surfaces).</summary>
        public const int MaxLayers = 1;

        public const byte FlagWalkable = 1;

        /// <summary>Flag bits defined in v1; the reader refuses files that set any other bit.</summary>
        public const byte KnownFlags = FlagWalkable;

        /// <summary>Region index 0 means "no region"; indices 1..255 point into the world's region list.</summary>
        public const int MaxRegions = 255;

        /// <summary>
        /// Format size limit: 16 Mi cells (64 MiB per layer). A main-map grid larger than this is chunked
        /// (spec 11.6 "loads chunks lazily"), which is a later format version.
        /// </summary>
        public const int MaxCells = 1 << 24;

        /// <summary>
        /// Every cell edge must be a valid simulation coordinate: Q24.8 cm covers [-2^23, 2^23) cm
        /// (<see cref="Shared.Simulation.FixedPoint"/>).
        /// </summary>
        public const int MinCoordinateCm = -(1 << 23);
        public const int MaxCoordinateCm = (1 << 23) - 1;

        /// <summary>Bytes per cell per layer: flags, region index, height offset.</summary>
        public const int BytesPerCell = 4;

        public static long FileBytes(int width, int height, int layers) =>
            HeaderBytes + (long)width * height * BytesPerCell * layers;
    }
}
