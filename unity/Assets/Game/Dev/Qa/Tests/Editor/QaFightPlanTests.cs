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

        [TestCase("warrior")]
        [TestCase("archer")]
        [TestCase("mage")]
        [TestCase("assist")]
        public void The_normal_attack_carries_its_chain_and_one_press_is_due_per_hit_in_its_window(string classId)
        {
            // R-3.1-10: the kill step presses through the four hits, once per hit from its chainFromMs.
            var config = Config();
            var attack = Kit(classId).Single(a => a.Slot == 0);
            Assert.That(attack.Chain.Select(hit => hit.Id),
                Is.EqualTo(new[] { attack.Id + ".2", attack.Id + ".3", attack.Id + ".4" }));
            Assert.That(attack.ChainTotalMs, Is.EqualTo(attack.Def.TotalMs() + attack.Chain.Sum(hit => hit.TotalMs())));
            Assert.That(Kit(classId).Where(a => a.Slot != 0).All(a => a.Chain.Count == 0));

            var catalog = Shared.Simulation.Actions.ActionCatalog.From(config);
            var machine = new Shared.Simulation.Actions.ActionMachineState(catalog, catalog.Loadout(classId));
            var fired = new Shared.Simulation.Actions.FiredEffect[Shared.Simulation.Actions.ActionMachine.MaxFiredPerStep];
            var resources = Shared.Simulation.Actions.UnlimitedActionResources.Instance;
            Shared.Simulation.Actions.ActionMachine.Step(machine,
                new Shared.Simulation.Actions.ActionInput(true, Shared.Protocol.InputActions.Attack, 0, 0, 1), resources, fired);
            var hit1 = machine.Action!;
            Assert.That(hit1.Id, Is.EqualTo(attack.Id));
            while (!QaFightPlan.ChainPressDue(machine, -1))
            {
                Assert.That(machine.ActionIndex, Is.EqualTo(hit1.Index), "the window closed without a press being due");
                Shared.Simulation.Actions.ActionMachine.Step(machine,
                    new Shared.Simulation.Actions.ActionInput(true, Shared.Protocol.InputActions.None, 0, 0, 2), resources, fired);
            }
            Assert.That(machine.Elapsed, Is.EqualTo(hit1.ChainFromTick));
            Assert.IsFalse(QaFightPlan.ChainPressDue(machine, hit1.Index)); // already pressed for this hit
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
            // QA wave 5 H1: Life Regeneration heals over time, so it is the heal while Heal Wave cools down.
            Assert.That(QaFightPlan.Choose(assist, State(hpPercent: low, cooling: "skill.assist.healWave")).Action!.Id,
                Is.EqualTo("skill.assist.lifeRegeneration"));
            var noHeals = new[] { "skill.assist.healWave", "skill.assist.lifeRegeneration" };
            Assert.That(QaFightPlan.Choose(assist, State(hpPercent: low, cooling: noHeals)).Action!.Id,
                Is.EqualTo("skill.assist.holyStrike"));
            Assert.That(QaFightPlan.Choose(assist, State(hpPercent: critical, cooling: noHeals)).Move,
                Is.EqualTo(QaFightMove.BackOff));
            // A kit without a heal (the warrior's) fights on when low and backs off when critical.
            var warrior = Kit("warrior");
            Assert.That(QaFightPlan.Choose(warrior, State(hpPercent: low)).Move, Is.EqualTo(QaFightMove.Use));
            Assert.That(QaFightPlan.Choose(warrior, State(hpPercent: critical)).Move, Is.EqualTo(QaFightMove.BackOff));
        }

        [Test]
        public void Low_hp_with_a_heal_short_only_of_stamina_spends_none_until_the_heal_is_paid()
        {
            // QA wave 6 F2: the Assist kept attacking below the heal line, each attack restarted the stamina delay, and the
            // heals waited until HP 27 (Life Regeneration never came).
            var assist = Kit("assist");
            var regen = QaStaminaRegen.From(Config());
            int low = QaTimeouts.HealBelowPercent - 1;
            var healWave = assist.Single(a => a.Id == "skill.assist.healWave");
            QaFightState Low(int stamina, long sinceSpendMs, int mp = 1000, params string[] cooling) =>
                new QaFightState(low, 100, mp, stamina, 100, a => !cooling.Contains(a.Id), null, regen, sinceSpendMs);

            var wait = QaFightPlan.Choose(assist, Low(stamina: 5, sinceSpendMs: 0));
            Assert.That((wait.Move, wait.Action!.Id), Is.EqualTo((QaFightMove.WaitForStamina, "skill.assist.lifeRegeneration")));
            // The wait is read from the tables: the rest of the delay, then the missing stamina at the regeneration rate.
            int lifeRegenCost = assist.Single(a => a.Id == "skill.assist.lifeRegeneration").Def.StaminaCost;
            Assert.That(wait.WaitMs, Is.EqualTo(regen.DelayMs + ((lifeRegenCost - 5) * 1000 + regen.PerSec - 1) / regen.PerSec));
            var later = QaFightPlan.Choose(assist, Low(stamina: 5, sinceSpendMs: regen.DelayMs, cooling: "skill.assist.lifeRegeneration"));
            Assert.That((later.Move, later.Action!.Id), Is.EqualTo((QaFightMove.WaitForStamina, "skill.assist.healWave")));
            Assert.That(later.WaitMs, Is.EqualTo(((healWave.Def.StaminaCost - 5) * 1000 + regen.PerSec - 1) / regen.PerSec));
            // Paid: it heals. Short of MP or with both heals cooling down, waiting does not help: it fights on.
            Assert.That(QaFightPlan.Choose(assist, Low(stamina: healWave.Def.StaminaCost, sinceSpendMs: 0)).Action!.Id,
                Is.EqualTo("skill.assist.healWave"));
            Assert.That(QaFightPlan.Choose(assist, Low(stamina: 12, sinceSpendMs: 0, mp: 0)).Action!.Id, Is.EqualTo("attack.assist"));
            Assert.That(QaFightPlan.Choose(assist, Low(stamina: 12, sinceSpendMs: 0, cooling: new[] { "skill.assist.healWave", "skill.assist.lifeRegeneration" }))
                .Action!.Id, Is.EqualTo("attack.assist"));
            // Above the heal line nothing is held back.
            Assert.That(QaFightPlan.Choose(assist, new QaFightState(100, 100, 1000, 12, 100, _ => true, null, regen, 0)).Action!.Id,
                Is.EqualTo("attack.assist"));
            Assert.That(regen.MsUntil(30, 25, 0), Is.EqualTo(0));
        }

        [Test]
        public void Self_statuses_are_read_from_the_tables_heals_over_time_count_as_heals_and_buffs_open_the_fight()
        {
            // QA wave 5 H1.
            var regen = Kit("assist").Single(a => a.Id == "skill.assist.lifeRegeneration");
            Assert.That((regen.HealsSelf, regen.OpensFight, regen.SelfStatusId), Is.EqualTo((true, false, "lifeRegeneration")));
            var stamina = Kit("warrior").Single(a => a.Id == "skill.warrior.staminaBuff");
            Assert.That((stamina.HealsSelf, stamina.OpensFight, stamina.SelfStatusId), Is.EqualTo((false, true, "staminaBuff")));
            Assert.IsTrue(Kit("mage").Single(a => a.Id == "skill.mage.magicBarrier").OpensFight);
            Assert.IsFalse(Kit("assist").Single(a => a.Id == "skill.assist.healWave").OpensFight);
        }

        [Test]
        public void A_ready_opening_buff_goes_first_once_and_low_hp_still_heals_first()
        {
            var mage = Kit("mage");
            var opening = new HashSet<string> { "skill.mage.magicBarrier" };
            QaFightState Opening(int hpPercent = 100, params string[] cooling) =>
                new QaFightState(hpPercent, 100, 1000, 100, 800, a => !cooling.Contains(a.Id), a => opening.Contains(a.Id));
            Assert.That(QaFightPlan.Choose(mage, Opening()).Action!.Id, Is.EqualTo("skill.mage.magicBarrier"));
            Assert.That(QaFightPlan.Choose(mage, Opening(cooling: "skill.mage.magicBarrier")).Action!.Id, Is.EqualTo("skill.mage.iceShard"));
            opening.Clear(); // cast: the fight goes on
            Assert.That(QaFightPlan.Choose(mage, Opening()).Action!.Id, Is.EqualTo("skill.mage.iceShard"));
            Assert.That(QaFightPlan.Choose(mage, State(distanceCm: 800)).Action!.Id, Is.EqualTo("skill.mage.iceShard")); // no opening
            var assist = Kit("assist");
            opening.Add("skill.assist.lifeRegeneration"); // not an opening buff: it heals
            Assert.That(QaFightPlan.Choose(assist, Opening()).Action?.Id, Is.Not.EqualTo("skill.assist.lifeRegeneration"));
            // Low HP: the heal goes first, opening or not.
            Assert.That(QaFightPlan.Choose(assist, Opening(hpPercent: QaTimeouts.HealBelowPercent - 1)).Action!.Id,
                Is.EqualTo("skill.assist.healWave"));
        }

        [Test]
        public void Jump_strike_reaches_its_dash_plus_its_circle_and_steps_back_when_the_demon_is_too_close()
        {
            // QA wave 5 H3: a 400 cm dash in Startup, then a 150 cm circle around the landing point.
            var jump = Kit("warrior").Single(a => a.Id == "skill.warrior.jumpStrike");
            Assert.That((jump.MinReachCm, jump.ReachCm, jump.PreferredCm), Is.EqualTo((250, 550, 400)));
            var warrior = Kit("warrior");
            var close = QaFightPlan.Choose(warrior, State(distanceCm: 100));
            Assert.That((close.Move, close.Action!.Id), Is.EqualTo((QaFightMove.StepBack, "skill.warrior.jumpStrike")));
            var room = QaFightPlan.Choose(warrior, State(distanceCm: 400));
            Assert.That((room.Move, room.Action!.Id), Is.EqualTo((QaFightMove.Use, "skill.warrior.jumpStrike")));
            // Cooling down, or low on HP: no stepping back.
            Assert.That(QaFightPlan.Choose(warrior, State(distanceCm: 100, cooling: "skill.warrior.jumpStrike")).Move, Is.EqualTo(QaFightMove.Use));
            Assert.That(QaFightPlan.Choose(warrior, State(hpPercent: QaTimeouts.HealBelowPercent - 1, distanceCm: 100)).Move,
                Is.EqualTo(QaFightMove.Use));
            // A skill without a dash keeps its plain reach.
            Assert.That(warrior.Single(a => a.Id == "skill.warrior.slash").MinReachCm, Is.EqualTo(0));
        }

        [Test]
        public void Moves_and_utilities_are_read_from_the_tables_for_the_pre_fight_presses()
        {
            // QA wave 6 F3: Dash, Back Step and Simple Cleanse were never pressed.
            var archer = Kit("archer");
            var dash = archer.Single(a => a.Id == "skill.archer.dash");
            var backStep = archer.Single(a => a.Id == "skill.archer.backStep");
            Assert.That((dash.Moves, dash.DashCm, dash.DashOffsetDeg), Is.EqualTo((true, 500, 180))); // no key held in the tour: backward (R-3.3-02)
            Assert.That((backStep.Moves, backStep.DashCm, backStep.DashOffsetDeg), Is.EqualTo((true, 400, 180)));
            Assert.That(archer.Where(a => a.Moves || a.Utility).Select(a => a.Id), Is.EquivalentTo(new[] { dash.Id, backStep.Id }));
            var assist = Kit("assist");
            Assert.That(assist.Where(a => a.Moves || a.Utility).Select(a => a.Id), Is.EqualTo(new[] { "skill.assist.simpleCleanse" }));
            Assert.That(Kit("warrior").Concat(Kit("mage")).Any(a => a.Moves || a.Utility), Is.False);
            // Jump Strike dashes too, but it damages: the fight uses it.
            Assert.That(Kit("warrior").Single(a => a.Id == "skill.warrior.jumpStrike").Moves, Is.False);
        }

        [TestCase("warrior")]
        [TestCase("archer")]
        [TestCase("mage")]
        [TestCase("assist")]
        public void Every_classes_dodge_is_a_move_of_the_starting_values_length(string classId)
        {
            // First play session P2 (R-3.3-02, spec 12.7): walk-to-demon presses it and checks the server position.
            var config = Config();
            var dodge = QaFightPlan.Dodge(config, ActionBindings.For(config, classId)!);

            Assert.That((dodge.Id, dodge.Slot, dodge.Button, dodge.Moves), Is.EqualTo(("dodge", QaFightPlan.DodgeSlot, "Dodge", true)));
            Assert.That(dodge.DashCm, Is.EqualTo(config.Single<StartingValuesM1>().DodgeDistanceCm));
            Assert.That(Kit(classId).Any(a => a.Id == dodge.Id), Is.False, "the dodge is not a kit action of the fight");
        }

        [Test]
        public void A_damaging_skill_not_fired_yet_goes_first_and_the_player_closes_in_when_it_is_out_of_reach()
        {
            // QA wave 6 F3: the Mage fought at about 14 m and never came within Flamethrower's 350 cm.
            var mage = Kit("mage");
            var flamethrower = mage.Single(a => a.Id == "skill.mage.flamethrower");
            Assert.That((flamethrower.Damages, flamethrower.ReachCm), Is.EqualTo((true, 350)));
            QaFightState Fresh(double distanceCm, int hpPercent = 100, params string[] fired) =>
                new QaFightState(hpPercent, 100, 1000, 100, distanceCm, _ => true, null, default, long.MaxValue, a => !fired.Contains(a.Id));
            var allButFlamethrower = mage.Where(a => a.Id != flamethrower.Id).Select(a => a.Id).ToArray();

            var far = QaFightPlan.Choose(mage, Fresh(800, 100, allButFlamethrower));
            Assert.That((far.Move, far.Action!.Id), Is.EqualTo((QaFightMove.CloseIn, flamethrower.Id)));
            var near = QaFightPlan.Choose(mage, Fresh(300, 100, allButFlamethrower));
            Assert.That((near.Move, near.Action!.Id), Is.EqualTo((QaFightMove.Use, flamethrower.Id)));
            // Not fired yet goes before stronger ones; once all fired, the strongest in reach again.
            Assert.That(QaFightPlan.Choose(mage, Fresh(800, 100, "skill.mage.iceShard")).Action!.Id, Is.EqualTo("skill.mage.fireSpark"));
            Assert.That(QaFightPlan.Choose(mage, Fresh(800, 100, mage.Select(a => a.Id).ToArray())).Action!.Id, Is.EqualTo("skill.mage.iceShard"));
            // Low HP: no closing in.
            Assert.That(QaFightPlan.Choose(mage, Fresh(800, QaTimeouts.HealBelowPercent - 1, allButFlamethrower)).Move,
                Is.Not.EqualTo(QaFightMove.CloseIn));
            // The Warrior's strongest, Jump Strike, goes first even though the demon is too close for its dash: a step back
            // (in wave 6 it landed in every warrior fight, QA 3-H3), not the weaker skills in reach.
            var warrior = Kit("warrior");
            var jump = warrior.Single(a => a.Id == "skill.warrior.jumpStrike");
            Assert.That(warrior.Where(a => a.Damages).Max(a => a.DamagePercent), Is.EqualTo(jump.DamagePercent));
            var allFresh = new QaFightState(100, 100, 1000, 100, 100, _ => true, null, default, long.MaxValue, _ => true);
            var close = QaFightPlan.Choose(warrior, allFresh);
            Assert.That((close.Move, close.Action!.Id), Is.EqualTo((QaFightMove.StepBack, jump.Id)));
            // Fired: the next strongest in reach.
            var jumped = new QaFightState(100, 100, 1000, 100, 100, _ => true, null, default, long.MaxValue, a => a.Id != jump.Id);
            Assert.That(QaFightPlan.Choose(warrior, jumped).Action!.Id, Is.EqualTo("skill.warrior.piercingAttack"));
        }

        [Test]
        public void After_a_back_off_it_fights_again_at_the_re_engage_line_or_with_a_heal_ready()
        {
            // QA wave 5 H2.
            var warrior = Kit("warrior");
            Assert.IsFalse(QaFightPlan.Recovered(warrior, State(hpPercent: QaTimeouts.ReengageAbovePercent - 1)));
            Assert.IsTrue(QaFightPlan.Recovered(warrior, State(hpPercent: QaTimeouts.ReengageAbovePercent)));
            var assist = Kit("assist");
            Assert.IsTrue(QaFightPlan.Recovered(assist, State(hpPercent: 10)));
            Assert.IsFalse(QaFightPlan.Recovered(assist, State(hpPercent: 10, cooling: new[] { "skill.assist.healWave", "skill.assist.lifeRegeneration" })));
        }
    }
}
