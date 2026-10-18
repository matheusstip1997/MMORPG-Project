using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// R-2.5-04: decoders are fuzz-tested. Random and mutated packets must be refused cleanly (false), never throw,
/// and anything accepted must re-encode to the same bytes. Seeds are fixed so failures reproduce.
/// </summary>
public class HotMessageFuzzTests
{
    private const int Iterations = 20_000;

    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(1234);
        var frames = new InputFrame[InputPacket.MaxFrames];
        for (int i = 0; i < Iterations; i++)
        {
            var packet = new byte[random.Next(0, 300)];
            random.NextBytes(packet);
            if (packet.Length > 0 && random.Next(2) == 0)
                packet[0] = (byte)(random.Next(2) == 0 ? HotMessageId.Input : HotMessageId.Snapshot);

            InputPacket.TryRead(packet, frames, out _);
            ReadWholeSnapshot(packet);
            HotMessage.TryPeekId(packet, out _);
        }
    }

    [Fact]
    public void Mutated_input_packets_are_refused_or_re_encode_exactly()
    {
        var random = new Random(5678);
        var frames = new InputFrame[InputPacket.MaxFrames];
        var reencoded = new byte[InputPacket.MaxSize];
        int accepted = 0;
        for (int i = 0; i < Iterations; i++)
        {
            byte[] packet = Mutate(ValidInput(random), random);
            if (!InputPacket.TryRead(packet, frames, out int count))
                continue;
            accepted++;
            int length = InputPacket.Write(reencoded, frames.AsSpan(0, count));
            Assert.Equal(packet, reencoded.AsSpan(0, length).ToArray());
        }
        Assert.True(accepted > 0);
    }

    [Fact]
    public void Mutated_snapshots_are_refused_or_read_completely()
    {
        var random = new Random(9012);
        int accepted = 0;
        for (int i = 0; i < Iterations; i++)
        {
            byte[] packet = Mutate(ValidSnapshot(random), random);
            if (ReadWholeSnapshot(packet))
                accepted++;
        }
        Assert.True(accepted > 0);
    }

    /// <summary>Once TryBegin accepts a packet, every entry it promised must read without failure.</summary>
    private static bool ReadWholeSnapshot(byte[] packet)
    {
        if (!SnapshotReader.TryBegin(packet, out var reader))
            return false;
        int despawns = 0, spawns = 0, entities = 0;
        while (reader.TryReadDespawn(out _)) despawns++;
        while (reader.TryReadSpawn(out _)) spawns++;
        while (reader.TryReadEntity(out _)) entities++;
        Assert.Equal((reader.DespawnCount, reader.SpawnCount, reader.EntityCount), (despawns, spawns, entities));
        return true;
    }

    private static byte[] Mutate(byte[] packet, Random random)
    {
        switch (random.Next(4))
        {
            case 0: // flip a few bits
                for (int flips = random.Next(1, 4); flips > 0; flips--)
                    packet[random.Next(packet.Length)] ^= (byte)(1 << random.Next(8));
                return packet;
            case 1: // truncate
                return packet.AsSpan(0, random.Next(packet.Length)).ToArray();
            case 2: // extend
                return [.. packet, (byte)random.Next(256)];
            default: // overwrite a byte
                packet[random.Next(packet.Length)] = (byte)random.Next(256);
                return packet;
        }
    }

    private static byte[] ValidInput(Random random)
    {
        int count = random.Next(1, InputPacket.MaxFrames + 1);
        var frames = new InputFrame[count];
        ushort sequence = (ushort)random.Next(65536);
        uint tick = (uint)random.Next(10_000, int.MaxValue);
        for (int i = 0; i < count; i++)
        {
            frames[i] = new InputFrame
            {
                Sequence = sequence,
                ClientTick = tick,
                SubTick = (byte)random.Next(64),
                Keys = (MoveKeys)random.Next(16),
                Facing = (ushort)random.Next(65536),
                Actions = (InputActions)random.Next(16),
                SkillSlot = (byte)random.Next(InputPacket.MaxSkillSlot + 1),
            };
            sequence--;
            tick -= (uint)random.Next(1, 4);
        }
        var buffer = new byte[InputPacket.MaxSize];
        return buffer.AsSpan(0, InputPacket.Write(buffer, frames)).ToArray();
    }

    private static byte[] ValidSnapshot(Random random)
    {
        var header = new SnapshotHeader
        {
            ServerTick = (uint)random.Next(),
            HasInputAck = random.Next(2) == 0,
            LastInputSequence = (ushort)random.Next(65536),
            OriginXCm = random.Next(-1_000_000, 1_000_000),
            OriginYCm = random.Next(-10_000, 10_000),
            OriginZCm = random.Next(-1_000_000, 1_000_000),
        };
        var buffer = new byte[1200];
        var writer = new SnapshotWriter(buffer, header);
        for (int i = random.Next(3); i > 0; i--)
            writer.TryAddDespawn((uint)random.Next());
        for (int i = random.Next(3); i > 0; i--)
            writer.TryAddSpawn(new SpawnEvent { EntityId = (uint)random.Next(), Kind = (EntityKind)random.Next(1, 9), DefId = (ushort)random.Next(65536) });
        for (int i = random.Next(6); i > 0; i--)
        {
            byte state = (byte)random.Next(8); // every action state code (D2)
            int extra = Snapshot.ExtraStateBits(state);
            writer.TryAddEntity(new EntitySnapshot
            {
                EntityId = (uint)random.Next(),
                XCm = header.OriginXCm + random.Next(-100_000, 100_000),
                YCm = header.OriginYCm + random.Next(-10_000, 10_000),
                ZCm = header.OriginZCm + random.Next(-100_000, 100_000),
                Facing = (ushort)random.Next(65536),
                State = state,
                ActionId = extra == Snapshot.ActionStateBits ? (ushort)random.Next(1, 4096) : (ushort)0,
                StateAge = extra > 0 ? (byte)random.Next(256) : (byte)0,
                StateDurationTicks = extra == Snapshot.InterruptStateBits ? (byte)random.Next(1, 256) : (byte)0,
                HpBucket = (byte)random.Next(Snapshot.TopHpBucket + 1),
                Flags = (EntityFlags)random.Next(32),
            });
        }
        return buffer.AsSpan(0, writer.Finish()).ToArray();
    }
}
