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
    /// R-2.2-06 on the predictor, with the committed tables: the facing sent while a target lock holds points at the target,
    /// and R-2.2-02 still wins (during an action the predicted facing stays the one of its start while the target moves).
    /// </summary>
    public class TargetLockPredictionTests
    {
        private static (LocalPlayerPredictor predictor, TargetLock targetLock) Setup(int xCm, int yCm)
        {
            Assert.IsTrue(ClientConfig.TryLoad(out var config, out var error), error);
            var catalog = ActionCatalog.From(config);
            var predictor = new LocalPlayerPredictor(PredictionTestKit.Settings(), PredictionTestKit.OpenGrid(), catalog);
            predictor.Reconcile(new SnapshotHeader { ServerTick = 1 }, new MovementState(FixVec2.FromCm(xCm, yCm), 0));
            predictor.SetLoadout(catalog.Loadout("warrior"));
            return (predictor, new TargetLock(config.Single<TargetLockSettings>()));
        }

        private static float Cm(int raw) => raw / (float)FixedPoint.RawPerCm;

        private static ushort LockFacing(LocalPlayerPredictor predictor, TargetLock targetLock, float targetX, float targetY)
        {
            var self = predictor.State.Position;
            var candidates = new List<LockCandidate> { new LockCandidate(9, targetX, targetY) };
            if (!targetLock.IsLocked)
                targetLock.Press(0, Cm(self.X), Cm(self.Y), candidates);
            else
                targetLock.Refresh(Cm(self.X), Cm(self.Y), candidates);
            Assert.IsTrue(targetLock.TryFacing(Cm(self.X), Cm(self.Y), out ushort facing));
            return facing;
        }

        [Test]
        public void The_facing_sent_while_locked_points_at_the_target()
        {
            var (predictor, targetLock) = Setup(1000, 1000);

            ushort facing = LockFacing(predictor, targetLock, 1300, 1300); // north-east of the player
            var frame = predictor.Predict(MoveKeys.None, facing);

            Assert.AreEqual(9u, targetLock.LockedId);
            Assert.AreEqual(8192, facing);
            Assert.AreEqual(Quantize.RoundTripAngle(8192, InputPacket.FacingBits), frame.Facing);
            Assert.AreEqual(frame.Facing, predictor.State.Facing);
        }

        [Test]
        public void The_lock_turns_the_facing_with_the_target_while_moving()
        {
            var (predictor, targetLock) = Setup(0, 0);
            LockFacing(predictor, targetLock, 0, 800);

            // The player walks east under the target: the facing swings from north toward north-west.
            for (int i = 0; i < 20; i++)
                predictor.Predict(MoveKeys.D, LockFacing(predictor, targetLock, 0, 800));
            var self = predictor.State.Position;
            TargetLock.TryFacingToward(0 - Cm(self.X), 800 - Cm(self.Y), out ushort expected);
            var frame = predictor.Predict(MoveKeys.None, LockFacing(predictor, targetLock, 0, 800));

            Assert.Greater(Cm(self.X), 100f);
            Assert.AreEqual(Quantize.RoundTripAngle(expected, InputPacket.FacingBits), frame.Facing);
        }

        [Test]
        public void During_an_action_the_facing_of_its_start_wins_over_the_lock()
        {
            var (predictor, targetLock) = Setup(0, 0);
            ushort east = LockFacing(predictor, targetLock, 500, 0);
            predictor.Predict(MoveKeys.None, east);
            predictor.Predict(MoveKeys.None, east, InputActions.Attack);
            Assert.IsTrue(predictor.Actions.IsBusy);
            ushort locked = predictor.State.Facing;

            // The target runs to the north of the player; the lock follows it, the running attack does not (R-2.2-02).
            ushort north = LockFacing(predictor, targetLock, 0, 500);
            var frame = predictor.Predict(MoveKeys.None, north);

            Assert.AreEqual(0, north);
            Assert.AreEqual(0, frame.Facing); // the frame carries the lock's facing ...
            Assert.IsTrue(predictor.Actions.IsBusy);
            Assert.AreEqual(locked, predictor.State.Facing); // ... the action keeps its own
            Assert.AreEqual(Quantize.RoundTripAngle(16384, InputPacket.FacingBits), locked);
        }
    }
}
