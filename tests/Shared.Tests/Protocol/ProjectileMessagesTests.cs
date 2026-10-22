using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>Spec 1.7 and 3.2: the projectile spawn (hot message 3) and despawn (hot message 5) layouts.</summary>
public class ProjectileMessagesTests
{
    private static ProjectileSpawn Spawn(uint id = 7) => new()
    {
        ProjectileId = id, DefId = 1, XCm = -3000, YCm = 15, HeightCm = 120, Facing = 32768, SpeedCmPerSec = 2400,
        StartTick = 123_456,
    };

    private static byte[] Write(params ProjectileSpawn[] spawns)
    {
        var buffer = new byte[1200];
        var writer = new ProjectileSpawnWriter(buffer);
        foreach (var spawn in spawns)
            Assert.True(writer.TryAdd(spawn));
        return buffer.AsSpan(0, writer.Finish()).ToArray();
    }

    private static byte[] Write(params ProjectileDespawn[] despawns)
    {
        var buffer = new byte[1200];
        var writer = new ProjectileDespawnWriter(buffer);
        foreach (var despawn in despawns)
            Assert.True(writer.TryAdd(despawn));
        return buffer.AsSpan(0, writer.Finish()).ToArray();
    }

    [Fact]
    public void Spawns_round_trip()
    {
        var extreme = new ProjectileSpawn
        {
            ProjectileId = uint.MaxValue, DefId = ProjectileMessages.MaxDefId, XCm = -(1 << 23), YCm = (1 << 23) - 1,
            HeightCm = -1, Facing = 65472, SpeedCmPerSec = ProjectileMessages.MaxSpeedCmPerSec, StartTick = uint.MaxValue,
        };
        byte[] packet = Write(Spawn(), extreme);
        Assert.Equal((byte)HotMessageId.ProjectileSpawn, packet[0]);
        Assert.Equal(ProjectileMessages.SpawnSizeFor(2), packet.Length);
        Assert.True(HotMessage.TryPeekId(packet, out var id));
        Assert.Equal(HotMessageId.ProjectileSpawn, id);

        Assert.True(ProjectileSpawnReader.TryBegin(packet, out var reader));
        Assert.Equal(2, reader.Count);
        Assert.True(reader.TryRead(out var first));
        Assert.Equal(Spawn(), first);
        Assert.True(reader.TryRead(out var second));
        Assert.Equal(extreme, second);
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void The_facing_goes_out_at_the_resolution_of_the_facing_table()
    {
        // FixedTrig uses 1024 steps per turn and the server spawns with the facing rounded to them, so the 10 wire bits
        // carry the exact facing the server simulates.
        var spawn = Spawn();
        spawn.Facing = Quantize.RoundTripAngle(12345, ProjectileMessages.FacingBits);
        Assert.True(ProjectileSpawnReader.TryBegin(Write(spawn), out var reader));
        Assert.True(reader.TryRead(out var read));
        Assert.Equal(spawn.Facing, read.Facing);
    }

    [Fact]
    public void Despawns_round_trip_with_every_reason()
    {
        var despawns = new[]
        {
            new ProjectileDespawn { ProjectileId = 1, Tick = 10, Reason = ProjectileDespawnReason.Hit },
            new ProjectileDespawn { ProjectileId = 2, Tick = 11, Reason = ProjectileDespawnReason.Range },
            new ProjectileDespawn { ProjectileId = 3, Tick = 12, Reason = ProjectileDespawnReason.World },
            new ProjectileDespawn { ProjectileId = uint.MaxValue, Tick = uint.MaxValue, Reason = ProjectileDespawnReason.OwnerGone },
        };
        byte[] packet = Write(despawns);
        Assert.Equal(ProjectileMessages.DespawnSizeFor(4), packet.Length);
        Assert.True(HotMessage.TryPeekId(packet, out var id));
        Assert.Equal(HotMessageId.ProjectileDespawn, id);
        Assert.True(ProjectileDespawnReader.TryBegin(packet, out var reader));
        foreach (var expected in despawns)
        {
            Assert.True(reader.TryRead(out var read));
            Assert.Equal(expected, read);
        }
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void An_empty_message_is_valid()
    {
        Assert.True(ProjectileSpawnReader.TryBegin(Write(Array.Empty<ProjectileSpawn>()), out var spawns));
        Assert.Equal(0, spawns.Count);
        Assert.True(ProjectileDespawnReader.TryBegin(Write(Array.Empty<ProjectileDespawn>()), out var despawns));
        Assert.Equal(0, despawns.Count);
    }

    [Fact]
    public void Values_the_layout_cannot_carry_throw()
    {
        var buffer = new byte[100];
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectileSpawnWriter(buffer).TryAdd(Spawn() with { DefId = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectileSpawnWriter(buffer).TryAdd(Spawn() with { DefId = 4096 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectileSpawnWriter(buffer).TryAdd(Spawn() with { SpeedCmPerSec = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectileSpawnWriter(buffer).TryAdd(Spawn() with { XCm = 1 << 23 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProjectileDespawnWriter(buffer).TryAdd(new ProjectileDespawn { Reason = ProjectileDespawnReason.None }));
    }

    [Fact]
    public void A_full_buffer_refuses_the_entry_and_keeps_the_message()
    {
        var buffer = new byte[ProjectileMessages.SpawnSizeFor(2)];
        var writer = new ProjectileSpawnWriter(buffer);
        Assert.True(writer.TryAdd(Spawn(1)));
        Assert.True(writer.TryAdd(Spawn(2)));
        Assert.False(writer.TryAdd(Spawn(3)));
        int length = writer.Finish();
        Assert.True(ProjectileSpawnReader.TryBegin(buffer.AsSpan(0, length), out var reader));
        Assert.Equal(2, reader.Count);
        Assert.Equal(2, ProjectileMessages.SpawnsThatFit(buffer.Length));
    }

    [Fact]
    public void Malformed_messages_are_refused()
    {
        byte[] spawn = Write(Spawn());
        Assert.False(ProjectileSpawnReader.TryBegin(spawn.AsSpan(0, spawn.Length - 1), out _)); // truncated
        Assert.False(ProjectileSpawnReader.TryBegin([.. spawn, 0], out _)); // trailing byte
        Assert.False(ProjectileDespawnReader.TryBegin(spawn, out _)); // wrong id

        byte[] padded = Write(Spawn());
        padded[^1] |= 0x80; // a padding bit (175 + 16 bits leave one)
        Assert.False(ProjectileSpawnReader.TryBegin(padded, out _));

        byte[] despawn = Write(new ProjectileDespawn { ProjectileId = 1, Tick = 1, Reason = ProjectileDespawnReason.Hit });
        // The reason is the last 3 bits after id 8 + count 8 + 32 + 32 = bit 80: byte 10, bits 0-2. 7 is unknown.
        despawn[10] |= 0b111;
        Assert.False(ProjectileDespawnReader.TryBegin(despawn, out _));
    }
}
