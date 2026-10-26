using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// Protocol 7: the observer's own statuses after its resources in the snapshot header (spec 3.7 buff row, 1.7): net id 12,
/// stacks 4, ticks left 16, at most 15.
/// </summary>
public class OwnStatusSnapshotTests
{
    private static SnapshotHeader Header() => new()
    {
        ServerTick = 5_000,
        HasInputAck = true,
        LastInputSequence = 7,
        HasOwnResources = true,
        Own = new OwnResources { Hp = 10, MaxHp = 20, Mp = 1, MaxMp = 2, Stamina = 3, MaxStamina = 4 },
    };

    private static EntitySnapshot Self() => new() { EntityId = 9, HpBucket = Snapshot.TopHpBucket };

    private static OwnStatus Status(ushort netId, byte stacks = 1, ushort remaining = 300) =>
        new() { NetId = netId, Stacks = stacks, RemainingTicks = remaining };

    private static byte[] Write(params OwnStatus[] statuses)
    {
        var buffer = new byte[512];
        var writer = new SnapshotWriter(buffer, Header(), statuses);
        Assert.True(writer.TryAddDespawn(77));
        Assert.True(writer.TryAddEntity(Self()));
        return buffer.AsSpan(0, writer.Finish()).ToArray();
    }

    [Fact]
    public void Own_statuses_round_trip_in_order_before_the_despawns()
    {
        OwnStatus[] statuses =
        [
            Status(1), Status(4095, 15, ushort.MaxValue), Status(5, 2, 1),
        ];
        byte[] packet = Write(statuses);

        int bits = Snapshot.HeaderBits + Snapshot.OwnResourcesBits + Snapshot.OwnCountBits + 3 * Snapshot.OwnStatusBits
            + Snapshot.DespawnBits + Snapshot.EntityBits;
        Assert.Equal((bits + 7) >> 3, packet.Length);
        Assert.True(SnapshotReader.TryBegin(packet, out var reader));
        Assert.Equal(3, reader.Header.OwnStatusCount);
        var read = new List<OwnStatus>();
        while (reader.TryReadOwnStatus(out var status))
            read.Add(status);
        Assert.Equal(statuses, read);
        Assert.True(reader.TryReadDespawn(out uint despawn));
        Assert.Equal(77u, despawn);
        Assert.True(reader.TryReadEntity(out var self));
        Assert.Equal(9u, self.EntityId);
    }

    [Fact]
    public void Unread_statuses_are_skipped_by_the_despawn_reader()
    {
        Assert.True(SnapshotReader.TryBegin(Write(Status(1), Status(2)), out var reader));
        Assert.True(reader.TryReadDespawn(out uint despawn));
        Assert.Equal(77u, despawn);
        Assert.False(reader.TryReadOwnStatus(out _));
    }

    [Fact]
    public void A_header_with_resources_and_no_status_reads_zero_statuses()
    {
        Assert.True(SnapshotReader.TryBegin(Write(), out var reader));
        Assert.Equal(0, reader.Header.OwnStatusCount);
        Assert.False(reader.TryReadOwnStatus(out _));
    }

    [Fact]
    public void The_writer_refuses_statuses_it_cannot_carry()
    {
        var buffer = new byte[512];
        var tooMany = Enumerable.Range(1, Snapshot.MaxOwnStatuses + 1).Select(i => Status((ushort)i)).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header(), tooMany));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header(), [Status(0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header(), [Status(4096)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header(), [Status(1, stacks: 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header(), [Status(1, stacks: 16)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header(), [Status(1, remaining: 0)]));
        var noResources = Header();
        noResources.HasOwnResources = false;
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, noResources, [Status(1)]));
        Assert.Throws<ArgumentException>(() =>
            new SnapshotWriter(new byte[Snapshot.HeaderSizeWithOwnStatuses(1) - 1], Header(), [Status(1)]));
        Assert.Equal(Snapshot.HeaderSizeWithOwnStatuses(1),
            new SnapshotWriter(new byte[Snapshot.HeaderSizeWithOwnStatuses(1)], Header(), [Status(1)]).Finish());
    }

    [Theory]
    [InlineData(0, 0)]  // net id 0
    [InlineData(12, 0)] // stacks 0
    [InlineData(16, 0)] // no ticks left
    public void A_forged_status_is_refused(int fieldOffset, uint value)
    {
        byte[] packet = Write(Status(3, 2, 40));
        int start = Snapshot.HeaderBits + Snapshot.OwnResourcesBits + Snapshot.OwnStatusCountBits;
        int bits = fieldOffset switch { 0 => Snapshot.StatusIdBits, 12 => Snapshot.StatusStacksBits, _ => Snapshot.StatusRemainingBits };
        Assert.True(SnapshotReader.TryBegin(packet, out _));
        PatchBits(packet, start + fieldOffset, value, bits);
        Assert.False(SnapshotReader.TryBegin(packet, out _));
    }

    [Fact]
    public void A_forged_status_count_beyond_the_packet_is_refused()
    {
        byte[] packet = Write(Status(3));
        PatchBits(packet, Snapshot.HeaderBits + Snapshot.OwnResourcesBits, Snapshot.MaxOwnStatuses, Snapshot.OwnStatusCountBits);
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
