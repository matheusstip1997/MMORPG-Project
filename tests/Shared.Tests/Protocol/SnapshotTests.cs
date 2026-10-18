using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Shared.Tests.Protocol;

public class SnapshotTests
{
    private static readonly SnapshotHeader Header = new()
    {
        ServerTick = 123_456,
        HasInputAck = true,
        LastInputSequence = 65_000,
        OriginXCm = -2_000_000,
        OriginYCm = 10_000,
        OriginZCm = 3_000_000,
    };

    /// <summary>Entities cycle through every state code (D2): idle, the four action phases, flinched, staggered, dead.</summary>
    private static EntitySnapshot Entity(uint id)
    {
        byte state = (byte)(id % 8);
        int extra = Snapshot.ExtraStateBits(state);
        return new EntitySnapshot
        {
            EntityId = id,
            XCm = Header.OriginXCm + (int)id * 37,
            YCm = Header.OriginYCm - (int)id,
            ZCm = Header.OriginZCm - (int)id * 53,
            Facing = Quantize.RoundTripAngle((ushort)(id * 977), Snapshot.FacingBits),
            State = state,
            ActionId = extra == Snapshot.ActionStateBits ? (ushort)(1 + id * 13 % 4095) : (ushort)0,
            StateAge = extra > 0 ? (byte)(id * 7) : (byte)0,
            StateDurationTicks = extra == Snapshot.InterruptStateBits ? (byte)(1 + id % 200) : (byte)0,
            HpBucket = (byte)(id % (Snapshot.TopHpBucket + 1)),
            Flags = (EntityFlags)(id % 32),
        };
    }

    /// <summary>Size of a snapshot built by <see cref="Build"/>: the idle size plus each entity's action fields.</summary>
    private static int BuiltSize(int despawns, int spawns, int entities)
    {
        long bits = Snapshot.HeaderBits + (long)despawns * Snapshot.DespawnBits + (long)spawns * Snapshot.SpawnBits;
        for (uint i = 0; i < entities; i++)
            bits += Snapshot.EntityBits + Snapshot.ExtraStateBits(Entity(i).State);
        return (int)((bits + 7) >> 3);
    }

    private static byte[] Build(int despawns, int spawns, int entities, int bufferSize = 4096)
    {
        var buffer = new byte[bufferSize];
        var writer = new SnapshotWriter(buffer, Header);
        for (uint i = 0; i < despawns; i++)
            Assert.True(writer.TryAddDespawn(1000 + i));
        for (uint i = 0; i < spawns; i++)
            Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = i, Kind = (EntityKind)(1 + i % 8), DefId = (ushort)(i * 11) }));
        for (uint i = 0; i < entities; i++)
            Assert.True(writer.TryAddEntity(Entity(i)));
        int length = writer.Finish();
        return buffer.AsSpan(0, length).ToArray();
    }

    [Fact]
    public void Snapshot_round_trips()
    {
        byte[] packet = Build(despawns: 3, spawns: 4, entities: 20);
        Assert.Equal(BuiltSize(3, 4, 20), packet.Length);
        Assert.True(packet.Length > Snapshot.SizeFor(3, 4, 20));

        Assert.True(SnapshotReader.TryBegin(packet, out var reader));
        Assert.Equal(Header, reader.Header);
        Assert.Equal((3, 4, 20), (reader.DespawnCount, reader.SpawnCount, reader.EntityCount));

        for (uint i = 0; i < 3; i++)
        {
            Assert.True(reader.TryReadDespawn(out uint id));
            Assert.Equal(1000 + i, id);
        }
        Assert.False(reader.TryReadDespawn(out _));
        for (uint i = 0; i < 4; i++)
        {
            Assert.True(reader.TryReadSpawn(out var spawn));
            Assert.Equal(new SpawnEvent { EntityId = i, Kind = (EntityKind)(1 + i % 8), DefId = (ushort)(i * 11) }, spawn);
        }
        Assert.False(reader.TryReadSpawn(out _));
        for (uint i = 0; i < 20; i++)
        {
            Assert.True(reader.TryReadEntity(out var entity));
            Assert.Equal(Entity(i), entity);
        }
        Assert.False(reader.TryReadEntity(out _));
    }

    [Fact]
    public void Empty_snapshot_round_trips()
    {
        byte[] packet = Build(0, 0, 0);
        Assert.Equal(Snapshot.HeaderSize, packet.Length);
        Assert.True(SnapshotReader.TryBegin(packet, out var reader));
        Assert.False(reader.TryReadEntity(out _));
    }

    [Fact]
    public void Reading_entities_first_skips_the_events()
    {
        byte[] packet = Build(2, 2, 1);
        Assert.True(SnapshotReader.TryBegin(packet, out var reader));
        Assert.True(reader.TryReadEntity(out var entity));
        Assert.Equal(Entity(0), entity);
    }

    [Fact]
    public void Boundary_entity_values_round_trip()
    {
        var header = new SnapshotHeader { OriginXCm = int.MaxValue, OriginYCm = int.MinValue, OriginZCm = 0 };
        var entity = new EntitySnapshot
        {
            EntityId = uint.MaxValue,
            XCm = int.MaxValue - (1 << 20),
            YCm = int.MinValue + (1 << 17) - 1,
            ZCm = (1 << 20) - 1,
            Facing = Quantize.RoundTripAngle(65000, Snapshot.FacingBits),
            State = (byte)ActionState.Recovery,
            ActionId = 4095,
            StateAge = byte.MaxValue,
            HpBucket = Snapshot.TopHpBucket,
            Flags = EntityFlags.Armed | EntityFlags.RedName | EntityFlags.Disguised | EntityFlags.Mounted | EntityFlags.Pet,
        };
        var staggered = entity with { State = (byte)ActionState.Staggered, ActionId = 0, StateDurationTicks = byte.MaxValue };
        var buffer = new byte[128];
        var writer = new SnapshotWriter(buffer, header);
        Assert.True(writer.TryAddSpawn(new SpawnEvent { EntityId = uint.MaxValue, Kind = EntityKind.ShopStall, DefId = ushort.MaxValue }));
        Assert.True(writer.TryAddEntity(entity));
        Assert.True(writer.TryAddEntity(staggered));
        int length = writer.Finish();

        Assert.True(SnapshotReader.TryBegin(buffer.AsSpan(0, length), out var reader));
        Assert.Equal(header, reader.Header);
        Assert.True(reader.TryReadSpawn(out var spawn));
        Assert.Equal(ushort.MaxValue, spawn.DefId);
        Assert.True(reader.TryReadEntity(out var read));
        Assert.Equal(entity, read);
        Assert.True(reader.TryReadEntity(out read));
        Assert.Equal(staggered, read);
    }

    [Fact]
    public void Action_fields_travel_only_with_the_states_that_use_them()
    {
        // Values outside a state's fields are not sent: they read back as 0.
        var idle = Entity(0) with { ActionId = 5, StateAge = 9, StateDurationTicks = 3 };
        var active = Entity(3) with { StateDurationTicks = 3 };
        var buffer = new byte[128];
        var writer = new SnapshotWriter(buffer, Header);
        Assert.True(writer.TryAddEntity(idle));
        Assert.True(writer.TryAddEntity(active));
        int length = writer.Finish();
        Assert.Equal((Snapshot.HeaderBits + 2 * Snapshot.EntityBits + Snapshot.ActionStateBits + 7) >> 3, length);

        Assert.True(SnapshotReader.TryBegin(buffer.AsSpan(0, length), out var reader));
        Assert.True(reader.TryReadEntity(out var read));
        Assert.Equal(idle with { ActionId = 0, StateAge = 0, StateDurationTicks = 0 }, read);
        Assert.True(reader.TryReadEntity(out read));
        Assert.Equal(active with { StateDurationTicks = 0 }, read);
    }

    [Fact]
    public void Forged_action_fields_are_refused()
    {
        byte[] packet = Build(0, 0, 2); // entity 1 is in Startup
        int stateBit = Snapshot.HeaderBits + Snapshot.EntityBits + Snapshot.EntityBits - Snapshot.HpBucketBits
            - Snapshot.FlagsBits - Snapshot.StateBits;
        // Action id 0 in an action state.
        byte[] noAction = [.. packet];
        PatchBits(noAction, stateBit + Snapshot.StateBits, 0, Snapshot.ActionIdBits);
        Assert.False(SnapshotReader.TryBegin(noAction, out _));
        // Unknown state codes.
        byte[] badState = [.. packet];
        PatchBits(badState, stateBit, 8, Snapshot.StateBits);
        Assert.False(SnapshotReader.TryBegin(badState, out _));
    }

    [Fact]
    public void Writer_refuses_values_it_cannot_encode()
    {
        var buffer = new byte[256];
        Assert.Throws<ArgumentException>(() => new SnapshotWriter(new byte[Snapshot.HeaderSize - 1], Header));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header).TryAddEntity(Entity(1) with { XCm = Header.OriginXCm + (1 << 20) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header).TryAddEntity(Entity(1) with { YCm = Header.OriginYCm - (1 << 17) - 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header).TryAddEntity(Entity(1) with { HpBucket = 64 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header).TryAddEntity(Entity(1) with { Flags = (EntityFlags)32 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header).TryAddEntity(Entity(1) with { State = 8 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header).TryAddEntity(Entity(1) with { ActionId = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header).TryAddEntity(Entity(1) with { ActionId = 4096 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header).TryAddEntity(Entity(5) with { StateDurationTicks = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(buffer, Header).TryAddSpawn(new SpawnEvent { Kind = EntityKind.None }));
        Assert.Throws<InvalidOperationException>(() =>
        {
            var writer = new SnapshotWriter(buffer, Header);
            writer.TryAddEntity(Entity(1));
            writer.TryAddDespawn(1);
        });
        Assert.False(Snapshot.FitsOrigin(Header, Entity(1) with { ZCm = Header.OriginZCm + (1 << 20) }));
        Assert.True(Snapshot.FitsOrigin(Header, Entity(1)));
    }

    [Fact]
    public void A_full_buffer_keeps_what_fits_and_stays_valid()
    {
        const int mtu = 1200;
        var buffer = new byte[mtu];
        var writer = new SnapshotWriter(buffer, Header);
        Assert.True(writer.TryAddDespawn(5));
        uint added = 0;
        while (writer.TryAddEntity(Entity(added)))
            added++;
        Assert.False(writer.TryAddEntity(Entity(0)));
        int length = writer.Finish();

        Assert.Equal(BuiltSize(1, 0, (int)added), length);
        Assert.True(length <= mtu);
        Assert.True(BuiltSize(1, 0, (int)added + 1) > mtu);
        Assert.True(SnapshotReader.TryBegin(buffer.AsSpan(0, length), out var reader));
        Assert.Equal((int)added, reader.EntityCount);
    }

    [Fact]
    public void Count_cap_is_enforced()
    {
        var buffer = new byte[Snapshot.SizeFor(Snapshot.MaxCount + 1, 0, 0)];
        var writer = new SnapshotWriter(buffer, Header);
        for (int i = 0; i < Snapshot.MaxCount; i++)
            Assert.True(writer.TryAddDespawn((uint)i));
        Assert.False(writer.TryAddDespawn(0));
        int length = writer.Finish();
        Assert.True(SnapshotReader.TryBegin(buffer.AsSpan(0, length), out var reader));
        Assert.Equal(Snapshot.MaxCount, reader.DespawnCount);
    }

    [Fact]
    public void Every_truncation_and_extension_is_refused()
    {
        byte[] packet = Build(2, 2, 3);
        for (int length = 0; length < packet.Length; length++)
            Assert.False(SnapshotReader.TryBegin(packet.AsSpan(0, length), out _));
        Assert.False(SnapshotReader.TryBegin([.. packet, 0], out _));
    }

    [Fact]
    public void Malicious_snapshots_are_refused_as_a_whole()
    {
        byte[] packet = Build(0, 2, 1);

        byte[] wrongId = [.. packet];
        wrongId[0] = (byte)HotMessageId.Input;
        Assert.False(SnapshotReader.TryBegin(wrongId, out _));

        // The second spawn's kind sits after the header and the first spawn.
        byte[] badKind = [.. packet];
        int kindBit = Snapshot.HeaderBits + Snapshot.SpawnBits + Snapshot.EntityIdBits;
        PatchBits(badKind, kindBit, 0, Snapshot.KindBits);
        Assert.False(SnapshotReader.TryBegin(badKind, out _));
        PatchBits(badKind, kindBit, 15, Snapshot.KindBits);
        Assert.False(SnapshotReader.TryBegin(badKind, out _));

        // Counts that promise more entries than the bytes hold.
        byte[] bigCount = [.. packet];
        PatchBits(bigCount, Snapshot.HeaderBits - Snapshot.CountBits, Snapshot.MaxCount, Snapshot.CountBits);
        Assert.False(SnapshotReader.TryBegin(bigCount, out _));
    }

    [Fact]
    public void Positions_that_leave_the_int_range_are_refused()
    {
        var buffer = new byte[Snapshot.SizeFor(0, 0, 1)];
        var writer = new BitWriter(buffer);
        writer.WriteBits((uint)HotMessageId.Snapshot, 8);
        writer.WriteBits(0, 32);
        writer.WriteBits(0, 1);
        writer.WriteBits(0, 16);
        writer.WriteSigned(int.MaxValue, 32);
        writer.WriteSigned(0, 32);
        writer.WriteSigned(0, 32);
        writer.WriteBits(0, Snapshot.CountBits);
        writer.WriteBits(0, Snapshot.CountBits);
        writer.WriteBits(1, Snapshot.CountBits);
        writer.WriteBits(0, 1); // no own resources
        writer.WriteBits(1, Snapshot.EntityIdBits);
        writer.WriteSigned(1, Snapshot.HorizontalOffsetBits);
        for (int left = Snapshot.EntityBits - Snapshot.EntityIdBits - Snapshot.HorizontalOffsetBits; left > 0; left -= 32)
            writer.WriteBits(0, Math.Min(left, 32));
        writer.Finish();

        Assert.False(SnapshotReader.TryBegin(buffer, out _));
    }

    private static readonly OwnResources Own = new()
    {
        Hp = 75, MaxHp = 150, Mp = 0, MaxMp = 30, Stamina = 100, MaxStamina = 100,
    };

    [Fact]
    public void The_observers_own_resources_round_trip_in_the_header()
    {
        var header = Header;
        header.HasOwnResources = true;
        header.Own = Own;
        header.Own.MaxHp = int.MaxValue; // the largest value the 31 bits carry
        header.Own.Hp = int.MaxValue - 1;
        var buffer = new byte[256];
        var writer = new SnapshotWriter(buffer, header);
        Assert.True(writer.TryAddEntity(Entity(1)));
        int length = writer.Finish();

        Assert.Equal((Snapshot.HeaderBits + Snapshot.OwnResourcesBits + Snapshot.EntityBits + Snapshot.ExtraStateBits(1) + 7) >> 3,
            length);
        Assert.True(SnapshotReader.TryBegin(buffer.AsSpan(0, length), out var reader));
        Assert.True(reader.Header.HasOwnResources);
        Assert.Equal(header.Own, reader.Header.Own);
        Assert.True(reader.TryReadEntity(out var entity));
        Assert.Equal(Entity(1), entity);
    }

    [Fact]
    public void A_header_without_own_resources_reads_back_without_them()
    {
        var packet = Build(0, 0, 0);
        Assert.Equal(Snapshot.HeaderSize, packet.Length);
        Assert.True(SnapshotReader.TryBegin(packet, out var reader));
        Assert.False(reader.Header.HasOwnResources);
        Assert.Equal(default, reader.Header.Own);
    }

    [Theory]
    [InlineData(151, 150)]
    [InlineData(-1, 150)]
    public void The_writer_refuses_own_resources_above_their_max_or_negative(int hp, int maxHp)
    {
        var header = Header;
        header.HasOwnResources = true;
        header.Own = Own;
        header.Own.Hp = hp;
        header.Own.MaxHp = maxHp;
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotWriter(new byte[256], header));
    }

    [Fact]
    public void The_writer_needs_room_for_the_own_resources()
    {
        var header = Header;
        header.HasOwnResources = true;
        header.Own = Own;
        Assert.Throws<ArgumentException>(() => new SnapshotWriter(new byte[Snapshot.HeaderSize], header));
    }

    [Fact]
    public void A_forged_current_resource_above_its_max_is_refused()
    {
        var header = Header;
        header.HasOwnResources = true;
        header.Own = Own;
        var buffer = new byte[Snapshot.HeaderSizeWithOwnResources];
        int length = new SnapshotWriter(buffer, header).Finish();
        Assert.True(SnapshotReader.TryBegin(buffer.AsSpan(0, length), out _));

        // Stamina (the fifth value) above its max (the sixth).
        int staminaBit = Snapshot.HeaderBits + 4 * Snapshot.ResourceBits;
        PatchBits(buffer, staminaBit, 101, Snapshot.ResourceBits);
        Assert.False(SnapshotReader.TryBegin(buffer.AsSpan(0, length), out _));
    }

    [Fact]
    public void The_HP_bucket_is_a_protocol_constant_of_6_bits()
    {
        // Spec 1.7, 9.4: both sides must agree, so it is not config. 0 is dead, 63 full.
        Assert.Equal(6, Snapshot.HpBucketBits);
        Assert.Equal(63, Snapshot.TopHpBucket);
        Assert.Equal(1, Quantize.Bucket(1, 1_000_000, Snapshot.TopHpBucket));
        Assert.Equal(62, Quantize.Bucket(999_999, 1_000_000, Snapshot.TopHpBucket));
    }

    /// <summary>Overwrites bits of an existing packet, LSB first, to forge values the writer refuses to produce.</summary>
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
