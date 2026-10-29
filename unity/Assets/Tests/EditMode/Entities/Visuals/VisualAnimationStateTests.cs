#nullable enable
using Game.Gameplay.Entities.Visuals;
using NUnit.Framework;

namespace Game.Tests.EditMode.Entities.Visuals
{
    /// <summary>Action layer against locomotion: actions silence the walk blend, reactions cut actions (R-3.3-07).</summary>
    public class VisualAnimationStateTests
    {
        private const int Attack = 0;
        private const int Flinch = 1;
        private const int Death = 2;
        private const float AttackLengthS = 2f;
        private const float FlinchLengthS = 1f;
        private const float CrossFadeS = 0.1f;
        private const float HoldMs = 250f;
        private const float Tolerance = 1e-3f;

        private static VisualAnimationState NewState() =>
            new VisualAnimationState(LocomotionProfile.ForwardOnly(2f, 1f, 0.8f, 150f, 450f), CrossFadeS, HoldMs);

        private static void Run(VisualAnimationState state, float seconds, float speed = 0f)
        {
            const float step = 0.005f;
            int steps = (int)System.Math.Round(seconds / step);
            for (int i = 0; i < steps; i++)
                state.Advance(step, speed, 0f);
        }

        [Test]
        public void Locomotion_drives_the_pose_when_nothing_plays()
        {
            var state = NewState();

            Run(state, 0.5f, speed: 400f);

            Assert.AreEqual(1f, state.LocomotionWeight, Tolerance);
            Assert.IsFalse(state.IsActionPlaying);
            Assert.Greater(state.Locomotion.Run, 0.5f);
        }

        [Test]
        public void During_an_action_locomotion_is_silenced_even_while_the_entity_moves()
        {
            var state = NewState();
            Run(state, 0.2f, speed: 400f);

            // A dash: the entity moves at speed while the action plays; the walk cycle must not fight it.
            state.PlayPhase(Attack, AttackLengthS, 0f, 0.35f, 300, 0f, loop: false, last: false);
            Run(state, 0.2f, speed: 900f);

            Assert.IsTrue(state.IsActionPlaying);
            Assert.AreEqual(0f, state.LocomotionWeight, Tolerance);
            Assert.AreEqual(Attack, state.ActionClip);
        }

        [Test]
        public void A_phase_plays_its_segment_scaled_to_the_phase_duration()
        {
            var state = NewState();

            state.PlayPhase(Attack, AttackLengthS, 0f, 0.35f, 300, 0f, loop: false, last: false);
            Run(state, 0.15f);

            // Half the 300 ms phase: half of the 0.7 s startup segment.
            Assert.AreEqual(0.35f, state.ActionTimeS, Tolerance);
        }

        [Test]
        public void A_late_join_starts_mid_phase()
        {
            var state = NewState();

            state.PlayPhase(Attack, AttackLengthS, 0f, 0.35f, 300, elapsedMs: 150f, loop: false, last: false);

            Assert.AreEqual(0.35f, state.ActionTimeS, Tolerance);
        }

        [Test]
        public void After_recovery_the_view_fades_back_to_locomotion()
        {
            var state = NewState();
            state.PlayPhase(Attack, AttackLengthS, 0.55f, 1f, 400, 0f, loop: false, last: true);

            Run(state, 0.39f);
            Assert.IsTrue(state.IsActionPlaying);

            Run(state, 0.02f + CrossFadeS);
            Assert.IsFalse(state.IsActionPlaying);
            Assert.AreEqual(1f, state.LocomotionWeight, Tolerance);
        }

        [Test]
        public void A_startup_holds_its_last_frame_waiting_for_the_next_phase_then_gives_up()
        {
            var state = NewState();
            state.PlayPhase(Attack, AttackLengthS, 0f, 0.35f, 300, 0f, loop: false, last: false);

            Run(state, 0.3f + HoldMs / 1000f - 0.05f);
            Assert.IsTrue(state.IsActionPlaying);
            Assert.AreEqual(0.7f, state.ActionTimeS, Tolerance);

            Run(state, 0.1f);
            Assert.IsFalse(state.IsActionPlaying);
        }

        [Test]
        public void A_flinch_cuts_the_playing_action_and_is_scaled_to_the_flinch_duration()
        {
            var state = NewState();
            state.PlayPhase(Attack, AttackLengthS, 0f, 0.35f, 300, 0f, loop: false, last: false);
            Run(state, 0.2f);

            state.PlayReaction(Flinch, FlinchLengthS, 250, 0f, hold: false);

            Assert.AreEqual(Flinch, state.ActionClip);
            Assert.AreEqual(Attack, state.PreviousClip, "the attack is frozen and cross-faded out");
            Assert.AreEqual(0f, state.ActionBlend, Tolerance);

            Run(state, 0.125f);
            Assert.AreEqual(0.5f, state.ActionTimeS, Tolerance, "half the 250 ms flinch is half the clip");
            Assert.AreEqual(-1, state.PreviousClip, "the cross-fade is over");

            Run(state, 0.13f + CrossFadeS);
            Assert.IsFalse(state.IsActionPlaying);
            Assert.AreEqual(1f, state.LocomotionWeight, Tolerance);
        }

        [Test]
        public void The_next_phase_of_the_same_clip_continues_without_a_cross_fade()
        {
            var state = NewState();
            state.PlayPhase(Attack, AttackLengthS, 0f, 0.35f, 300, 0f, loop: false, last: false);
            Run(state, 0.3f);

            state.PlayPhase(Attack, AttackLengthS, 0.35f, 0.55f, 100, 0f, loop: false, last: false);

            Assert.AreEqual(-1, state.PreviousClip);
            Assert.AreEqual(1f, state.ActionBlend, Tolerance);
            Assert.AreEqual(0.7f, state.ActionTimeS, Tolerance);
        }

        [Test]
        public void Death_holds_its_last_frame()
        {
            var state = NewState();

            state.PlayReaction(Death, 1.5f, 1500, 0f, hold: true);
            Run(state, 5f);

            Assert.IsTrue(state.IsActionPlaying);
            Assert.AreEqual(1.5f, state.ActionTimeS, Tolerance);
            Assert.AreEqual(0f, state.LocomotionWeight, Tolerance);
        }

        /// <summary>A body like the placeholder humanoids: the backward loops reuse the forward ones in reverse, legs turn 90.</summary>
        private static VisualAnimationState NewDirectionalState()
        {
            var slots = new LocomotionSlot[LocomotionProfile.Slots];
            for (int d = 0; d < LocomotionProfile.Directions; d++)
            {
                bool backward = d == (int)LocomotionDirection.Backward;
                slots[LocomotionProfile.SlotIndex(true, d)] = new LocomotionSlot(1f, backward, 150f);
                slots[LocomotionProfile.SlotIndex(false, d)] = new LocomotionSlot(0.8f, backward, 450f);
            }
            return new VisualAnimationState(new LocomotionProfile(2f, slots, 90f, 540f, 15f), CrossFadeS, HoldMs);
        }

        private static void Move(VisualAnimationState state, float seconds, float forward, float right)
        {
            const float step = 0.005f;
            int steps = (int)System.Math.Round(seconds / step);
            for (int i = 0; i < steps; i++)
                state.Advance(step, forward, right);
        }

        private static readonly int RunForward = LocomotionProfile.SlotIndex(false, (int)LocomotionDirection.Forward);
        private static readonly int RunBackward = LocomotionProfile.SlotIndex(false, (int)LocomotionDirection.Backward);

        [Test]
        public void A_backpedal_plays_the_backward_slot_in_reverse_with_the_legs_on_the_facing()
        {
            var state = NewDirectionalState();

            Move(state, 0.5f, -400f, 0f);

            Assert.AreEqual(-400f, state.ForwardCmPerSec, Tolerance);
            Assert.AreEqual(1f, state.Directions.Backward, Tolerance);
            Assert.AreEqual(0f, state.LegTurnDeg, Tolerance);
            Assert.IsTrue(state.LegsBackward);
            Assert.Greater(state.SlotWeight(RunBackward), 0.5f);
            Assert.AreEqual(0f, state.SlotWeight(RunForward), Tolerance);
            // The reversed slot runs the shared gait cycle backward.
            float before = state.SlotTimeS(RunBackward);
            Assert.AreEqual(1f, state.SlotTimeS(RunForward) / 0.8f + before / 0.8f, Tolerance);
            Move(state, 0.01f, -400f, 0f);
            float back = before - state.SlotTimeS(RunBackward);
            if (back < 0f)
                back += 0.8f; // wrapped around the loop
            Assert.That(back, Is.InRange(0.001f, 0.1f));
        }

        [Test]
        public void A_strafe_turns_the_legs_and_walks_them_forward()
        {
            var state = NewDirectionalState();

            Move(state, 0.5f, 0f, 400f);

            Assert.AreEqual(400f, state.RightCmPerSec, Tolerance);
            Assert.AreEqual(90f, state.LegTurnDeg, Tolerance);
            Assert.IsFalse(state.LegsBackward);
            Assert.AreEqual(1f, state.Directions.Forward, Tolerance);
        }

        [Test]
        public void Standing_still_the_legs_do_not_turn()
        {
            var state = NewDirectionalState();
            Move(state, 0.5f, 0f, 400f);

            Move(state, 0.5f, 0f, 0f);

            Assert.AreEqual(0f, state.LegTurnDeg, Tolerance);
            Assert.AreEqual(1f, state.Locomotion.Idle, Tolerance);
        }

        [Test]
        public void After_a_backward_dodge_locomotion_resumes_along_the_new_movement()
        {
            // R-2.2-02 and the dodge dash (PR #56): the facing is locked during the action, the dash goes backward with no
            // key held. The dodge plays its own clip with no leg turn; afterwards a strafe shows the turned legs walking
            // forward, not the backward half the dash left behind.
            var state = NewDirectionalState();
            Move(state, 0.3f, 0f, 400f);

            state.PlayPhase(Attack, AttackLengthS, 0f, 1f, 300, 0f, loop: false, last: true);
            Move(state, 0.2f, -1300f, 0f);
            Assert.IsTrue(state.IsActionPlaying);
            Assert.AreEqual(0f, state.LocomotionWeight, Tolerance);
            Assert.AreEqual(0f, state.LegTurnDeg, Tolerance, "the dodge clip plays untouched");
            Assert.AreEqual(1f, state.Directions.Backward, Tolerance, "the direction keeps being tracked");

            Move(state, 0.1f, 0f, 400f); // the dash ends at 300 ms, the strafe goes on
            Move(state, CrossFadeS + 0.05f, 0f, 400f);

            Assert.IsFalse(state.IsActionPlaying);
            Assert.AreEqual(1f, state.LocomotionWeight, Tolerance);
            Assert.IsFalse(state.LegsBackward);
            Assert.AreEqual(90f, state.LegTurnDeg, Tolerance);
            Assert.AreEqual(1f, state.Directions.Forward, Tolerance);
        }

        [Test]
        public void A_forward_only_body_never_turns_its_legs()
        {
            var state = NewState();

            Run(state, 0.5f, speed: 0f);
            state.Advance(0.5f, 0f, 400f);

            Assert.AreEqual(0f, state.LegTurnDeg, Tolerance);
            Assert.AreEqual(1f, state.Directions.Right, Tolerance);
            // Every direction repeats the forward loops.
            Assert.Greater(state.SlotWeight(LocomotionProfile.SlotIndex(false, (int)LocomotionDirection.Right)), 0.5f);
        }

        [Test]
        public void Stop_goes_back_to_locomotion()
        {
            var state = NewState();
            state.PlayPhase(Attack, AttackLengthS, 0.35f, 0.55f, 2000, 0f, loop: true, last: false);
            Run(state, 0.5f);

            state.Stop();
            Run(state, CrossFadeS + 0.01f);

            Assert.IsFalse(state.IsActionPlaying);
            Assert.AreEqual(1f, state.LocomotionWeight, Tolerance);
        }
    }
}
