using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>Protocol 9 (spec 1.7, 3.1): the ground area spawn (hot message 6) and despawn (hot message 7) layouts.</summary>
public class GroundAreaMessagesTests
{
    private static GroundAreaSpawn Spawn(uint id = 7) => new()
    {
        AreaId = id, ActionId = 106, EffectIndex = 0, XCm = -3000, YCm = 15, HeightCm = 120, Facing = 32768, StartTick = 123_456,
    };

    private static byte[] Write(params GroundAreaSpawn[] spawns)
    {
        var buffer = new byte[1200];
        var writer = new GroundAreaSpawnWriter(buffer);
        foreach (var spawn in spawns)
            Assert.True(writer.TryAdd(spawn));
        return buffer.AsSpan(0, writer.Finish()).ToArray();
    }

    private static byte[] Write(params GroundAreaDespawn[] despawns)
    {
        var buffer = new byte[1200];
        var writer = new GroundAreaDespawnWriter(buffer);
        foreach (var despawn in despawns)
            Assert.True(writer.TryAdd(despawn));
        return buffer.AsSpan(0, writer.Finish()).ToArray();
    }

    [Fact]
    public void Spawns_round_trip()
    {
        var extreme = new GroundAreaSpawn
        {
            AreaId = uint.MaxValue, ActionId = GroundAreaMessages.MaxActionId, EffectIndex = GroundAreaMessages.MaxEffectIndex,
            XCm = -(1 << 23), YCm = (1 << 23) - 1, HeightCm = -1, Facing = 65472, StartTick = uint.MaxValue,
        };
        byte[] packet = Write(Spawn(), extreme);
        Assert.Equal((byte)HotMessageId.GroundAreaSpawn, packet[0]);
        Assert.Equal(GroundAreaMessages.SpawnSizeFor(2), packet.Length);
        Assert.Equal((8 + 8 + 2 * 161 + 7) / 8, packet.Length);
        Assert.True(HotMessage.TryPeekId(packet, out var id));
        Assert.Equal(HotMessageId.GroundAreaSpawn, id);

        Assert.True(GroundAreaSpawnReader.TryBegin(packet, out var reader));
        Assert.Equal(2, reader.Count);
        Assert.True(reader.TryRead(out var first));
        Assert.Equal(Spawn(), first);
        Assert.True(reader.TryRead(out var second));
        Assert.Equal(extreme, second);
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void Despawns_round_trip_with_every_reason()
    {
        var despawns = new[]
        {
            new GroundAreaDespawn { AreaId = 1, Tick = 100, Reason = GroundAreaDespawnReason.Expired },
            new GroundAreaDespawn { AreaId = 2, Tick = 101, Reason = GroundAreaDespawnReason.OwnerDied },
            new GroundAreaDespawn { AreaId = uint.MaxValue, Tick = uint.MaxValue, Reason = GroundAreaDespawnReason.OwnerGone },
        };
        byte[] packet = Write(despawns);
        Assert.Equal(GroundAreaMessages.DespawnSizeFor(3), packet.Length);
        Assert.True(HotMessage.TryPeekId(packet, out var id));
        Assert.Equal(HotMessageId.GroundAreaDespawn, id);
        Assert.True(GroundAreaDespawnReader.TryBegin(packet, out var reader));
        foreach (var expected in despawns)
        {
            Assert.True(reader.TryRead(out var despawn));
            Assert.Equal(expected, despawn);
        }
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void The_writers_refuse_values_the_layout_cannot_carry()
    {
        var buffer = new byte[256];
        Assert.Throws<ArgumentOutOfRangeException>(() => new GroundAreaSpawnWriter(buffer).TryAdd(Spawn() with { ActionId = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GroundAreaSpawnWriter(buffer).TryAdd(Spawn() with { ActionId = 4096 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GroundAreaSpawnWriter(buffer).TryAdd(Spawn() with { EffectIndex = 8 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GroundAreaSpawnWriter(buffer).TryAdd(Spawn() with { XCm = 1 << 23 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GroundAreaDespawnWriter(buffer).TryAdd(new GroundAreaDespawn { AreaId = 1, Reason = GroundAreaDespawnReason.None }));
        Assert.Throws<ArgumentException>(() => new GroundAreaSpawnWriter(new byte[1]));
    }

    [Fact]
    public void A_full_buffer_leaves_the_message_as_it_was()
    {
        var buffer = new byte[GroundAreaMessages.SpawnSizeFor(1)];
        var writer = new GroundAreaSpawnWriter(buffer);
        Assert.True(writer.TryAdd(Spawn(1)));
        Assert.False(writer.TryAdd(Spawn(2)));
        int length = writer.Finish();
        Assert.True(GroundAreaSpawnReader.TryBegin(buffer.AsSpan(0, length), out var reader));
        Assert.Equal(1, reader.Count);
        Assert.Equal(1, GroundAreaMessages.SpawnsThatFit(buffer.Length));
    }

    [Fact]
    public void Malformed_messages_are_refused_whole()
    {
        byte[] spawn = Write(Spawn());
        Assert.False(GroundAreaSpawnReader.TryBegin(spawn.AsSpan(0, spawn.Length - 1), out _)); // short
        Assert.False(GroundAreaSpawnReader.TryBegin([.. spawn, 0], out _));                    // long
        Assert.False(GroundAreaDespawnReader.TryBegin(spawn, out _));                          // another id
        var zeroAction = (byte[])spawn.Clone();
        PatchBits(zeroAction, 16 + 32, 0, GroundAreaMessages.ActionIdBits);
        Assert.False(GroundAreaSpawnReader.TryBegin(zeroAction, out _));

        byte[] despawn = Write(new GroundAreaDespawn { AreaId = 1, Tick = 2, Reason = GroundAreaDespawnReason.Expired });
        var badReason = (byte[])despawn.Clone();
        PatchBits(badReason, 16 + 64, 7, GroundAreaMessages.ReasonBits);
        Assert.False(GroundAreaDespawnReader.TryBegin(badReason, out _));
        var padding = (byte[])despawn.Clone();
        padding[^1] |= 0x80; // a padding bit set
        Assert.False(GroundAreaDespawnReader.TryBegin(padding, out _));
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
