using System.Buffers.Binary;
using Shared.Simulation;
using Shared.Util;
using Shared.World;

namespace Shared.Tests.World;

/// <summary>grid.bin decoding (spec 11.6): exact round trips, and bad input is refused without throwing.</summary>
public class WorldGridReaderTests
{
    internal static WorldGrid SmallGrid(int width = 4, int height = 3)
    {
        int cells = width * height;
        var flags = new byte[cells];
        var regions = new byte[cells];
        var heights = new short[cells];
        for (int i = 0; i < cells; i++)
        {
            flags[i] = (byte)(i % 3 == 0 ? 0 : WorldGridFormat.FlagWalkable);
            regions[i] = (byte)(i % 2 + 1);
            heights[i] = (short)(i * 7 - 20);
        }
        return new WorldGrid(-200, 100, 50, width, height, 1_000, flags, regions, heights);
    }

    [Theory]
    [InlineData(WorldGridFormat.Version)]
    [InlineData(WorldGridFormat.RawVersion)]
    public void A_written_grid_reads_back_identical(int version)
    {
        var grid = SmallGrid();
        var bytes = WorldGridWriter.Write(grid, version);

        Assert.True(WorldGridReader.TryRead(bytes, out var read, out var error), error);

        // SmallGrid's cells all differ from their neighbours, so v2 has one run per cell.
        Assert.Equal(version == WorldGridFormat.RawVersion ? WorldGridFormat.RawFileBytes(4, 3, 1) : WorldGridFormat.RunFileBytes(12),
            bytes.Length);
        Assert.Equal((-200, 100, 50, 4, 3, 1_000), (read!.OriginXCm, read.OriginYCm, read.CellSizeCm, read.Width, read.Height, read.BaseHeightCm));
        Assert.Equal(2, read.MaxRegionIndex);
        Assert.Equal(bytes, WorldGridWriter.Write(read, version));
    }

    [Fact]
    public void Runs_merge_equal_cells_across_rows()
    {
        // WP-M2-7 (spec 11.6, grid v2): a 100 x 80 grid of one walkable region with a blocked column is 161 runs.
        int width = 100, height = 80, cells = width * height;
        var flags = new byte[cells];
        var regions = new byte[cells];
        for (int i = 0; i < cells; i++)
        {
            flags[i] = (byte)(i % width == 50 ? 0 : WorldGridFormat.FlagWalkable);
            regions[i] = 1;
        }
        var grid = new WorldGrid(0, 0, 50, width, height, 0, flags, regions, new short[cells]);

        var bytes = WorldGridWriter.Write(grid);

        Assert.Equal(WorldGridFormat.RunFileBytes(2 * height + 1), bytes.Length);
        Assert.True(WorldGridReader.TryRead(bytes, out var read, out var error), error);
        Assert.Equal(WorldGridWriter.Write(grid, WorldGridFormat.RawVersion), WorldGridWriter.Write(read!, WorldGridFormat.RawVersion));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(13u)]
    public void A_run_count_outside_the_cells_is_refused(uint runs)
    {
        var bytes = WorldGridWriter.Write(SmallGrid());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(WorldGridFormat.HeaderBytes), runs);

        Assert.False(WorldGridReader.TryRead(bytes, out _, out var error));
        Assert.Contains("run count", error);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void A_run_length_that_does_not_tile_the_cells_is_refused(uint length)
    {
        var bytes = WorldGridWriter.Write(SmallGrid());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(WorldGridFormat.HeaderBytes + WorldGridFormat.RunCountBytes), length);

        Assert.False(WorldGridReader.TryRead(bytes, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Writing_is_deterministic()
    {
        Assert.Equal(WorldGridWriter.Write(SmallGrid()), WorldGridWriter.Write(SmallGrid()));
    }

    [Fact]
    public void Every_truncation_is_refused()
    {
        var bytes = WorldGridWriter.Write(SmallGrid());
        for (int length = 0; length < bytes.Length; length++)
            Assert.False(WorldGridReader.TryRead(bytes.AsSpan(0, length), out _, out _), $"length {length}");
    }

    [Fact]
    public void Trailing_bytes_are_refused()
    {
        var bytes = WorldGridWriter.Write(SmallGrid());
        var longer = bytes.Concat(new byte[] { 0 }).ToArray();

        Assert.False(WorldGridReader.TryRead(longer, out _, out var error));
        Assert.Contains("header describes", error);
    }

    [Theory]
    [InlineData(0, 0x12345678u, "magic")]
    [InlineData(4, 3u, "format version")]
    [InlineData(4, 0u, "format version")]
    [InlineData(6, 0u, "layer count")]
    [InlineData(6, 2u, "layer count")]
    public void Header_fields_are_checked(int offset, uint value, string expected)
    {
        var bytes = WorldGridWriter.Write(SmallGrid());
        if (offset == 0)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)value);

        Assert.False(WorldGridReader.TryRead(bytes, out var grid, out var error));
        Assert.Null(grid);
        Assert.Contains(expected, error);
    }

    [Theory]
    [InlineData(16, 0)]                 // cell size
    [InlineData(16, -50)]
    [InlineData(20, 0)]                 // width
    [InlineData(24, -3)]                // height
    [InlineData(20, int.MaxValue)]      // cell count over the limit
    [InlineData(8, int.MinValue)]       // origin outside the simulation range
    [InlineData(12, 8_388_600)]         // far edge outside the simulation range
    public void Geometry_outside_the_format_limits_is_refused(int offset, int value)
    {
        var bytes = WorldGridWriter.Write(SmallGrid());
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);

        Assert.False(WorldGridReader.TryRead(bytes, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Unknown_flag_bits_are_refused()
    {
        var bytes = WorldGridWriter.Write(SmallGrid());
        bytes[WorldGridFormat.HeaderBytes + WorldGridFormat.RunCountBytes + 5 * WorldGridFormat.BytesPerRun + 4] = 0x80; // run 5's flags

        Assert.False(WorldGridReader.TryRead(bytes, out _, out var error));
        Assert.Contains("unknown flag", error);
    }

    [Fact]
    public void Random_corruption_never_throws_and_accepted_grids_stay_in_range()
    {
        var rng = new Pcg32Rng(seed: 20261017, stream: 11);
        var original = WorldGridWriter.Write(SmallGrid(6, 5));
        for (int round = 0; round < 5_000; round++)
        {
            var bytes = (byte[])original.Clone();
            int flips = rng.NextInt(1, 8);
            for (int i = 0; i < flips; i++)
                bytes[rng.NextInt(bytes.Length)] = (byte)rng.NextInt(256);
            int length = rng.RollPercent(20) ? rng.NextInt(bytes.Length + 1) : bytes.Length;

            if (!WorldGridReader.TryRead(bytes.AsSpan(0, length), out var grid, out var error))
            {
                Assert.NotNull(error);
                continue;
            }

            // Whatever was accepted can be queried anywhere without throwing.
            for (int probe = 0; probe < 16; probe++)
            {
                var position = new FixVec2(rng.NextInt(int.MinValue, int.MaxValue), rng.NextInt(int.MinValue, int.MaxValue));
                grid!.IsWalkable(position);
                grid.RegionIndexAt(position);
                grid.TryGetHeightCm(position, out _);
                GridCollision.Move(grid, position, new FixVec2(position.X / 2, position.Y / 2));
            }
        }
    }

    [Fact]
    public void Pure_random_bytes_are_refused_without_throwing()
    {
        var rng = new Pcg32Rng(seed: 7, stream: 3);
        for (int round = 0; round < 2_000; round++)
        {
            var bytes = new byte[rng.NextInt(0, 128)];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)rng.NextInt(256);
            Assert.False(WorldGridReader.TryRead(bytes, out _, out _));
        }
    }

    [Fact]
    public void The_constructor_checks_layer_lengths_and_geometry()
    {
        Assert.Throws<ArgumentException>(() => new WorldGrid(0, 0, 50, 2, 2, 0, new byte[3], new byte[4], new short[4]));
        Assert.Throws<ArgumentException>(() => new WorldGrid(0, 0, 0, 2, 2, 0, new byte[4], new byte[4], new short[4]));
    }
}
