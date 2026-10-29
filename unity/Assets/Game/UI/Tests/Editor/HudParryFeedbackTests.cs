#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Protocol;

namespace Game.UI.Tests
{
    /// <summary>
    /// fix/parry-feel: what a parry result shows. A success flashes the P square (HudSettings.parryFlashMs) and the monster
    /// whose hit was parried (hitFlashMs); a timing failure names its side (protocol 15, spec 3.3, 9.7).
    /// </summary>
    public sealed class HudParryFeedbackTests
    {
        private const uint Self = 7, Boss = 3;

        [Test]
        public void A_successful_parry_flashes_the_p_square_for_its_time()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            var config = HudConfig.From(set);
            double now = 1_000;
            var models = new HudViewModels(4, 4, config.Chat.HistoryLines, config.Chat.MaxChars);
            var binder = new HudFeedBinder(config, models, () => now);
            var feed = HudChatTests.Feed();
            binder.Bind(feed);

            feed.RaiseActionResult(3, 0, ActionReason.ParrySuccess);

            int flashMs = config.Settings.ParryFlashMs;
            Assert.That(flashMs, Is.GreaterThan(0));
            Assert.That(models.Charges.ParryFlashing(now + flashMs - 1), Is.True);
            Assert.That(models.Charges.ParryFlashing(now + flashMs), Is.False);
            binder.Dispose();
        }

        [TestCase(ActionReason.ParryFailedLate, "Parry too late.")]
        [TestCase(ActionReason.ParryFailedEarly, "Parry too early.")]
        public void A_mistimed_parry_says_which_side_and_does_not_flash(ActionReason reason, string text)
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            var config = HudConfig.From(set);
            var models = new HudViewModels(4, 4, config.Chat.HistoryLines, config.Chat.MaxChars);
            var binder = new HudFeedBinder(config, models, () => 1_000);
            var feed = HudChatTests.Feed();
            binder.Bind(feed);

            feed.RaiseActionResult(3, 0, reason);

            Assert.That((models.Toast.Visible, models.Toast.ReasonCode, models.Toast.IsParryResult), Is.EqualTo((true, reason.ToString(), true)));
            Assert.That(config.Reasons.Text(reason.ToString()), Is.EqualTo(text));
            Assert.That(models.Charges.ParryFlashing(1_000), Is.False);
            binder.Dispose();
        }

        [Test]
        public void The_monster_whose_hit_the_player_parried_flashes_and_others_do_not()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            var config = HudConfig.From(set);
            double now = 5_000;
            var models = new HudViewModels(4, 4, config.Chat.HistoryLines, config.Chat.MaxChars);
            var binder = new HudFeedBinder(config, models, () => now);
            var feed = HudChatTests.Feed();
            binder.Bind(feed);
            ApplySnapshot(feed, 10);

            feed.RaiseHits(10, new[]
            {
                new HitEntry { AttackerId = Boss, TargetId = Self, Kind = HitKind.Parried },
                new HitEntry { AttackerId = 9, TargetId = Self, Kind = HitKind.Damaged, Amount = 12 }, // not parried
            });

            Assert.That(models.HitFlashes.Flashing(Boss, now), Is.True);
            Assert.That(models.HitFlashes.Flashing(9, now), Is.False);
            Assert.That(models.HitFlashes.Flashing(Boss, now + config.Settings.HitFlashMs), Is.False);
            models.HitFlashes.Expire(now + config.Settings.HitFlashMs);
            Assert.That(models.HitFlashes.Count, Is.EqualTo(0));
            binder.Dispose();
        }

        /// <summary>The observer is the first entity of the snapshot (the registry's own entity).</summary>
        private static void ApplySnapshot(WorldClientFeed feed, uint tick)
        {
            var buffer = new byte[2048];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = tick });
            Assert.That(writer.TryAddSpawn(new SpawnEvent { EntityId = Self, Kind = EntityKind.Player, DefId = 1 }), Is.True);
            foreach (var id in new[] { Self, Boss })
                Assert.That(writer.TryAddEntity(new EntitySnapshot { EntityId = id, XCm = (int)(id * 100), HpBucket = Snapshot.TopHpBucket }), Is.True);
            Assert.That(feed.Registry.ApplySnapshot(buffer.AsSpan(0, writer.Finish()), out _, out _, out bool hasOwn) && hasOwn, Is.True);
            feed.RaiseSnapshotApplied();
        }
    }
}
