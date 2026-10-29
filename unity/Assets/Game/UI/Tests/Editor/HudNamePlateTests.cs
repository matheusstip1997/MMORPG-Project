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

        private const uint Demon = 20, Boss = 21, Player = 22;
        private const ushort DemonNetId = 1, BossNetId = 3;

        [Test]
        public void A_staggered_monster_shows_the_marker_for_the_stagger_on_the_render_clock_and_never_after()
        {
            // R-3.3-01 poise break, spec 9.3 stagger_marker (user request 2026-10-26): monsters and bosses only, from the
            // snapshot's stagger start and length, drawn like the stagger animation on the render clock.
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            var config = HudConfig.From(set);
            Assert.That(ClientWorld.TryLoadGrid("m1", out var grid, out error), Is.True, error);
            var settings = PredictionSettings.From(set);
            var catalog = ActionCatalog.From(set);
            var feed = new WorldClientFeed(set, catalog, new EntityRegistry(settings), new LocalPlayerPredictor(settings, grid!, catalog),
                null, null, settings.TickMs);
            var models = new HudViewModels(8, 4, config.Chat.HistoryLines, config.Chat.MaxChars);
            var binder = new HudFeedBinder(config, models, () => 0);
            binder.Bind(feed);
            var plates = models.NamePlates;

            // Tick 100: the demon, the boss and another player all staggered 4 ticks ago for 30 ticks (ticks 96 to 126).
            Apply(feed, 100, staggered: true, age: 4, spawn: true);
            Assert.That(plates.TryGetIndex(Demon, out int demon), Is.True);
            Assert.That(plates.TryGetIndex(Boss, out int boss), Is.True);
            Assert.That(plates.TryGetIndex(Player, out int player), Is.True);
            Assert.That(plates[boss].Kind, Is.EqualTo(PlateKind.Boss));

            feed.SetRenderTick(95.5); // drawn 100 ms behind: the stagger has not started on screen yet
            binder.Frame(0);
            Assert.That(plates[demon].Staggered, Is.False);

            feed.SetRenderTick(96);
            binder.Frame(0);
            Assert.That(plates[demon].Staggered && plates[boss].Staggered, Is.True, "monsters and bosses show the marker");
            Assert.That(plates[player].Staggered, Is.False, "players never show it");
            Assert.That(plates[demon].ShowsHpBar, Is.False, "the HP bar keeps its own rule");

            // The server's stagger ends at tick 126; newer snapshots show the demon idle, the render clock is still inside.
            Apply(feed, 128, staggered: false, age: 0, spawn: false);
            feed.SetRenderTick(125.9);
            binder.Frame(0);
            Assert.That(plates[demon].Staggered, Is.True, "shown for the whole stagger on the render clock");

            feed.SetRenderTick(126);
            binder.Frame(0);
            Assert.That(plates[demon].Staggered || plates[boss].Staggered, Is.False, "gone when the stagger ends");
            binder.Dispose();
        }

        [Test]
        public void Death_cuts_the_marker_short_and_leaving_view_drops_it()
        {
            var plates = new NamePlateSet(4);
            plates.Add(Demon, PlateKind.Monster, "Demon", Full, EntityFlags.None);
            plates.Add(Boss, PlateKind.Boss, "Boss", Full, EntityFlags.None);
            plates.Add(Player, PlateKind.Player, "Aria", Full, EntityFlags.None);
            Assert.That(plates.TryGetIndex(Demon, out int demon), Is.True);
            Assert.That(plates.TryGetIndex(Player, out int player), Is.True);
            plates.SetStagger(Demon, 100, 30);
            plates.SetStagger(Boss, 100, 30);
            plates.SetStagger(Player, 100, 30);
            plates.UpdateStaggers(101);
            Assert.That(plates[demon].Staggered, Is.True);
            Assert.That(plates[player].Staggered, Is.False);

            // The demon dies at tick 110 (its snapshot is no longer staggered): drawn until the render clock reaches it.
            plates.EndStaggerAt(Demon, 110);
            plates.UpdateStaggers(109.5);
            Assert.That(plates[demon].Staggered, Is.True);
            plates.UpdateStaggers(110);
            Assert.That(plates[demon].Staggered, Is.False);

            // The boss leaves view while staggered: its plate goes, marker included.
            plates.Remove(Boss);
            Assert.That(plates.TryGetIndex(Boss, out _), Is.False);
            plates.UpdateStaggers(111);
            plates.Add(Boss, PlateKind.Boss, "Boss", Full, EntityFlags.None);
            plates.UpdateStaggers(112);
            Assert.That(plates.TryGetIndex(Boss, out int boss) && !plates[boss].Staggered, Is.True, "a plate that comes back starts clean");
        }

        private static void Apply(WorldClientFeed feed, uint tick, bool staggered, byte age, bool spawn)
        {
            var buffer = new byte[2048];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = tick });
            if (spawn)
            {
                Assert.That(writer.TryAddSpawn(new SpawnEvent { EntityId = Self, Kind = EntityKind.Player, DefId = 1 }), Is.True);
                Assert.That(writer.TryAddSpawn(new SpawnEvent { EntityId = Demon, Kind = EntityKind.Monster, DefId = DemonNetId }), Is.True);
                Assert.That(writer.TryAddSpawn(new SpawnEvent { EntityId = Boss, Kind = EntityKind.Monster, DefId = BossNetId }), Is.True);
                Assert.That(writer.TryAddSpawn(new SpawnEvent { EntityId = Player, Kind = EntityKind.Player, DefId = 1 }), Is.True);
            }
            foreach (var id in new[] { Self, Demon, Boss, Player })
            {
                var entity = new EntitySnapshot { EntityId = id, XCm = (int)(id * 100), HpBucket = Shared.Protocol.Snapshot.TopHpBucket };
                if (staggered && id != Self)
                {
                    entity.State = (byte)ActionState.Staggered;
                    entity.StateAge = age;
                    entity.StateDurationTicks = 30;
                }
                Assert.That(writer.TryAddEntity(entity), Is.True);
            }
            Assert.That(feed.Registry.ApplySnapshot(buffer.AsSpan(0, writer.Finish()), out _, out _, out _), Is.True);
            feed.RaiseSnapshotApplied();
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
