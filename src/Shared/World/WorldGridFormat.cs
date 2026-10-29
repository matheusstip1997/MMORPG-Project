namespace Shared.World
{
    /// <summary>
    /// Binary layout of <c>grid.bin</c>, the walkability, region and height layers exported by UnityExporter (spec 11.6).
    /// One definition for the exporter (<see cref="WorldGridWriter"/>), the server and client prediction
    /// (<see cref="WorldGridReader"/>). Little-endian; changing the layout bumps <see cref="Version"/>.
    /// <code>
    ///  0  4  magic "MMWG"
    ///  4  2  format version (u16): 2 (written), 1 (still read)
    ///  6  2  layer count (u16), 1
    ///  8  4  origin X cm (i32): ground-plane X of the low edge of column 0
    /// 12  4  origin Y cm (i32): ground-plane Y of the low edge of row 0
    /// 16  4  cell size cm (i32)
    /// 20  4  width in cells (i32)
    /// 24  4  height in cells (i32)
    /// 28  4  base height cm (i32)
    /// 32     per layer, cells in row-major order (index = y * width + x), each cell a flags u8 (bit 0 walkable, other
    ///        bits zero), a region index u8 (0 = none) and a height offset i16 (cm above the base height):
    ///        v1: flags u8[cells], region index u8[cells], height offset i16[cells];
    ///        v2 (run-length, WP-M2-7): run count u32, then per run: length u32 (1 or more), flags u8, region u8,
    ///        height offset i16. The runs cover the cells in order and their lengths add up to the cell count.
    /// </code>
    /// <para>
    /// v2 exists for size (spec 11.6, exporter v1): the field zone's grid of 704,000 cells is mostly long runs of equal
    /// cells, so it commits as tens of KB instead of 2.8 MB. Runs may span rows.
    /// </para>
    /// <para>
    /// Overlapping walkable surfaces (bridges, floors, caves; decision log 2026-10-16) are what the layer count is for.
    /// TODO(open): how an entity picks and changes layer (stairs, ramps, bridge ends) is not decided, so files hold
    /// exactly one layer and the reader refuses more.
    /// </para>
    /// </summary>
    public static class WorldGridFormat
    {
        /// <summary>The version the writer produces (run-length).</summary>
        public const int Version = 2;

        /// <summary>The raw layout (one array per field), still read.</summary>
        public const int RawVersion = 1;

        /// <summary>"MMWG" read as a little-endian u32.</summary>
        public const uint Magic = 0x47574D4D;

        public const int HeaderBytes = 32;

        /// <summary>Layers a file may hold (see the TODO on overlapping surfaces).</summary>
        public const int MaxLayers = 1;

        public const byte FlagWalkable = 1;

        /// <summary>Flag bits defined; the reader refuses files that set any other bit.</summary>
        public const byte KnownFlags = FlagWalkable;

        /// <summary>Region index 0 means "no region"; indices 1..255 point into the world's region list.</summary>
        public const int MaxRegions = 255;

        /// <summary>
        /// Format size limit: 16 Mi cells (64 MiB per decoded layer). A main-map grid larger than this is chunked
        /// (spec 11.6 "loads chunks lazily"), which is a later format version.
        /// </summary>
        public const int MaxCells = 1 << 24;

        /// <summary>
        /// Every cell edge must be a valid simulation coordinate: Q24.8 cm covers [-2^23, 2^23) cm
        /// (<see cref="Shared.Simulation.FixedPoint"/>).
        /// </summary>
        public const int MinCoordinateCm = -(1 << 23);
        public const int MaxCoordinateCm = (1 << 23) - 1;

        /// <summary>v1: bytes per cell per layer (flags, region index, height offset).</summary>
        public const int BytesPerCell = 4;

        /// <summary>v2: the run count before each layer's runs.</summary>
        public const int RunCountBytes = 4;

        /// <summary>v2: bytes per run (length u32, flags u8, region u8, height offset i16).</summary>
        public const int BytesPerRun = 8;

        /// <summary>Size of a v1 (raw) file.</summary>
        public static long RawFileBytes(int width, int height, int layers) =>
            HeaderBytes + (long)width * height * BytesPerCell * layers;

        /// <summary>Size of a one-layer v2 file with <paramref name="runs"/> runs.</summary>
        public static long RunFileBytes(long runs) => HeaderBytes + RunCountBytes + runs * BytesPerRun;
    }
}
