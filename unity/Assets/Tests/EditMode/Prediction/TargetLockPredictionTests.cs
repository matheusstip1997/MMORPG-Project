using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Input;
using Game.Gameplay.Prediction;
using NUnit.Framework;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace Game.Tests.EditMode.Prediction
{
    /// <summary>
    /// R-2.2-06, M3 decision 16 on the predictor, with the committed tables: a lock never changes the walk; attacks, skills and
    /// the parry aim at the locked target's drawn position (the server resolves the same from its history), ground skills at
    /// the mouse, move skills along the keys; R-2.2-02 still wins (a running action keeps the facing of its start).
    /// </summary>
    public class TargetLockPredictionTests
    {
        private const ushort Mouse = 40_000;

        private static (LocalPlayerPredictor predictor, TargetLock targetLock, ActionCatalog catalog) Setup(int xCm, int yCm,
            string classId = "warrior")
        {
            Assert.IsTrue(ClientConfig.TryLoad(out var config, out var error), error);
            var catalog = ActionCatalog.From(config);
            var predictor = new LocalPlayerPredictor(PredictionTestKit.Settings(), PredictionTestKit.OpenGrid(), catalog);
            predictor.Reconcile(new SnapshotHeader { ServerTick = 1 }, new MovementState(FixVec2.FromCm(xCm, yCm), 0));
            predictor.SetLoadout(catalog.Loadout(classId));
            predictor.SetEquippedWeapons(uint.MaxValue);
            return (predictor, new TargetLock(config.Single<TargetLockSettings>()), catalog);
        }

        private static float Cm(int raw) => raw / (float)FixedPoint.RawPerCm;

        /// <summary>Locks (or refreshes) the candidate 9 at the drawn position, and returns where the lock aims from.</summary>
        private static FixVec2 LockAt(LocalPlayerPredictor predictor, TargetLock targetLock, int targetXCm, int targetYCm)
        {
            var self = predictor.State.Position;
            var candidates = new List<LockCandidate> { new LockCandidate(9, targetXCm, targetYCm) };
            if (!targetLock.IsLocked)
                targetLock.Press(0, Cm(self.X), Cm(self.Y), candidates);
            else
                targetLock.Refresh(Cm(self.X), Cm(self.Y), candidates);
            Assert.IsTrue(targetLock.TryTargetPosition(out float x, out float y));
            return FixVec2.FromCm((int)x, (int)y);
        }

        [Test]
        public void The_committed_tables_aim_ground_skills_at_the_mouse_and_move_skills_along_the_keys()
        {
            var (_, _, catalog) = Setup(0, 0);
            Assert.AreEqual(ActionAim.Ground, catalog.Find("skill.mage.icePike")!.Aim);
            Assert.AreEqual(ActionAim.Ground, catalog.Find("skill.mage.flamethrower")!.Aim);
            Assert.AreEqual(ActionAim.Body, catalog.Find("dodge")!.Aim);
            Assert.AreEqual(ActionAim.Body, catalog.Find("skill.archer.dash")!.Aim);
            Assert.AreEqual(ActionAim.Body, catalog.Find("skill.archer.backStep")!.Aim);
            Assert.AreEqual(ActionAim.Target, catalog.Find("attack.warrior")!.Aim);
            Assert.AreEqual(ActionAim.Target, catalog.Find("parry")!.Aim);
            Assert.AreEqual(ActionAim.Target, catalog.Find("skill.warrior.jumpStrike")!.Aim);
        }

        [Test]
        public void A_locked_attack_turns_the_body_to_the_target_and_the_walk_ignores_the_lock()
        {
            var (predictor, targetLock, _) = Setup(1000, 1000);
            var target = LockAt(predictor, targetLock, 1300, 1300); // north-east of the player

            var frame = predictor.Predict(MoveKeys.None, Mouse, locked: true, lockTarget: target);
            Assert.IsTrue(frame.Locked);
            Assert.AreEqual(0, predictor.State.Facing); // standing: the lock does not turn the body

            predictor.Predict(MoveKeys.None, Mouse, InputActions.Attack, locked: true, lockTarget: target);
            Assert.AreEqual(8192, predictor.Actions.Facing);
            Assert.AreEqual(8192, predictor.State.Facing);
        }

        [Test]
        public void A_locked_walk_goes_along_the_keys_and_faces_them()
        {
            var (predictor, targetLock, _) = Setup(0, 0);
            for (int i = 0; i < 20; i++)
                predictor.Predict(MoveKeys.D, Mouse, locked: true, lockTarget: LockAt(predictor, targetLock, 0, 800));

            Assert.AreEqual(16_384, predictor.State.Facing);
            Assert.Greater(Cm(predictor.State.Position.X), 100f);
            Assert.AreEqual(0, predictor.State.Position.Y);
        }

        [Test]
        public void A_locked_ground_skill_turns_the_body_to_the_mouse()
        {
            var (predictor, targetLock, _) = Setup(0, 0, "mage");
            var target = LockAt(predictor, targetLock, 500, 0);

            predictor.Predict(MoveKeys.None, Mouse, skillSlot: 10, locked: true, lockTarget: target); // Flamethrower

            Assert.IsTrue(predictor.Actions.IsBusy);
            Assert.AreEqual(Quantize.RoundTripAngle(Mouse, InputPacket.AimBits), predictor.Actions.Facing);
        }

        [Test]
        public void A_target_out_of_view_leaves_the_press_at_the_mouse()
        {
            var (predictor, _, _) = Setup(0, 0);
            predictor.Predict(MoveKeys.None, Mouse, InputActions.Attack, locked: true, lockTarget: null);
            Assert.AreEqual(Quantize.RoundTripAngle(Mouse, InputPacket.AimBits), predictor.Actions.Facing);
        }

        [Test]
        public void During_an_action_the_facing_of_its_start_wins_over_the_lock()
        {
            var (predictor, targetLock, _) = Setup(0, 0);
            var east = LockAt(predictor, targetLock, 500, 0);
            predictor.Predict(MoveKeys.None, Mouse, InputActions.Attack, locked: true, lockTarget: east);
            Assert.IsTrue(predictor.Actions.IsBusy);
            Assert.AreEqual(16_384, predictor.State.Facing);

            // The target runs to the north of the player; the running attack keeps its facing (R-2.2-02).
            predictor.Predict(MoveKeys.None, Mouse, locked: true, lockTarget: LockAt(predictor, targetLock, 0, 500));

            Assert.IsTrue(predictor.Actions.IsBusy);
            Assert.AreEqual(16_384, predictor.State.Facing);
        }
    }
}
