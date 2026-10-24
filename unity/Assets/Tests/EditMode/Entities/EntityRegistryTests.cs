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
        public void The_own_spawn_names_the_local_players_class()
        {
            // Backlog E: the own spawn's definition id is the ClassDef net id; the client picks its kit from it.
            var buffer = new byte[2048];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 10, HasInputAck = true, LastInputSequence = 3 });
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = Self, Kind = EntityKind.Player, DefId = 2 }));
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 3, Kind = EntityKind.Player, DefId = 1 }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = Self, HpBucket = 63 }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 3, HpBucket = 63 }));
            Assert.AreEqual(0, _registry.OwnDefId);

            Assert.IsTrue(Apply(buffer.AsSpan(0, writer.Finish()).ToArray(), out _));

            Assert.AreEqual(2, _registry.OwnDefId);
            Assert.AreEqual(1, _registry.Entities[3].DefId);
            Assert.IsFalse(_registry.Entities.ContainsKey(Self));
            _registry.Clear();
            Assert.AreEqual(0, _registry.OwnDefId);
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
            Assert.IsFalse(_registry.HasOwnResources);
        }

        // ---------- resources (protocol 3, spec 9.4) ----------

        private static readonly OwnResources Own = new OwnResources
        {
            Hp = 120, MaxHp = 150, Mp = 30, MaxMp = 30, Stamina = 75, MaxStamina = 100,
        };

        /// <summary>The observer, then a monster with <paramref name="monsterBucket"/>; own resources when given.</summary>
        private static byte[] ResourceSnapshot(uint tick, byte monsterBucket, OwnResources? own, ushort monsterDefId = 1)
        {
            var buffer = new byte[2048];
            var header = new SnapshotHeader { ServerTick = tick, HasOwnResources = own.HasValue, Own = own ?? default };
            var writer = new SnapshotWriter(buffer, header);
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 3, Kind = EntityKind.Monster, DefId = monsterDefId }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = Self, HpBucket = Shared.Protocol.Snapshot.TopHpBucket }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 3, XCm = 100, HpBucket = monsterBucket }));
            int size = writer.Finish();
            return buffer.AsSpan(0, size).ToArray();
        }

        [Test]
        public void A_monster_spawn_keeps_its_definition_id_and_its_HP_bucket_follows_the_newest_snapshot()
        {
            Apply(ResourceSnapshot(10, Shared.Protocol.Snapshot.TopHpBucket, Own, monsterDefId: 2), out _);
            var monster = _registry.Entities[3];
            Assert.AreEqual(EntityKind.Monster, monster.Kind);
            Assert.AreEqual(2, monster.DefId);
            Assert.AreEqual(1f, monster.HpFraction);

            Apply(ResourceSnapshot(12, 21, Own), out _);
            Assert.AreEqual(21, monster.HpBucket);
            Assert.AreEqual(21f / 63f, monster.HpFraction, 1e-6f);

            // A late, older snapshot does not step the bar back.
            Apply(ResourceSnapshot(11, 40, Own), out _);
            Assert.AreEqual(21, monster.HpBucket);
        }

        /// <summary>The observer, then monster 3 in <paramref name="state"/> with <paramref name="color"/> (protocol 6).</summary>
        private static byte[] TelegraphSnapshot(uint tick, byte state, Shared.Data.Tables.ParryColor color)
        {
            var buffer = new byte[2048];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = tick });
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 3, Kind = EntityKind.Monster, DefId = 1 }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = Self, HpBucket = Shared.Protocol.Snapshot.TopHpBucket }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot
            {
                EntityId = 3, State = state, ActionId = (ushort)(state == 0 ? 0 : 1), ParryColor = color,
                HpBucket = Shared.Protocol.Snapshot.TopHpBucket,
            }));
            int size = writer.Finish();
            return buffer.AsSpan(0, size).ToArray();
        }

        [Test]
        public void A_monster_attack_telegraphs_the_parry_color_of_the_newest_snapshot()
        {
            // R-3.1-05: the start of a monster attack carries its parry color (R-3.3-04).
            Apply(TelegraphSnapshot(10, 1, Shared.Data.Tables.ParryColor.Yellow), out _);
            var monster = _registry.Entities[3];
            Assert.AreEqual(Shared.Data.Tables.ParryColor.Yellow, monster.TelegraphColor);

            Apply(TelegraphSnapshot(11, 0, Shared.Data.Tables.ParryColor.None), out _);
            Assert.AreEqual(Shared.Data.Tables.ParryColor.None, monster.TelegraphColor);
        }

        [Test]
        public void The_local_players_exact_resources_come_from_the_newest_header()
        {
            Assert.IsFalse(_registry.HasOwnResources);
            Apply(ResourceSnapshot(10, Shared.Protocol.Snapshot.TopHpBucket, Own), out _);
            Assert.IsTrue(_registry.HasOwnResources);
            Assert.AreEqual(Own, _registry.OwnResources);

            var later = Own;
            later.Stamina = 55;
            Apply(ResourceSnapshot(11, Shared.Protocol.Snapshot.TopHpBucket, later), out _);
            Apply(ResourceSnapshot(9, Shared.Protocol.Snapshot.TopHpBucket, Own), out _); // late: ignored
            Assert.AreEqual(55, _registry.OwnResources.Stamina);

            Apply(ResourceSnapshot(12, Shared.Protocol.Snapshot.TopHpBucket, null), out _);
            Assert.IsFalse(_registry.HasOwnResources);
        }

        // ---------- own statuses (protocol 7, spec 3.7 buff row) ----------

        private static byte[] StatusSnapshot(uint tick, params OwnStatus[] statuses)
        {
            var buffer = new byte[2048];
            var header = new SnapshotHeader { ServerTick = tick, HasOwnResources = true, Own = Own };
            var writer = new SnapshotWriter(buffer, header, statuses);
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 3, Kind = EntityKind.Monster, DefId = 1 }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = Self, HpBucket = Shared.Protocol.Snapshot.TopHpBucket }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 3, XCm = 100, HpBucket = 10 }));
            int size = writer.Finish();
            return buffer.AsSpan(0, size).ToArray();
        }

        [Test]
        public void The_local_players_statuses_follow_the_newest_snapshot()
        {
            var buff = new OwnStatus { NetId = 1, Stacks = 1, RemainingTicks = 300 };
            var debuff = new OwnStatus { NetId = 5, Stacks = 2, RemainingTicks = 40 };
            Apply(StatusSnapshot(10, buff, debuff), out _);
            Assert.AreEqual(2, _registry.OwnStatusCount);
            Assert.AreEqual(buff, _registry.OwnStatuses[0]);
            Assert.AreEqual(debuff, _registry.OwnStatuses[1]);
            Assert.AreEqual(10u, _registry.OwnStatusesTick);
            Assert.AreEqual(10, _registry.Entities[3].HpBucket); // the rest of the snapshot still reads

            // A late, older snapshot changes nothing; a newer one without statuses clears them.
            Apply(StatusSnapshot(9), out _);
            Assert.AreEqual(2, _registry.OwnStatusCount);
            Apply(StatusSnapshot(11), out _);
            Assert.AreEqual(0, _registry.OwnStatusCount);
            Assert.AreEqual(11u, _registry.OwnStatusesTick);
        }
    }
}
