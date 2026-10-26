#nullable enable
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;

namespace Game.Dev.Qa.EditorTests
{
    /// <summary>The kill step's fight choice (QaFightPlan) on the committed kits: one rule for every class.</summary>
    public sealed class QaFightPlanTests
    {
        private static ConfigSet Config()
        {
            Assert.IsTrue(ClientConfig.TryLoad(out var config, out var error), error);
            return config;
        }

        private static List<QaKitAction> Kit(string classId)
        {
            var config = Config();
            return QaFightPlan.Kit(config, ActionBindings.For(config, classId)!);
        }

        /// <summary>Full meters by default; <paramref name="cooling"/> names the actions still on cooldown.</summary>
        private static QaFightState State(int hpPercent = 100, double distanceCm = 100, int mp = 1000, int stamina = 100,
            params string[] cooling) =>
            new QaFightState(hpPercent, 100, mp, stamina, distanceCm, a => !cooling.Contains(a.Id));

        [Test]
        public void The_kit_is_the_bindings_row_with_damage_reach_and_heals_from_the_tables()
        {
            var kit = Kit("assist");
            Assert.That(kit.Select(a => (a.Id, a.Button)), Is.EqualTo(new[]
            {
                ("attack.assist", "Attack"), ("skill.assist.lifeRegeneration", "Hotbar1"), ("skill.assist.holyStrike", "Hotbar7"),
                ("skill.assist.healWave", "Hotbar8"), ("skill.assist.flash", "Hotbar9"), ("skill.assist.simpleCleanse", "Hotbar10"),
            }));
            var holy = kit.Single(a => a.Id == "skill.assist.holyStrike");
            Assert.That((holy.DamagePercent, holy.ReachCm), Is.EqualTo((160, 640))); // a 6 m beam, 40 cm around it
            Assert.IsTrue(kit.Single(a => a.Id == "skill.assist.healWave").HealsSelf);
            Assert.IsFalse(kit.Single(a => a.Id == "skill.assist.simpleCleanse").Damages);
            Assert.That(Kit("archer").Single(a => a.Id == "skill.archer.multiShot").DamagePercent, Is.EqualTo(210)); // 3 x 70
        }

        [Test]
        public void It_uses_the_strongest_ready_skill_in_reach_and_the_normal_attack_in_between()
        {
            var kit = Kit("assist");
            Assert.That(QaFightPlan.Choose(kit, State()).Action!.Id, Is.EqualTo("skill.assist.holyStrike"));
            Assert.That(QaFightPlan.Choose(kit, State(cooling: "skill.assist.holyStrike")).Action!.Id, Is.EqualTo("skill.assist.flash"));
            Assert.That(QaFightPlan.Choose(kit, State(cooling: new[] { "skill.assist.holyStrike", "skill.assist.flash" })).Action!.Id,
                Is.EqualTo("attack.assist"));
            // Out of the staff's 2 m cone, inside Flash's 3 m circle.
            Assert.That(QaFightPlan.Choose(kit, State(distanceCm: 250, cooling: "skill.assist.holyStrike")).Action!.Id,
                Is.EqualTo("skill.assist.flash"));
        }

        [Test]
        public void A_skill_it_cannot_pay_for_is_skipped_and_nothing_affordable_means_wait()
        {
            var kit = Kit("mage");
            Assert.That(QaFightPlan.Choose(kit, State(distanceCm: 800)).Action!.Id, Is.EqualTo("skill.mage.iceShard")); // 150%
            Assert.That(QaFightPlan.Choose(kit, State(distanceCm: 800, mp: 6)).Action!.Id, Is.EqualTo("skill.mage.fireSpark"));
            Assert.That(QaFightPlan.Choose(kit, State(distanceCm: 800, mp: 0)).Action!.Id, Is.EqualTo("attack.mage"));
            Assert.That(QaFightPlan.Choose(kit, State(distanceCm: 800, stamina: 5)).Move, Is.EqualTo(QaFightMove.Wait));
        }

        [Test]
        public void Low_hp_uses_a_ready_heal_else_backs_off_below_the_back_off_line()
        {
            var assist = Kit("assist");
            int low = QaTimeouts.HealBelowPercent - 1, critical = QaTimeouts.BackOffBelowPercent - 1;
            Assert.That(QaFightPlan.Choose(assist, State(hpPercent: low)).Action!.Id, Is.EqualTo("skill.assist.healWave"));
            Assert.That(QaFightPlan.Choose(assist, State(hpPercent: low, cooling: "skill.assist.healWave")).Action!.Id,
                Is.EqualTo("skill.assist.holyStrike"));
            Assert.That(QaFightPlan.Choose(assist, State(hpPercent: critical, cooling: "skill.assist.healWave")).Move,
                Is.EqualTo(QaFightMove.BackOff));
            // A kit without a heal (the warrior's) fights on when low and backs off when critical.
            var warrior = Kit("warrior");
            Assert.That(QaFightPlan.Choose(warrior, State(hpPercent: low)).Move, Is.EqualTo(QaFightMove.Use));
            Assert.That(QaFightPlan.Choose(warrior, State(hpPercent: critical)).Move, Is.EqualTo(QaFightMove.BackOff));
        }
    }
}
