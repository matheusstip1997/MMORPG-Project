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

        private static VisualAnimationState NewState() => new VisualAnimationState(2f, 1f, 0.8f, CrossFadeS, HoldMs);

        private static void Run(VisualAnimationState state, float seconds, float speed = 0f)
        {
            const float step = 0.005f;
            int steps = (int)System.Math.Round(seconds / step);
            for (int i = 0; i < steps; i++)
                state.Advance(step, speed, 150f, 450f);
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
