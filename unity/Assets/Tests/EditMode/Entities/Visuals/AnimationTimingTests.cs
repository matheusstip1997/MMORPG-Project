#nullable enable
using Game.Gameplay.Entities.Visuals;
using NUnit.Framework;

namespace Game.Tests.EditMode.Entities.Visuals
{
    /// <summary>Locomotion blend and clip time-scaling to ActionDef phase durations (visuals follow the timings).</summary>
    public class AnimationTimingTests
    {
        private const float Walk = 150f;
        private const float Run = 450f;
        private const float Tolerance = 1e-4f;

        [Test]
        public void Standing_still_is_idle()
        {
            var w = LocomotionBlend.Evaluate(0f, Walk, Run);

            Assert.AreEqual(1f, w.Idle, Tolerance);
            Assert.AreEqual(0f, w.Walk + w.Run, Tolerance);
        }

        [Test]
        public void Below_walk_speed_walk_fades_in_over_idle_at_its_own_rate()
        {
            var w = LocomotionBlend.Evaluate(75f, Walk, Run);

            Assert.AreEqual(0.5f, w.Idle, Tolerance);
            Assert.AreEqual(0.5f, w.Walk, Tolerance);
            Assert.AreEqual(1f, w.GaitRate, Tolerance);
        }

        [Test]
        public void Between_walk_and_run_they_cross_fade_at_the_rate_that_keeps_the_feet_planted()
        {
            var w = LocomotionBlend.Evaluate(300f, Walk, Run);

            Assert.AreEqual(0.5f, w.Walk, Tolerance);
            Assert.AreEqual(0.5f, w.Run, Tolerance);
            Assert.AreEqual(0f, w.Idle, Tolerance);
            Assert.AreEqual(1f, w.GaitRate, Tolerance); // 300 cm/s is exactly the blended reference speed
        }

        [Test]
        public void Above_run_speed_run_plays_faster_up_to_the_cap()
        {
            Assert.AreEqual(600f / Run, LocomotionBlend.Evaluate(600f, Walk, Run).GaitRate, Tolerance);
            Assert.AreEqual(1f, LocomotionBlend.Evaluate(600f, Walk, Run).Run, Tolerance);
            Assert.AreEqual(LocomotionBlend.MaxGaitRate, LocomotionBlend.Evaluate(5000f, Walk, Run).GaitRate, Tolerance);
        }

        [Test]
        public void Without_a_run_clip_the_walk_clip_is_sped_up()
        {
            var w = LocomotionBlend.Evaluate(300f, Walk, Walk);

            Assert.AreEqual(1f, w.Walk, Tolerance);
            Assert.AreEqual(0f, w.Run, Tolerance);
            Assert.AreEqual(2f, w.GaitRate, Tolerance);
        }

        [Test]
        public void Speed_smoothing_is_frame_rate_independent()
        {
            float oneStep = LocomotionBlend.Smooth(0f, 400f, 0.1f, 0.1f);
            float twoSteps = LocomotionBlend.Smooth(LocomotionBlend.Smooth(0f, 400f, 0.05f, 0.1f), 400f, 0.05f, 0.1f);

            Assert.AreEqual(oneStep, twoSteps, 0.01f);
        }

        [TestCase(ActionPresentationPhase.Startup, 0f, 0.3f)]
        [TestCase(ActionPresentationPhase.Active, 0.3f, 0.6f)]
        [TestCase(ActionPresentationPhase.Channel, 0.3f, 0.6f)]
        [TestCase(ActionPresentationPhase.Recovery, 0.6f, 1f)]
        public void Phases_map_to_clip_segments(ActionPresentationPhase phase, float from, float to)
        {
            PhaseClipTiming.Segment(phase, 0.3f, 0.6f, out float from01, out float to01);

            Assert.AreEqual(from, from01, Tolerance);
            Assert.AreEqual(to, to01, Tolerance);
        }

        [Test]
        public void A_segment_is_time_scaled_to_fill_its_phase()
        {
            // A 2 s clip, startup segment [0, 0.3] = 0.6 s of clip, phase 300 ms: plays at 2x.
            Assert.AreEqual(2f, PhaseClipTiming.PlaybackRate(2f, 0f, 0.3f, 300), Tolerance);
            Assert.AreEqual(0.3f, PhaseClipTiming.ClipTime(2f, 0f, 0.3f, 300, 150f, loop: false), Tolerance);
            Assert.AreEqual(0.6f, PhaseClipTiming.ClipTime(2f, 0f, 0.3f, 300, 300f, loop: false), Tolerance);
        }

        [Test]
        public void A_longer_phase_slows_the_same_segment_down()
        {
            Assert.AreEqual(0.5f, PhaseClipTiming.PlaybackRate(2f, 0f, 0.3f, 1200), Tolerance);
            Assert.AreEqual(0.15f, PhaseClipTiming.ClipTime(2f, 0f, 0.3f, 1200, 300f, loop: false), Tolerance);
        }

        [Test]
        public void A_finished_segment_stays_on_its_last_frame()
        {
            Assert.AreEqual(2f, PhaseClipTiming.ClipTime(2f, 0.6f, 1f, 400, 10_000f, loop: false), Tolerance);
        }

        [Test]
        public void A_channel_loops_its_segment_at_the_clips_own_speed()
        {
            // Segment [0.3, 0.6] of a 2 s clip is 0.6 s long; 0.75 s into the channel wraps to 0.15 s into it.
            Assert.AreEqual(0.6f + 0.15f, PhaseClipTiming.ClipTime(2f, 0.3f, 0.6f, 5000, 750f, loop: true), Tolerance);
        }

        [Test]
        public void A_zero_length_phase_shows_the_segment_end()
        {
            Assert.AreEqual(1.2f, PhaseClipTiming.ClipTime(2f, 0.3f, 0.6f, 0, 0f, loop: false), Tolerance);
            Assert.AreEqual(0f, PhaseClipTiming.PlaybackRate(2f, 0.3f, 0.6f, 0), Tolerance);
        }
    }
}
