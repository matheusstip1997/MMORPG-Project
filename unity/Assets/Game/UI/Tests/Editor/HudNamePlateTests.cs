#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Game.Gameplay.Streaming;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.UI.Tests
{
    /// <summary>Spec 9.3 <c>nameplate</c>: above each other character, never the observer's own (QA wave 6 F6, PR #43).</summary>
    public sealed class HudNamePlateTests
    {
        private const uint Self = 7, Other = 3;

        [Test]
        public void Plates_go_above_the_other_characters_and_never_the_observers_own()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            var config = HudConfig.From(set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out error), Is.True, error);
            var settings = PredictionSettings.From(set);
            var catalog = ActionCatalog.From(set);
            var feed = new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs);
            var models = new HudViewModels(4, 4, config.Chat.HistoryLines, config.Chat.MaxChars);
            var binder = new HudFeedBinder(config, models, () => 0);
            binder.Bind(feed);

            // The server lists the observer first and spawns it like any player; both get their display names.
            Assert.That(feed.Registry.ApplySnapshot(Snapshot(10, new[] { Self, Other }), out _, out _, out bool hasOwn) && hasOwn, Is.True);
            feed.RaiseSnapshotApplied();
            feed.RaiseIdentity(Self, "Myself");
            feed.RaiseIdentity(Other, "Aria");
            Assert.That(feed.Registry.ApplySnapshot(Snapshot(11, new[] { Self, Other }, spawn: false), out _, out _, out _), Is.True);
            feed.RaiseSnapshotApplied();

            var plates = models.NamePlates;
            Assert.That(plates.TryGetIndex(Other, out int other) && plates[other].Name == "Aria", Is.True);
            Assert.That(plates.TryGetIndex(Self, out _), Is.False, "the observer's own character has no name plate");
            Assert.That(plates.ActiveCount, Is.EqualTo(1));
            binder.Dispose();
        }

        private const byte Full = Shared.Protocol.Snapshot.TopHpBucket;

        [Test]
        public void The_target_lock_marks_one_plate_and_shows_the_locked_monsters_hp_bar()
        {
            // R-2.2-06 and spec 9.3: the lock marker, and a locked normal monster's HP bar even at full HP.
            var plates = new NamePlateSet(4);
            var changed = new List<int>();
            plates.PlateChanged += changed.Add;
            plates.Add(1, PlateKind.Monster, "Demon", Full, EntityFlags.None);
            plates.Add(2, PlateKind.Boss, "Boss", Full, EntityFlags.None);
            Assert.That(plates.TryGetIndex(1, out int demon), Is.True);
            Assert.That(plates.TryGetIndex(2, out int boss), Is.True);
            Assert.That(plates[demon].ShowsHpBar, Is.False);
            changed.Clear();

            plates.SetLocked(1);
            Assert.That(plates[demon].Locked && plates[demon].ShowsHpBar, Is.True);
            Assert.That(changed, Is.EqualTo(new[] { demon }));
            changed.Clear();

            plates.SetLocked(2);
            Assert.That(plates[demon].Locked || plates[demon].ShowsHpBar, Is.False);
            Assert.That(plates[boss].Locked, Is.True);
            Assert.That(plates[boss].ShowsHpBar, Is.False, "a boss shows its HP in the boss bar");
            Assert.That(changed, Is.EquivalentTo(new[] { demon, boss }));

            plates.SetLocked(0);
            Assert.That(plates[boss].Locked, Is.False);
        }

        [Test]
        public void A_plate_that_appears_after_the_lock_gets_the_marker()
        {
            var plates = new NamePlateSet(4);
            plates.SetLocked(5);

            plates.Add(5, PlateKind.Monster, "Demon", Full, EntityFlags.None);

            Assert.That(plates.TryGetIndex(5, out int index) && plates[index].Locked, Is.True);
        }

        [Test]
        public void The_feed_moves_the_marker_and_a_disconnect_drops_it()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            var config = HudConfig.From(set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out error), Is.True, error);
            var settings = PredictionSettings.From(set);
            var catalog = ActionCatalog.From(set);
            var feed = new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs);
            var models = new HudViewModels(4, 4, config.Chat.HistoryLines, config.Chat.MaxChars);
            var binder = new HudFeedBinder(config, models, () => 0);
            binder.Bind(feed);
            Assert.That(feed.Registry.ApplySnapshot(Snapshot(10, new[] { Self, Other }), out _, out _, out _), Is.True);
            feed.RaiseSnapshotApplied();

            feed.RaiseLockedTarget(Other);
            Assert.That(models.NamePlates.TryGetIndex(Other, out int other) && models.NamePlates[other].Locked, Is.True);

            feed.RaiseDisconnected("");
            Assert.That(feed.LockedTargetId, Is.EqualTo(0u));
            Assert.That(models.NamePlates.TryGetIndex(Other, out other) && models.NamePlates[other].Locked, Is.False);
            binder.Dispose();
        }

        private static byte[] Snapshot(uint tick, uint[] entities, bool spawn = true)
        {
            var buffer = new byte[2048];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = tick });
            foreach (var id in entities)
            {
                if (spawn)
                    Assert.That(writer.TryAddSpawn(new SpawnEvent { EntityId = id, Kind = EntityKind.Player, DefId = 1 }), Is.True);
            }
            foreach (var id in entities)
                Assert.That(writer.TryAddEntity(new EntitySnapshot { EntityId = id, XCm = (int)(id * 100), HpBucket = Shared.Protocol.Snapshot.TopHpBucket }), Is.True);
            return buffer.AsSpan(0, writer.Finish()).ToArray();
        }
    }
}
