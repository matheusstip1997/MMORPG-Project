#nullable enable
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using NUnit.Framework;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace Game.UI.Tests
{
    /// <summary>HUD view models (spec 9.9, backlog I): state from messages and prediction, Changed only on a real change.</summary>
    public sealed class HudViewModelTests
    {
        private static HudConfig Committed()
        {
            Assert.That(ClientConfig.TryLoad(out var set, out var error), Is.True, error);
            return HudConfig.From(set);
        }

        [Test]
        public void Resources_change_once_per_new_value_and_not_for_a_repeat()
        {
            var model = new ResourcePanelViewModel();
            int changes = 0;
            model.Changed += () => changes++;
            var own = new OwnResources { Hp = 120, MaxHp = 150, Mp = 30, MaxMp = 30, Stamina = 80, MaxStamina = 100 };

            model.Set(own);
            model.Set(own);
            own.Hp = 100;
            model.Set(own);

            Assert.That(changes, Is.EqualTo(2));
            Assert.That((model.Hp, model.MaxHp, model.Stamina), Is.EqualTo((100, 150, 80)));
            Assert.That(ResourcePanelViewModel.Fraction(model.Hp, model.MaxHp), Is.EqualTo(100f / 150f).Within(1e-5));
            Assert.That(ResourcePanelViewModel.Fraction(5, 0), Is.EqualTo(0f));
        }

        [Test]
        public void Hotbar_cooldown_end_comes_from_the_ticks_left_and_a_steady_countdown_is_not_a_change()
        {
            var model = new HotbarViewModel();
            int changes = 0;
            model.Changed += () => changes++;
            model.SetAction(7, "skill.warrior.slash");
            Assert.That(changes, Is.EqualTo(1));

            model.SetCooldown(7, nowMs: 1_000, leftMs: 4_000, totalMs: 4_000, toleranceMs: 50);
            model.SetCooldown(7, nowMs: 1_050, leftMs: 3_950, totalMs: 4_000, toleranceMs: 50); // one tick later: same end
            model.SetCooldown(7, nowMs: 1_120, leftMs: 3_900, totalMs: 4_000, toleranceMs: 50); // frame jitter within a tick

            var slot = model.Slot(7)!;
            Assert.That(changes, Is.EqualTo(2));
            Assert.That(slot.CooldownEndMs, Is.EqualTo(5_000));
            Assert.That(slot.CooldownLeftMs(3_000), Is.EqualTo(2_000));
            Assert.That(slot.CooldownFraction(3_000), Is.EqualTo(0.5f));
            Assert.That(slot.CooldownFraction(6_000), Is.EqualTo(0f));

            model.SetCooldown(7, nowMs: 5_000, leftMs: 0, totalMs: 4_000, toleranceMs: 50); // ready
            Assert.That(changes, Is.EqualTo(3));
            Assert.That(slot.CooldownEndMs, Is.EqualTo(0));
        }

        [Test]
        public void Hotbar_batches_one_change_and_flashes_a_refused_slot()
        {
            var model = new HotbarViewModel();
            int changes = 0;
            model.Changed += () => changes++;

            model.BeginUpdate();
            model.SetClass("archer");
            model.SetAction(1, "skill.archer.dash");
            model.SetAction(7, "skill.archer.piercingShot");
            model.EndUpdate();
            model.Flash(1, nowMs: 2_000, durationMs: 400);

            Assert.That(changes, Is.EqualTo(2));
            Assert.That(model.Slot(1)!.FlashUntilMs, Is.EqualTo(2_400));
            Assert.That(model.Slot(2)!.IsEmpty, Is.True);
            Assert.That(model.Slot(0), Is.Null); // the unassigned slot has no model
            Assert.That(model.Slot(ActionBindings.MaxSkillSlots + 1), Is.Null);
        }

        [Test]
        public void Charges_change_with_the_count_or_a_moved_recharge_but_not_with_tick_jitter()
        {
            var model = new ChargesViewModel();
            int changes = 0;
            model.Changed += () => changes++;
            var full = new ChargeView(2, 2, 0, 3_000);

            model.Set(full, full, 50);
            model.Set(full, full, 50);
            model.Set(new ChargeView(1, 2, 4_000, 3_000), full, 50);
            model.Set(new ChargeView(1, 2, 4_030, 3_000), full, 50);

            Assert.That(changes, Is.EqualTo(2));
            Assert.That(model.Parry.Count, Is.EqualTo(1));
            Assert.That(model.Parry.Progress(2_500), Is.EqualTo(0.5f).Within(1e-5));
            Assert.That(model.Dodge.Progress(2_500), Is.EqualTo(1f));
        }

        [Test]
        public void Buff_row_sorts_by_registry_order_keeps_kinds_and_skips_unknown_statuses()
        {
            var config = Committed();
            var model = new BuffRowViewModel();
            int changes = 0;
            model.Changed += () => changes++;
            var statuses = new[]
            {
                new OwnStatus { NetId = 4, Stacks = 1, RemainingTicks = 80 },   // burn, sort 100, debuff
                new OwnStatus { NetId = 999, Stacks = 1, RemainingTicks = 10 }, // not in the tables
                new OwnStatus { NetId = 1, Stacks = 2, RemainingTicks = 300 },  // staminaBuff, sort 10, buff
            };

            model.Set(statuses, 100, EntityFlags.Armed, config);
            model.Set(statuses, 100, EntityFlags.Armed, config);

            Assert.That(changes, Is.EqualTo(1));
            Assert.That(model.Count, Is.EqualTo(2));
            Assert.That(model[0].StatusId, Is.EqualTo("staminaBuff"));
            Assert.That(model[0].Kind, Is.EqualTo(StatusEffectKind.Buff));
            Assert.That(model[0].Stacks, Is.EqualTo(2));
            Assert.That(model[0].EndTick, Is.EqualTo(400u));
            Assert.That(model[1].StatusId, Is.EqualTo("burn"));
            Assert.That(model[1].Kind, Is.EqualTo(StatusEffectKind.Debuff));
            Assert.That(model.Flags, Is.EqualTo(EntityFlags.Armed));

            // The next snapshot: the same statuses with fewer ticks left end at the same ticks, so nothing changed.
            statuses[0].RemainingTicks = 70;
            statuses[2].RemainingTicks = 290;
            model.Set(statuses, 110, EntityFlags.Armed, config);
            Assert.That(changes, Is.EqualTo(1));

            model.Set(System.Array.Empty<OwnStatus>(), 120, EntityFlags.None, config);
            Assert.That(changes, Is.EqualTo(2));
            Assert.That(model.Count, Is.EqualTo(0));
        }

        [Test]
        public void Boss_bar_engages_on_a_hit_and_hides_after_the_timeout_on_death_or_when_the_boss_leaves()
        {
            var model = new BossBarViewModel();
            int changes = 0;
            model.Changed += () => changes++;

            model.Engage(9, "m1.cyclopsBoss", Rank.F, 63, nowMs: 1_000);
            model.Tick(10_999, 10_000, bossPresent: true, bossDead: false);
            Assert.That(model.Visible, Is.True);
            model.Tick(11_001, 10_000, bossPresent: true, bossDead: false);
            Assert.That(model.Visible, Is.False);

            model.Engage(9, "m1.cyclopsBoss", Rank.F, 40, nowMs: 20_000);
            model.SetHp(30);
            Assert.That(model.HpFraction, Is.EqualTo(30f / Snapshot.TopHpBucket));
            model.Tick(20_100, 10_000, bossPresent: true, bossDead: true);
            Assert.That(model.Visible, Is.False);

            model.Engage(9, "m1.cyclopsBoss", Rank.F, 63, nowMs: 30_000);
            model.Tick(30_100, 10_000, bossPresent: false, bossDead: false);
            Assert.That(model.Visible, Is.False);
            Assert.That(changes, Is.EqualTo(7));
        }

        [Test]
        public void Cast_bar_shows_a_channel_once_and_reports_progress()
        {
            var model = new CastBarViewModel();
            int changes = 0;
            model.Changed += () => changes++;

            model.Show("skill.archer.heavyShot", 1_000, 2_000, 50);
            model.Show("skill.archer.heavyShot", 1_020, 2_010, 50);
            Assert.That(changes, Is.EqualTo(1));
            Assert.That(model.Progress(1_500), Is.EqualTo(0.5f));
            model.Hide();
            model.Hide();
            Assert.That(changes, Is.EqualTo(2));
        }

        [Test]
        public void Toast_replaces_the_shown_reason_and_expires()
        {
            var model = new ToastViewModel();
            int changes = 0;
            model.Changed += () => changes++;

            model.Show("OnCooldown", false, 1_000, 1_500);
            model.Show("ParrySuccess", true, 1_200, 1_500);
            model.Tick(2_600);
            Assert.That((model.Visible, model.ReasonCode, model.IsParryResult), Is.EqualTo((true, "ParrySuccess", true)));
            model.Tick(2_700);
            Assert.That(model.Visible, Is.False);
            Assert.That(changes, Is.EqualTo(3));
            Assert.That(HudFeedBinder.ReasonName(ActionReason.NoMp), Is.EqualTo("NoMp"));
        }

        [Test]
        public void Damage_number_pool_reuses_the_oldest_when_full_and_expires_by_lifetime()
        {
            var pool = new DamageNumberPool(2);
            int spawned = 0, expired = 0;
            pool.Spawned += _ => spawned++;
            pool.Expired += _ => expired++;

            int a = pool.Add(5, "10", DamageNumberStyle.Normal, 100);
            int b = pool.Add(5, "25", DamageNumberStyle.Crit, 200);
            int c = pool.Add(6, "+8", DamageNumberStyle.Heal, 300);

            Assert.That(c, Is.EqualTo(a)); // the oldest slot was reused
            Assert.That(pool.ActiveCount, Is.EqualTo(2));
            Assert.That(pool[b].Style, Is.EqualTo(DamageNumberStyle.Crit));
            Assert.That(pool[c].Text, Is.EqualTo("+8"));
            pool.Expire(1_250, 1_000); // the number from 200 ms is 1 s old; the one from 300 ms is not yet
            Assert.That((pool.ActiveCount, expired), Is.EqualTo((1, 1)));
            pool.Expire(1_400, 1_000);
            Assert.That((pool.ActiveCount, spawned, pool.TotalSpawned), Is.EqualTo((0, 3, 3L)));
        }

        [Test]
        public void Damage_numbers_show_only_hits_the_player_dealt_or_took()
        {
            var dealt = new HitEntry { AttackerId = 1, TargetId = 9, Amount = 10, Kind = HitKind.Damaged };
            var taken = new HitEntry { AttackerId = 9, TargetId = 1, Amount = 5, Kind = HitKind.Damaged };
            var others = new HitEntry { AttackerId = 7, TargetId = 9, Amount = 5, Kind = HitKind.Damaged };

            Assert.That(DamageNumberPool.Shows(dealt, 1), Is.True);
            Assert.That(DamageNumberPool.Shows(taken, 1), Is.True);
            Assert.That(DamageNumberPool.Shows(others, 1), Is.False);
            Assert.That(DamageNumberPool.Shows(dealt, 0), Is.False); // before the own entity is known
            Assert.That(NumberText.Of(42), Is.SameAs(NumberText.Of(42)));
            Assert.That(NumberText.Tenths(15), Is.EqualTo("1.5"));
        }

        [Test]
        public void Name_plates_are_pooled_and_report_the_slot_that_changed()
        {
            var plates = new NamePlateSet(2);
            var changed = new System.Collections.Generic.List<int>();
            plates.PlateChanged += changed.Add;

            Assert.That(plates.Add(10, PlateKind.Player, "", Snapshot.TopHpBucket, EntityFlags.None), Is.True);
            Assert.That(plates.Add(11, PlateKind.Monster, "Demon", Snapshot.TopHpBucket, EntityFlags.None), Is.True);
            Assert.That(plates.Add(12, PlateKind.Monster, "Demon", Snapshot.TopHpBucket, EntityFlags.None), Is.False);
            plates.SetName(10, "Arthas");
            plates.SetState(11, 40, EntityFlags.None);
            plates.SetState(11, 40, EntityFlags.None);

            Assert.That(plates.Overflows, Is.EqualTo(1));
            Assert.That(plates.TryGetIndex(10, out int index) && plates[index].Name == "Arthas", Is.True);
            Assert.That(plates.TryGetIndex(11, out int monster) && plates[monster].ShowsHpBar, Is.True);
            Assert.That(changed.Count, Is.EqualTo(4));
            plates.Remove(10);
            Assert.That(plates.Add(12, PlateKind.Boss, "Cyclops", 20, EntityFlags.None), Is.True);
            Assert.That(plates.TryGetIndex(12, out int boss) && !plates[boss].ShowsHpBar, Is.True); // bosses use the boss bar
        }

        [Test]
        public void Telegraphs_show_from_the_start_to_the_end_of_active_and_end_early_when_cut_short()
        {
            var set = new TelegraphSet();
            set.Upsert(5, 211, startTick: 100, endTick: 126, ParryColor.Red);
            set.Upsert(5, 211, startTick: 100, endTick: 126, ParryColor.Red);

            Assert.That((set.Count, set.TotalStarted), Is.EqualTo((1, 1L)));
            Assert.That(TelegraphSet.Shows(set[0], 99.5), Is.False);
            Assert.That(TelegraphSet.Shows(set[0], 100), Is.True);
            Assert.That(TelegraphSet.Shows(set[0], 125.9), Is.True);

            set.EndAt(5, 110);
            Assert.That(set[0].EndTick, Is.EqualTo(110u));
            set.EndAt(5, 120); // never later than it already ends
            Assert.That(set[0].EndTick, Is.EqualTo(110u));
            set.Expire(110);
            Assert.That(set.Count, Is.EqualTo(0));
        }

        [Test]
        public void Clearing_the_hud_hides_everything()
        {
            var models = new HudViewModels(4, 4);
            models.Resources.Set(new OwnResources { Hp = 1, MaxHp = 2, Mp = 1, MaxMp = 1, Stamina = 1, MaxStamina = 1 });
            models.BossBar.Engage(1, "m1.cyclopsBoss", Rank.F, 63, 0);
            models.DamageNumbers.Add(1, "5", DamageNumberStyle.Normal, 0);
            models.NamePlates.Add(3, PlateKind.Player, "A", 63, EntityFlags.None);

            models.Clear();

            Assert.That(models.Resources.HasValues || models.BossBar.Visible, Is.False);
            Assert.That(models.DamageNumbers.ActiveCount + models.NamePlates.ActiveCount, Is.EqualTo(0));
        }
    }
}
