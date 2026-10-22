using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>Spec 11.2 and CLAUDE.md: no allocation per packet on the hot path (encode and decode).</summary>
public class HotMessageAllocationTests
{
    private readonly byte[] _snapshotBuffer = new byte[1200];
    private readonly byte[] _inputBuffer = new byte[InputPacket.MaxSize];
    private readonly byte[] _projectileBuffer = new byte[1200];
    private readonly InputFrame[] _frames = new InputFrame[InputPacket.MaxFrames];
    private readonly InputFrame[] _readFrames = new InputFrame[InputPacket.MaxFrames];

    [Fact]
    public void Writing_and_reading_hot_messages_does_not_allocate()
    {
        for (int i = 0; i < _frames.Length; i++)
            _frames[i] = new InputFrame { Sequence = (ushort)(100 - i), ClientTick = (uint)(1000 - i), Keys = MoveKeys.W, Facing = 1234 };

        RoundTrip(); // warm up the JIT
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            RoundTrip();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private void RoundTrip()
    {
        int inputLength = InputPacket.Write(_inputBuffer, _frames.AsSpan(0, 3));
        if (!InputPacket.TryRead(_inputBuffer.AsSpan(0, inputLength), _readFrames, out _))
            throw new InvalidOperationException("Input packet did not round-trip.");

        var header = new SnapshotHeader { ServerTick = 1, OriginXCm = 1000 };
        var writer = new SnapshotWriter(_snapshotBuffer, header);
        writer.TryAddDespawn(3);
        writer.TryAddSpawn(new SpawnEvent { EntityId = 4, Kind = EntityKind.Player });
        uint id = 0;
        while (writer.TryAddEntity(new EntitySnapshot { EntityId = id, XCm = 1000 + (int)id, HpBucket = 10 }))
            id++;
        int snapshotLength = writer.Finish();

        if (!SnapshotReader.TryBegin(_snapshotBuffer.AsSpan(0, snapshotLength), out var reader))
            throw new InvalidOperationException("Snapshot did not round-trip.");
        while (reader.TryReadEntity(out _)) { }

        var spawns = new ProjectileSpawnWriter(_projectileBuffer);
        spawns.TryAdd(new ProjectileSpawn { ProjectileId = 1, DefId = 1, XCm = -3000, Facing = 32768, SpeedCmPerSec = 2400, StartTick = 5 });
        int spawnLength = spawns.Finish();
        if (!ProjectileSpawnReader.TryBegin(_projectileBuffer.AsSpan(0, spawnLength), out var spawnReader))
            throw new InvalidOperationException("Projectile spawn did not round-trip.");
        while (spawnReader.TryRead(out _)) { }

        var despawns = new ProjectileDespawnWriter(_projectileBuffer);
        despawns.TryAdd(new ProjectileDespawn { ProjectileId = 1, Tick = 9, Reason = ProjectileDespawnReason.World });
        int despawnLength = despawns.Finish();
        if (!ProjectileDespawnReader.TryBegin(_projectileBuffer.AsSpan(0, despawnLength), out var despawnReader))
            throw new InvalidOperationException("Projectile despawn did not round-trip.");
        while (despawnReader.TryRead(out _)) { }
    }
}
