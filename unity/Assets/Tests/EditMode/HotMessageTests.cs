using NUnit.Framework;
using Shared.Protocol;

namespace Game.Tests.EditMode
{
    /// <summary>The bit-packed hot messages (spec 11.2) also round-trip under Unity's runtime.</summary>
    public class HotMessageTests
    {
        [Test]
        public void Input_packet_round_trips()
        {
            var frames = new[]
            {
                new InputFrame { Sequence = 0, ClientTick = 100, SubTick = 63, Keys = MoveKeys.W | MoveKeys.D, Facing = 16384, Actions = InputActions.Attack, SkillSlot = 11 },
                new InputFrame { Sequence = 65535, ClientTick = 99, Keys = MoveKeys.S },
            };
            var buffer = new byte[InputPacket.MaxSize];
            int length = InputPacket.Write(buffer, frames);

            var read = new InputFrame[InputPacket.MaxFrames];
            Assert.IsTrue(InputPacket.TryRead(new System.ReadOnlySpan<byte>(buffer, 0, length), read, out int count));
            Assert.AreEqual(2, count);
            Assert.AreEqual(frames[0], read[0]);
            Assert.AreEqual(frames[1], read[1]);
        }

        [Test]
        public void Snapshot_round_trips()
        {
            var header = new SnapshotHeader { ServerTick = 7, HasInputAck = true, LastInputSequence = 5, OriginXCm = -100000, OriginYCm = 500, OriginZCm = 250000 };
            var entity = new EntitySnapshot { EntityId = 42, XCm = -99000, YCm = 800, ZCm = 251000, Facing = 32768, State = 3, ActionId = 101, StateAge = 4, HpBucket = 40, Flags = EntityFlags.Armed | EntityFlags.Pet };
            var buffer = new byte[256];

            var writer = new SnapshotWriter(buffer, header);
            Assert.IsTrue(writer.TryAddDespawn(9));
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 42, Kind = EntityKind.Monster, DefId = 12 }));
            Assert.IsTrue(writer.TryAddEntity(entity));
            int length = writer.Finish();

            Assert.IsTrue(SnapshotReader.TryBegin(new System.ReadOnlySpan<byte>(buffer, 0, length), out var reader));
            Assert.IsTrue(reader.TryReadDespawn(out uint despawned));
            Assert.AreEqual(9u, despawned);
            Assert.IsTrue(reader.TryReadSpawn(out var spawn));
            Assert.AreEqual(EntityKind.Monster, spawn.Kind);
            Assert.IsTrue(reader.TryReadEntity(out var readEntity));
            Assert.AreEqual(entity, readEntity);
            Assert.IsFalse(reader.TryReadEntity(out _));
        }
    }
}
