#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using NUnit.Framework;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.World;
using UnityEngine;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// Projectiles on the client (spec 3.2, backlog D3): spawn and despawn hot messages, the deterministic path from the
    /// start tick, duplicates from the server's repeats, late spawns, and stopping at walls without the despawn.
    /// </summary>
    public class ProjectileTrackerTests
    {
        private const int TickRate = 20;

        private static readonly ProjectileDef Arrow = new ProjectileDef
        {
            Id = "arrow", NetId = 1, SpeedCmPerSec = 2400, RangeCm = 2000, HitRadiusCm = 15, MaxHits = 1, StopsOnWorld = true,
            VisualId = "arrow",
        };

        private ProjectileTracker _tracker = null!;
        private List<uint> _spawned = null!;
        private List<uint> _despawned = null!;

        [SetUp]
        public void SetUp() => Use(null);

        private void Use(WorldGrid? grid)
        {
            _tracker = new ProjectileTracker(new[] { Arrow }, TickRate, grid);
            _spawned = new List<uint>();
            _despawned = new List<uint>();
            _tracker.Spawned += p => _spawned.Add(p.Id);
            _tracker.Despawned += p => _despawned.Add(p.Id);
        }

        private static ProjectileSpawn Spawn(uint id = 1, uint startTick = 100, ushort facing = 0, ushort defId = 1) => new ProjectileSpawn
        {
            ProjectileId = id, DefId = defId, XCm = 0, YCm = 0, HeightCm = 50, Facing = facing, SpeedCmPerSec = 2400,
            StartTick = startTick,
        };

        private static byte[] Message(params ProjectileSpawn[] spawns)
        {
            var buffer = new byte[1200];
            var writer = new ProjectileSpawnWriter(buffer);
            foreach (var spawn in spawns)
                Assert.IsTrue(writer.TryAdd(spawn));
            return buffer.AsSpan(0, writer.Finish()).ToArray();
        }

        private static byte[] Message(params ProjectileDespawn[] despawns)
        {
            var buffer = new byte[1200];
            var writer = new ProjectileDespawnWriter(buffer);
            foreach (var despawn in despawns)
                Assert.IsTrue(writer.TryAdd(despawn));
            return buffer.AsSpan(0, writer.Finish()).ToArray();
        }

        private static ProjectileDespawn Despawn(uint id, uint tick, ProjectileDespawnReason reason = ProjectileDespawnReason.Hit) =>
            new ProjectileDespawn { ProjectileId = id, Tick = tick, Reason = reason };

        [Test]
        public void A_spawn_is_drawn_on_its_straight_path_from_the_start_tick()
        {
            Assert.IsTrue(_tracker.Apply(Message(Spawn(startTick: 100))));
            CollectionAssert.AreEqual(new[] { 1u }, _spawned);
            var arrow = _tracker.Projectiles[1];
            Assert.AreEqual(50, arrow.HeightCm);

            arrow.GetPose(100, out float x, out float y); // after phase 4 of the start tick: one step of 120 cm
            Assert.AreEqual((0f, 120f), (x, y));
            arrow.GetPose(102.5, out x, out y); // blended between ticks
            Assert.AreEqual(420f, y, 0.01f);
            arrow.GetPose(99, out _, out y); // before it was fired: at the origin
            Assert.AreEqual(0f, y);
        }

        [Test]
        public void A_late_spawn_is_fast_forwarded_from_its_start_tick()
        {
            _tracker.Apply(Message(Spawn(startTick: 100)));
            _tracker.Projectiles[1].GetPose(110, out _, out float y);
            Assert.AreEqual(1320f, y, 0.01f); // 11 steps
        }

        [Test]
        public void Repeated_spawns_and_late_repeats_after_the_end_are_ignored()
        {
            var message = Message(Spawn(1), Spawn(2));
            _tracker.Apply(message);
            _tracker.Apply(message);
            CollectionAssert.AreEqual(new[] { 1u, 2u }, _spawned);

            _tracker.Apply(Message(Despawn(1, 101)));
            _tracker.Update(102);
            CollectionAssert.AreEqual(new[] { 1u }, _despawned);
            _tracker.Apply(message); // a repeat sent before the despawn, arriving late
            CollectionAssert.AreEqual(new[] { 1u, 2u }, _spawned);
        }

        [Test]
        public void A_despawn_applies_when_the_render_time_reaches_its_tick()
        {
            _tracker.Apply(Message(Spawn(startTick: 100)));
            _tracker.Apply(Message(Despawn(1, 104)));
            var arrow = _tracker.Projectiles[1];
            Assert.AreEqual(ProjectileDespawnReason.Hit, arrow.Reason);

            _tracker.Update(104.5);
            CollectionAssert.IsEmpty(_despawned);
            arrow.GetPose(104.9, out _, out float y);
            Assert.AreEqual(600f, y, 0.01f); // held where the hit tick left it
            _tracker.Update(105);
            CollectionAssert.AreEqual(new[] { 1u }, _despawned);
            Assert.IsFalse(_tracker.Projectiles.ContainsKey(1));
        }

        [Test]
        public void Without_a_despawn_it_ends_at_its_range()
        {
            _tracker.Apply(Message(Spawn(startTick: 100)));
            var arrow = _tracker.Projectiles[1];
            Assert.AreEqual(116u, arrow.Path.LastTick); // 2000 cm at 120 cm per tick: 17 steps
            _tracker.Update(116.9);
            CollectionAssert.IsEmpty(_despawned);
            _tracker.Update(117);
            CollectionAssert.AreEqual(new[] { 1u }, _despawned);
        }

        [Test]
        public void It_stops_at_a_wall_of_the_client_grid_like_the_server()
        {
            // 100 cm cells from (-1000, -1000); everything from y = 500 cm is blocked.
            int width = 20, height = 20;
            var flags = new byte[width * height];
            for (int row = 0; row < height; row++)
                for (int column = 0; column < width; column++)
                    flags[row * width + column] = row < 15 ? WorldGridFormat.FlagWalkable : (byte)0;
            Use(new WorldGrid(-1000, -1000, 100, width, height, 0, flags, new byte[width * height], new short[width * height]));

            _tracker.Apply(Message(Spawn(startTick: 0)));
            var arrow = _tracker.Projectiles[1];
            Assert.IsTrue(arrow.Path.StoppedByWorld);
            Assert.AreEqual(new FixVec2(0, FixedPoint.FromCm(500) - 1), arrow.Path.End);
            arrow.GetPose(50, out _, out float y);
            Assert.Less(y, 500f);
            _tracker.Update(arrow.Path.LastTick + 1);
            CollectionAssert.AreEqual(new[] { 1u }, _despawned);
        }

        [Test]
        public void Unknown_definitions_and_other_messages_are_not_drawn()
        {
            Assert.IsTrue(_tracker.Apply(Message(Spawn(defId: 99))));
            Assert.AreEqual(1, _tracker.UnknownDefs);
            CollectionAssert.IsEmpty(_spawned);
            Assert.IsFalse(_tracker.Apply(new byte[] { (byte)HotMessageId.Snapshot, 0 }));
            Assert.IsFalse(_tracker.Apply(new byte[] { (byte)HotMessageId.ProjectileSpawn, 1, 0 })); // truncated
        }

        [Test]
        public void Clear_despawns_everything()
        {
            _tracker.Apply(Message(Spawn(1), Spawn(2)));
            _tracker.Clear();
            CollectionAssert.AreEquivalent(new[] { 1u, 2u }, _despawned);
            CollectionAssert.IsEmpty(_tracker.Projectiles);
            _tracker.Apply(Message(Spawn(1)));
            Assert.AreEqual(3, _spawned.Count); // after a disconnect nothing ended is remembered
        }

        [Test]
        public void Views_follow_the_tracker_and_sit_on_the_path()
        {
            var host = new GameObject("ProjectileViewsTest");
            try
            {
                var views = host.AddComponent<ProjectileViews>();
                views.Bind(_tracker);
                _tracker.Apply(Message(Spawn(startTick: 100, facing: 16384))); // east
                Assert.AreEqual(1, views.Count);
                views.Place(101);
                var view = views.ViewOf(1)!;
                Assert.IsTrue(view.gameObject.activeSelf);
                Assert.AreEqual(2.4f, view.position.x, 1e-4f); // 240 cm east
                Assert.AreEqual(0.5f, view.position.y, 1e-4f); // the origin height
                Assert.AreEqual(90f, view.rotation.eulerAngles.y, 0.01f);

                _tracker.Clear();
                Assert.AreEqual(0, views.Count);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }
    }
}
