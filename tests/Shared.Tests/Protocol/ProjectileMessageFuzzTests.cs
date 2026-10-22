using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// R-2.5-04 for the projectile hot messages (spec 3.2): random and mutated packets are refused cleanly or read completely,
/// never throw, and anything accepted re-encodes to the same bytes. Seeds are fixed so failures reproduce.
/// </summary>
public class ProjectileMessageFuzzTests
{
    private const int Iterations = 20_000;

    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(4321);
        for (int i = 0; i < Iterations; i++)
        {
            var packet = new byte[random.Next(0, 300)];
            random.NextBytes(packet);
            if (packet.Length > 0 && random.Next(2) == 0)
                packet[0] = (byte)(random.Next(2) == 0 ? HotMessageId.ProjectileSpawn : HotMessageId.ProjectileDespawn);
            ReencodeSpawns(packet);
            ReencodeDespawns(packet);
        }
    }

    [Fact]
    public void Mutated_spawns_are_refused_or_re_encode_exactly()
    {
        var random = new Random(8765);
        int accepted = 0;
        for (int i = 0; i < Iterations; i++)
        {
            byte[] packet = Mutate(ValidSpawns(random), random);
            if (ReencodeSpawns(packet))
                accepted++;
        }
        Assert.True(accepted > 0);
    }

    [Fact]
    public void Mutated_despawns_are_refused_or_re_encode_exactly()
    {
        var random = new Random(2109);
        int accepted = 0;
        for (int i = 0; i < Iterations; i++)
        {
            byte[] packet = Mutate(ValidDespawns(random), random);
            if (ReencodeDespawns(packet))
                accepted++;
        }
        Assert.True(accepted > 0);
    }

    private static bool ReencodeSpawns(byte[] packet)
    {
        if (!ProjectileSpawnReader.TryBegin(packet, out var reader))
            return false;
        var buffer = new byte[ProjectileMessages.SpawnSizeFor(ProjectileMessages.MaxCount)];
        var writer = new ProjectileSpawnWriter(buffer);
        int count = 0;
        while (reader.TryRead(out var spawn))
        {
            Assert.True(writer.TryAdd(spawn));
            count++;
        }
        Assert.Equal(reader.Count, count);
        Assert.Equal(packet, buffer.AsSpan(0, writer.Finish()).ToArray());
        return true;
    }

    private static bool ReencodeDespawns(byte[] packet)
    {
        if (!ProjectileDespawnReader.TryBegin(packet, out var reader))
            return false;
        var buffer = new byte[ProjectileMessages.DespawnSizeFor(ProjectileMessages.MaxCount)];
        var writer = new ProjectileDespawnWriter(buffer);
        int count = 0;
        while (reader.TryRead(out var despawn))
        {
            Assert.True(writer.TryAdd(despawn));
            count++;
        }
        Assert.Equal(reader.Count, count);
        Assert.Equal(packet, buffer.AsSpan(0, writer.Finish()).ToArray());
        return true;
    }

    private static byte[] Mutate(byte[] packet, Random random)
    {
        switch (random.Next(4))
        {
            case 0:
                for (int flips = random.Next(1, 4); flips > 0; flips--)
                    packet[random.Next(packet.Length)] ^= (byte)(1 << random.Next(8));
                return packet;
            case 1:
                return packet.AsSpan(0, random.Next(packet.Length)).ToArray();
            case 2:
                return [.. packet, (byte)random.Next(256)];
            default:
                packet[random.Next(packet.Length)] = (byte)random.Next(256);
                return packet;
        }
    }

    private static byte[] ValidSpawns(Random random)
    {
        var buffer = new byte[1200];
        var writer = new ProjectileSpawnWriter(buffer);
        for (int i = random.Next(1, 6); i > 0; i--)
        {
            writer.TryAdd(new ProjectileSpawn
            {
                ProjectileId = (uint)random.Next(),
                DefId = (ushort)random.Next(1, ProjectileMessages.MaxDefId + 1),
                XCm = random.Next(-(1 << 23), 1 << 23),
                YCm = random.Next(-(1 << 23), 1 << 23),
                HeightCm = random.Next(-10_000, 10_000),
                Facing = (ushort)(random.Next(1024) << 6), // what the wire keeps, so it re-encodes exactly
                SpeedCmPerSec = random.Next(1, ProjectileMessages.MaxSpeedCmPerSec + 1),
                StartTick = (uint)random.Next(),
            });
        }
        return buffer.AsSpan(0, writer.Finish()).ToArray();
    }

    private static byte[] ValidDespawns(Random random)
    {
        var buffer = new byte[1200];
        var writer = new ProjectileDespawnWriter(buffer);
        for (int i = random.Next(1, 8); i > 0; i--)
        {
            writer.TryAdd(new ProjectileDespawn
            {
                ProjectileId = (uint)random.Next(),
                Tick = (uint)random.Next(),
                Reason = (ProjectileDespawnReason)random.Next(1, 5),
            });
        }
        return buffer.AsSpan(0, writer.Finish()).ToArray();
    }
}
