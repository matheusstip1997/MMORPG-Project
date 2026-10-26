using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// Protocol 9: the observer's own build-up meters after its statuses in the snapshot header (spec 3.8: only the owner's,
/// one byte each; spec 9.3 <c>buildup_meters</c>): count 3, then net id 4 and points 7 per meter.
/// </summary>
public class OwnBuildUpSnapshotTests
{
    private static SnapshotHeader Header() => new()
    {
        ServerTick = 5_000,
        HasInputAck = true,
        LastInputSequence = 7,
        HasOwnResources = true,
        Own = new OwnResources { Hp = 10, MaxHp = 20, Mp = 1, MaxMp = 2, Stamina = 3, MaxStamina = 4 },
    };

    private static OwnStatus Status(ushort netId) => new() { NetId = netId, Stacks = 1, RemainingTicks = 80 };

    private static OwnBuildUp Meter(byte netId, byte points) => new() { NetId = netId, Points = points };

    private static byte[] Write(OwnStatus[] statuses, params OwnBuildUp[] meters)
    {
        var buffer = new byte[512];
        var writer = new SnapshotWriter(buffer, Header(), statuses, meters);
        Assert.True(writer.TryAddDespawn(77));
        Assert.True(writer.TryAddEntity(new EntitySnapshot { EntityId = 9, HpBucket = Snapshot.TopHpBucket }));
        return buffer.AsSpan(0, writer.Finish()).ToArray();
    }

    [Fact]
    public void Own_meters_round_trip_after_the_statuses()
    {
        byte[] packet = Write([Status(4), Status(2)], Meter(1, 37), Meter(15, 100));
        int bits = Snapshot.HeaderBits + Snapshot.OwnResourcesBits + Snapshot.OwnCountBits + 2 * Snapshot.OwnStatusBits
            + 2 * Snapshot.OwnBuildUpBits + Snapshot.DespawnBits + Snapshot.EntityBits;
        Assert.Equal((bits + 7) >> 3, packet.Length);

        Assert.True(SnapshotReader.TryBegin(packet, out var reader));
        Assert.Equal(2, reader.Header.OwnStatusCount);
        Assert.Equal(2, reader.Header.OwnBuildUpCount);
        Assert.True(reader.TryReadOwnStatus(out var status));
        Assert.Equal(4, status.NetId);
        // Reading a meter skips the statuses not read yet.
        Assert.True(reader.TryReadOwnBuildUp(out var first));
        Assert.Equal(Meter(1, 37), first);
        Assert.True(reader.TryReadOwnBuildUp(out var second));
        Assert.Equal(Meter(15, 100), second);
        Assert.False(reader.TryReadOwnBuildUp(out _));
        Assert.True(reader.TryReadDespawn(out uint despawn));
        Assert.Equal(77u, despawn);
        Assert.True(reader.TryReadEntity(out var entity));
        Assert.Equal(9u, entity.EntityId);
    }

    [Fact]
    public void No_meters_cost_only_the_count_and_despawns_skip_unread_meters()
    {
        byte[] packet = Write([], Meter(1, 5));
        Assert.True(SnapshotReader.TryBegin(packet, out var reader));
        Assert.True(reader.TryReadDespawn(out uint despawn)); // meters not read: skipped
        Assert.Equal(77u, despawn);

        byte[] empty = Write([]);
        Assert.True(SnapshotReader.TryBegin(empty, out var none));
        Assert.Equal(0, none.Header.OwnBuildUpCount);
        Assert.False(none.TryReadOwnBuildUp(out _));
        Assert.Equal(Snapshot.HeaderSizeWithOwnResources, Snapshot.HeaderSizeWithOwn(0, 0));
    }

    [Fact]
    public void The_writer_refuses_meters_the_layout_cannot_carry()
    {
        var buffer = new byte[512];
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header(), [], [Meter(0, 5)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header(), [], [Meter(16, 5)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header(), [], [Meter(1, 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header(), [], [Meter(1, 101)]));
        var tooMany = Enumerable.Range(1, Snapshot.MaxOwnBuildUps + 1).Select(i => Meter((byte)i, 1)).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header(), [], tooMany));
        var noResources = Header();
        noResources.HasOwnResources = false;
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, noResources, [], [Meter(1, 5)]));
        Assert.Throws<ArgumentException>(() =>
            new SnapshotWriter(new byte[Snapshot.HeaderSizeWithOwn(0, 1) - 1], Header(), [], [Meter(1, 5)]));
    }

    [Theory]
    [InlineData(0, 0u)]   // net id 0
    [InlineData(4, 0u)]   // 0 points
    [InlineData(4, 101u)] // above a full meter
    public void A_forged_meter_is_refused(int fieldOffset, uint value)
    {
        byte[] packet = Write([], Meter(1, 40));
        int start = Snapshot.HeaderBits + Snapshot.OwnResourcesBits + Snapshot.OwnCountBits;
        int bits = fieldOffset == 0 ? Snapshot.BuildUpIdBits : Snapshot.BuildUpPointsBits;
        Assert.True(SnapshotReader.TryBegin(packet, out _));
        PatchBits(packet, start + fieldOffset, value, bits);
        Assert.False(SnapshotReader.TryBegin(packet, out _));
    }

    [Fact]
    public void A_forged_meter_count_beyond_the_packet_is_refused()
    {
        byte[] packet = Write([], Meter(1, 40));
        PatchBits(packet, Snapshot.HeaderBits + Snapshot.OwnResourcesBits + Snapshot.OwnStatusCountBits, Snapshot.MaxOwnBuildUps,
            Snapshot.OwnBuildUpCountBits);
        Assert.False(SnapshotReader.TryBegin(packet, out _));
    }

    private static void PatchBits(byte[] buffer, int bitPosition, uint value, int bitCount)
    {
        for (int i = 0; i < bitCount; i++)
        {
            int bit = bitPosition + i;
            int mask = 1 << (bit & 7);
            buffer[bit >> 3] = (byte)(((value >> i) & 1) != 0 ? buffer[bit >> 3] | mask : buffer[bit >> 3] & ~mask);
        }
    }
}
