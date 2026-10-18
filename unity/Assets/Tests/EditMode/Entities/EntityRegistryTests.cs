#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Tests.EditMode.Prediction;
using NUnit.Framework;
using Shared.Protocol;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>Remote entity spawn and despawn from snapshots (spec 1.7, 2.3).</summary>
    public class EntityRegistryTests
    {
        private const uint Self = 7;

        private EntityRegistry _registry = null!;
        private List<uint> _spawned = null!;
        private List<uint> _despawned = null!;

        [SetUp]
        public void SetUp()
        {
            _registry = new EntityRegistry(PredictionTestKit.Settings());
            _spawned = new List<uint>();
            _despawned = new List<uint>();
            _registry.Spawned += e => _spawned.Add(e.Id);
            _registry.Despawned += e => _despawned.Add(e.Id);
        }

        private static byte[] Snapshot(uint tick, uint[] entities, uint[]? spawns = null, uint[]? despawns = null)
        {
            var buffer = new byte[2048];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = tick, HasInputAck = true, LastInputSequence = 3 });
            foreach (var id in despawns ?? Array.Empty<uint>())
                Assert.IsTrue(writer.TryAddDespawn(id));
            foreach (var id in spawns ?? Array.Empty<uint>())
                Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = id, Kind = EntityKind.Player }));
            foreach (var id in entities)
                Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = id, XCm = (int)(id * 100 + tick), ZCm = 5, HpBucket = 63 }));
            int size = writer.Finish();
            return buffer.AsSpan(0, size).ToArray();
        }

        private bool Apply(byte[] snapshot, out EntitySnapshot own) =>
            _registry.ApplySnapshot(snapshot, out _, out own, out bool hasOwn) && hasOwn;

        [Test]
        public void The_first_entity_is_the_local_player_and_is_never_a_remote_entity()
        {
            Assert.IsTrue(Apply(Snapshot(10, new[] { Self, 3u }, spawns: new[] { Self, 3u }), out var own));

            Assert.AreEqual(Self, own.EntityId);
            Assert.AreEqual(Self, _registry.OwnEntityId);
            CollectionAssert.AreEqual(new[] { 3u }, _spawned);
            Assert.IsEmpty(_despawned);
            Assert.AreEqual(EntityKind.Player, _registry.Entities[3].Kind);
        }

        [Test]
        public void An_entity_whose_spawn_was_lost_appears_on_first_sight_with_no_kind()
        {
            Apply(Snapshot(10, new[] { Self }), out _);

            Apply(Snapshot(11, new[] { Self, 4u }), out _);

            CollectionAssert.AreEqual(new[] { 4u }, _spawned);
            Assert.AreEqual(EntityKind.None, _registry.Entities[4].Kind);
        }

        [Test]
        public void A_despawn_takes_effect_when_the_render_time_reaches_it()
        {
            Apply(Snapshot(10, new[] { Self, 3u }, spawns: new[] { 3u }), out _);
            Apply(Snapshot(11, new[] { Self }, despawns: new[] { 3u }), out _);

            _registry.Update(renderTick: 10.5);
            Assert.IsEmpty(_despawned, "still drawn up to its last position");
            _registry.Update(renderTick: 11);
            CollectionAssert.AreEqual(new[] { 3u }, _despawned);
            Assert.IsFalse(_registry.Entities.ContainsKey(3));
        }

        [Test]
        public void An_entity_missing_from_a_newer_snapshot_leaves_even_if_its_despawn_was_lost()
        {
            Apply(Snapshot(10, new[] { Self, 3u }), out _);
            Apply(Snapshot(12, new[] { Self }), out _);

            _registry.Update(renderTick: 12);

            CollectionAssert.AreEqual(new[] { 3u }, _despawned);
        }

        [Test]
        public void An_entity_back_in_interest_before_its_removal_stays()
        {
            Apply(Snapshot(10, new[] { Self, 3u }), out _);
            Apply(Snapshot(11, new[] { Self }, despawns: new[] { 3u }), out _);
            Apply(Snapshot(12, new[] { Self, 3u }, spawns: new[] { 3u }), out _);

            _registry.Update(renderTick: 13);

            Assert.IsEmpty(_despawned);
            CollectionAssert.AreEqual(new[] { 3u }, _spawned);
        }

        [Test]
        public void A_late_older_snapshot_adds_samples_but_changes_no_membership()
        {
            Apply(Snapshot(10, new[] { Self, 3u }), out _);
            Apply(Snapshot(12, new[] { Self, 3u }), out _);

            Apply(Snapshot(11, new[] { Self, 3u, 9u }, spawns: new[] { 9u }, despawns: new[] { 3u }), out _);

            CollectionAssert.AreEqual(new[] { 3u }, _spawned);
            Assert.AreEqual(3, _registry.Entities[3].Samples.Count, "the late sample fills the gap");
            _registry.Update(renderTick: 20);
            Assert.IsEmpty(_despawned);
        }

        [Test]
        public void A_malformed_snapshot_changes_nothing()
        {
            var bytes = Snapshot(10, new[] { Self, 3u });

            Assert.IsFalse(_registry.ApplySnapshot(bytes.AsSpan(0, bytes.Length - 1), out _, out _, out _));
            Assert.IsEmpty(_spawned);
        }

        [Test]
        public void Clear_despawns_everything()
        {
            Apply(Snapshot(10, new[] { Self, 3u, 4u }), out _);

            _registry.Clear();

            CollectionAssert.AreEquivalent(new[] { 3u, 4u }, _despawned);
            Assert.IsNull(_registry.OwnEntityId);
        }
    }
}
