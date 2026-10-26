#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using NUnit.Framework;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using UnityEngine;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// Ground areas on the client (spec 3.1, protocol 9): spawn and despawn hot messages, the effect found by action net id
    /// and effect index, the span from the duration, duplicates from the server's repeats, the end without a despawn, and
    /// the placeholder mesh built from the hit shapes.
    /// </summary>
    public class GroundAreaTrackerTests
    {
        private const int TickRate = 20;

        private static readonly ActionDef Circle = new ActionDef
        {
            Id = "skill.circle", NetId = 106, Kind = ActionKind.Skill, StartupMs = 200, ChannelMs = 1500, ActiveMs = 100, RecoveryMs = 300,
        };

        private static readonly ActionEffectDef Area = new ActionEffectDef
        {
            Id = "skill.circle.area", ActionId = "skill.circle", Kind = EffectKind.GroundArea, Phase = ActionPhase.Active,
            Targets = new[] { EffectTarget.Enemies }, DurationMs = 1000, TickIntervalMs = 300, PoiseDamage = 5,
            DamagePercent = 40, DamageKind = EffectDamageKind.Magic, Element = Element.Fire,
        };

        private static readonly HitShapeDef Ring = new HitShapeDef
        {
            Id = "skill.circle.area.circle", EffectId = "skill.circle.area", Kind = HitShapeKind.Capsule, ForwardOffsetCm = 300, RadiusCm = 300,
        };

        private GroundAreaTracker _tracker = null!;
        private List<uint> _spawned = null!;
        private List<uint> _despawned = null!;

        [SetUp]
        public void SetUp()
        {
            var catalog = new ActionCatalog(TickRate, new[] { Circle }, new[] { Area }, (ActionBindings?)null);
            _tracker = new GroundAreaTracker(catalog, new[] { Ring });
            _spawned = new List<uint>();
            _despawned = new List<uint>();
            _tracker.Spawned += a => _spawned.Add(a.Id);
            _tracker.Despawned += a => _despawned.Add(a.Id);
        }

        private static GroundAreaSpawn Spawn(uint id = 1, uint startTick = 100, ushort actionId = 106, byte effectIndex = 0) => new GroundAreaSpawn
        {
            AreaId = id, ActionId = actionId, EffectIndex = effectIndex, XCm = 250, YCm = -40, HeightCm = 10, Facing = 16384,
            StartTick = startTick,
        };

        private static byte[] Message(params GroundAreaSpawn[] spawns)
        {
            var buffer = new byte[1200];
            var writer = new GroundAreaSpawnWriter(buffer);
            foreach (var spawn in spawns)
                Assert.IsTrue(writer.TryAdd(spawn));
            return buffer.AsSpan(0, writer.Finish()).ToArray();
        }

        private static byte[] Message(params GroundAreaDespawn[] despawns)
        {
            var buffer = new byte[1200];
            var writer = new GroundAreaDespawnWriter(buffer);
            foreach (var despawn in despawns)
                Assert.IsTrue(writer.TryAdd(despawn));
            return buffer.AsSpan(0, writer.Finish()).ToArray();
        }

        [Test]
        public void A_spawn_finds_its_effect_and_shapes_and_spans_its_duration()
        {
            Assert.IsTrue(_tracker.Apply(Message(Spawn())));

            var area = _tracker.Areas[1];
            Assert.AreSame(Area, area.Effect);
            Assert.AreEqual(1, area.Shapes.Count);
            Assert.AreEqual((250, -40, 10, (ushort)16384), (area.XCm, area.YCm, area.HeightCm, area.Facing));
            Assert.AreEqual(20, area.DurationTicks); // 1000 ms at 20 Hz
            Assert.IsFalse(area.IsShownAt(99.9));
            Assert.IsTrue(area.IsShownAt(100));
            Assert.IsTrue(area.IsShownAt(119.9));
            Assert.IsFalse(area.IsShownAt(120));
            CollectionAssert.AreEqual(new[] { 1u }, _spawned);
        }

        [Test]
        public void Repeats_are_dropped_and_an_area_ends_on_its_own_without_its_despawn()
        {
            _tracker.Apply(Message(Spawn()));
            _tracker.Apply(Message(Spawn()));
            Assert.AreEqual(1, _spawned.Count);

            _tracker.Update(119.5);
            Assert.AreEqual(1, _tracker.Areas.Count);
            _tracker.Update(120);
            Assert.AreEqual(0, _tracker.Areas.Count);
            CollectionAssert.AreEqual(new[] { 1u }, _despawned);

            _tracker.Apply(Message(Spawn())); // a late repeat of an ended area
            Assert.AreEqual(0, _tracker.Areas.Count);
        }

        [Test]
        public void A_despawn_ends_the_area_when_the_render_time_reaches_its_tick()
        {
            _tracker.Apply(Message(Spawn()));
            Assert.IsTrue(_tracker.Apply(Message(new GroundAreaDespawn { AreaId = 1, Tick = 105, Reason = GroundAreaDespawnReason.OwnerDied })));

            var area = _tracker.Areas[1];
            Assert.AreEqual(GroundAreaDespawnReason.OwnerDied, area.Reason);
            Assert.IsTrue(area.IsShownAt(104.5));
            _tracker.Update(104.9);
            Assert.AreEqual(1, _tracker.Areas.Count);
            _tracker.Update(105);
            Assert.AreEqual(0, _tracker.Areas.Count);
        }

        [Test]
        public void Spawns_naming_no_ground_area_effect_are_counted_and_dropped()
        {
            _tracker.Apply(Message(Spawn(1, actionId: 999), Spawn(2, effectIndex: 3)));
            Assert.AreEqual(0, _tracker.Areas.Count);
            Assert.AreEqual(2, _tracker.UnknownEffects);
            Assert.IsFalse(_tracker.Apply(new byte[] { (byte)HotMessageId.GroundAreaSpawn }));
        }

        [Test]
        public void Clear_removes_everything()
        {
            _tracker.Apply(Message(Spawn(1), Spawn(2)));
            _tracker.Clear();
            Assert.AreEqual(0, _tracker.Areas.Count);
            Assert.AreEqual(2, _despawned.Count);
        }

        [Test]
        public void The_placeholder_mesh_covers_each_shape_kind()
        {
            var shapes = new[]
            {
                new HitShapeDef { Id = "a", EffectId = "e", Kind = HitShapeKind.Cone, RangeCm = 350, HalfAngleDeg = 30 },
                new HitShapeDef { Id = "b", EffectId = "e", Kind = HitShapeKind.Box, LengthCm = 500, WidthCm = 160 },
                new HitShapeDef { Id = "c", EffectId = "e", Kind = HitShapeKind.Capsule, ForwardOffsetCm = 300, RadiusCm = 300 },
            };
            var color = new Color(1f, 0.5f, 0f, 0.3f);
            var mesh = GroundAreaMesh.Build(shapes, color);
            try
            {
                Assert.Greater(mesh.vertexCount, 0);
                Assert.AreEqual(0, mesh.triangles.Length % 3);
                var bounds = mesh.bounds;
                // The capsule (a circle of 3 m around 3 m forward) reaches 6 m forward; the box is 1.6 m wide.
                Assert.AreEqual(600f / WorldUnits.CmPerUnit, bounds.max.z, 0.01f);
                Assert.AreEqual(0f, bounds.size.y, 0.001f);
                Assert.AreEqual(color, mesh.colors[0]);
                // Every triangle faces up (clockwise seen from above).
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    var normal = Vector3.Cross(vertices[triangles[i + 1]] - vertices[triangles[i]], vertices[triangles[i + 2]] - vertices[triangles[i]]);
                    Assert.GreaterOrEqual(normal.y, -1e-4f); // the joints of a length-0 capsule are degenerate (area 0)
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
    }
}
