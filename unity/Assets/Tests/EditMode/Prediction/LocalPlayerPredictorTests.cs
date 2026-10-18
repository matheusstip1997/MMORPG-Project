using Game.Gameplay.Prediction;
using NUnit.Framework;
using Shared.Protocol;
using Shared.Simulation;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace Game.Tests.EditMode.Prediction
{
    /// <summary>Client prediction and reconciliation against a fake server (spec 2.2, backlog D1).</summary>
    public class LocalPlayerPredictorTests
    {
        private static PredictionSettings Settings => PredictionTestKit.Settings();

        [Test]
        public void Walking_with_latency_never_corrects_and_ends_exactly_on_the_server_state()
        {
            var rig = new PredictionRig(Settings, delayTicks: 3);

            rig.Run(40, MoveKeys.W);
            rig.Run(40, MoveKeys.D | MoveKeys.S, facing: 20_000);
            rig.Settle();

            Assert.AreEqual(0, rig.Corrections, rig.Describe());
            Assert.AreEqual(0, rig.Predictor.PendingCount);
            Assert.AreEqual(rig.Server.State, rig.Predictor.State);
        }

        [Test]
        public void Replaying_after_a_correction_gives_what_the_server_computes()
        {
            var rig = new PredictionRig(Settings, delayTicks: 4);
            rig.Run(20, MoveKeys.W);

            // The server moves the entity on its own (knockback, teleport): whole cm, so the replay can be exact.
            rig.Server.State = new MovementState(rig.Server.State.Position + FixVec2.FromCm(60, 0), rig.Server.State.Facing);
            rig.Run(20, MoveKeys.W);
            rig.Settle();

            Assert.AreEqual(1, rig.Corrections, rig.Describe());
            Assert.AreEqual(rig.Server.State, rig.Predictor.State);
        }

        [Test]
        public void A_correction_mid_stream_shifts_the_prediction_by_the_server_error_and_keeps_pending_inputs()
        {
            var rig = new PredictionRig(Settings, delayTicks: 4);
            rig.Run(20, MoveKeys.D);
            rig.Server.State = new MovementState(rig.Server.State.Position + FixVec2.FromCm(0, -30), rig.Server.State.Facing);

            rig.Run(6, MoveKeys.D);

            var correction = rig.Results.Find(r => r.Outcome == ReconcileOutcome.Corrected);
            Assert.AreEqual(ReconcileOutcome.Corrected, correction.Outcome);
            Assert.AreEqual(FixVec2.FromCm(0, -30), correction.Shift);
            Assert.AreEqual(30.0, correction.ErrorCm, 1e-9);
            Assert.Greater(rig.Predictor.PendingCount, 0, "frames the server has not applied yet are replayed, not dropped");
        }

        [Test]
        public void Dropped_snapshots_only_delay_the_comparison()
        {
            var rig = new PredictionRig(Settings, delayTicks: 3) { DeliverSnapshot = tick => tick % 3 != 0 && tick % 7 != 0 };

            rig.Run(60, MoveKeys.W | MoveKeys.A, facing: 50_000);
            rig.Settle();

            Assert.AreEqual(0, rig.Corrections, rig.Describe());
            Assert.AreEqual(rig.Server.State, rig.Predictor.State);
        }

        [Test]
        public void Reordered_and_duplicated_snapshots_are_ignored_as_stale()
        {
            // Every fourth snapshot arrives 3 ticks late, behind newer ones.
            var rig = new PredictionRig(Settings, delayTicks: 2) { SnapshotExtraDelay = tick => tick % 4 == 0 ? 3 : 0 };

            rig.Run(60, MoveKeys.S);
            rig.Settle();

            Assert.Greater(rig.Count(ReconcileOutcome.Stale), 0);
            Assert.AreEqual(0, rig.Corrections, rig.Describe());
            Assert.AreEqual(rig.Server.State, rig.Predictor.State);
        }

        [Test]
        public void An_input_frame_the_server_never_got_is_corrected_once_and_the_prediction_converges()
        {
            var rig = new PredictionRig(Settings, delayTicks: 3);
            rig.Server.Lost.Add(10);

            rig.Run(40, MoveKeys.W);
            rig.Settle();

            Assert.AreEqual(1, rig.Corrections, rig.Describe());
            Assert.AreEqual(rig.Server.State, rig.Predictor.State);
            Assert.AreEqual(39 * PredictionTestKit.StepCm, FixedPoint.ToCmRound(rig.Server.State.Position.Y) + 50);
        }

        [Test]
        public void Diagonal_runs_stay_within_the_tolerance_of_the_server_after_a_correction_from_rounded_cm()
        {
            // Diagonal steps are fractional cm: after a rebase on a whole-cm snapshot the prediction differs from the
            // server's exact state by under 1 cm per axis, which must not trigger more corrections.
            var rig = new PredictionRig(Settings, delayTicks: 3);
            rig.Run(10, MoveKeys.W | MoveKeys.D);
            rig.Server.State = new MovementState(rig.Server.State.Position + FixVec2.FromCm(25, 25), rig.Server.State.Facing);

            rig.Run(200, MoveKeys.W | MoveKeys.D);
            rig.Settle();

            Assert.AreEqual(1, rig.Corrections, rig.Describe());
            Assert.Less(rig.ErrorCm(), 1.5);
        }

        [Test]
        public void Small_errors_within_the_tolerance_are_kept_without_a_correction()
        {
            var rig = new PredictionRig(PredictionTestKit.Settings(toleranceCm: 5), delayTicks: 2);
            rig.Run(10, MoveKeys.W);
            rig.Server.State = new MovementState(rig.Server.State.Position + FixVec2.FromCm(3, 0), rig.Server.State.Facing);

            rig.Run(10, MoveKeys.W);

            Assert.AreEqual(0, rig.Corrections, rig.Describe());
            Assert.Greater(rig.Count(ReconcileOutcome.WithinTolerance), 0);
        }

        [Test]
        public void Sequences_wrap_from_a_previous_ack_near_the_end_of_the_range()
        {
            var rig = new PredictionRig(Settings, delayTicks: 2, previousAck: 65_530);

            rig.Run(30, MoveKeys.W);
            rig.Settle();

            Assert.AreEqual(0, rig.Corrections, rig.Describe());
            Assert.AreEqual(rig.Server.State, rig.Predictor.State);
            Assert.Less(rig.Predictor.LastSequence, 100);
        }

        [Test]
        public void An_ack_for_a_frame_never_sent_is_rejected()
        {
            var predictor = new LocalPlayerPredictor(Settings, PredictionTestKit.OpenGrid());
            var start = new MovementState(FixVec2.Zero, 0);
            predictor.Reconcile(new SnapshotHeader { ServerTick = 1 }, start);
            predictor.Predict(MoveKeys.W, 0);

            var result = predictor.Reconcile(new SnapshotHeader { ServerTick = 2, HasInputAck = true, LastInputSequence = 5 }, start);

            Assert.AreEqual(ReconcileOutcome.UnknownAck, result.Outcome);
            Assert.AreEqual(1, predictor.PendingCount);
        }

        [Test]
        public void A_stalled_server_caps_the_pending_inputs()
        {
            var predictor = new LocalPlayerPredictor(PredictionTestKit.Settings(maxPending: 8), PredictionTestKit.OpenGrid());
            predictor.Reconcile(new SnapshotHeader { ServerTick = 1 }, new MovementState(FixVec2.Zero, 0));

            for (int i = 0; i < 20; i++)
                predictor.Predict(MoveKeys.W, 0);

            Assert.AreEqual(8, predictor.PendingCount);
            Assert.AreEqual(12, predictor.DroppedPending);
            Assert.AreEqual(FixVec2.FromCm(0, 20 * PredictionTestKit.StepCm), predictor.State.Position);
        }

        [Test]
        public void Predicted_facing_is_the_value_the_server_decodes()
        {
            var predictor = new LocalPlayerPredictor(Settings, PredictionTestKit.OpenGrid());
            predictor.Reconcile(new SnapshotHeader { ServerTick = 1 }, new MovementState(FixVec2.Zero, 0));

            var frame = predictor.Predict(MoveKeys.None, 12_345);

            Assert.AreEqual(Quantize.RoundTripAngle(12_345, InputPacket.FacingBits), predictor.State.Facing);
            Assert.AreEqual(predictor.State.Facing, frame.Facing);
        }
    }
}
