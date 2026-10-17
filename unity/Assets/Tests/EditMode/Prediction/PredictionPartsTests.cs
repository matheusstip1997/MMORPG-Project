using Game.Gameplay.Prediction;
using NUnit.Framework;
using Shared.Protocol;
using Shared.Simulation;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace Game.Tests.EditMode.Prediction
{
    public class InputPacketBuilderTests
    {
        private static InputFrame Frame(ushort sequence, uint tick) =>
            new InputFrame { Sequence = sequence, ClientTick = tick, Keys = MoveKeys.W };

        private static InputFrame[] Decode(byte[] buffer, int size)
        {
            var frames = new InputFrame[InputPacket.MaxFrames];
            Assert.IsTrue(InputPacket.TryRead(new System.ReadOnlySpan<byte>(buffer, 0, size), frames, out int count));
            return frames[..count];
        }

        [Test]
        public void Packets_carry_the_newest_frame_and_the_configured_number_before_it()
        {
            var builder = new InputPacketBuilder(redundancy: 3);
            var buffer = new byte[builder.MaxPacketSize];

            builder.Write(Frame(1, 1), buffer);
            builder.Write(Frame(2, 2), buffer);
            int size = builder.Write(Frame(3, 3), buffer);
            var three = Decode(buffer, size);
            size = builder.Write(Frame(4, 4), buffer);
            var four = Decode(buffer, size);

            CollectionAssert.AreEqual(new ushort[] { 3, 2, 1 }, System.Array.ConvertAll(three, f => f.Sequence));
            CollectionAssert.AreEqual(new ushort[] { 4, 3, 2 }, System.Array.ConvertAll(four, f => f.Sequence));
        }

        [Test]
        public void A_frame_that_does_not_follow_on_starts_a_new_window()
        {
            var builder = new InputPacketBuilder(redundancy: 3);
            var buffer = new byte[builder.MaxPacketSize];
            builder.Write(Frame(1, 1), buffer);
            builder.Write(Frame(2, 2), buffer);

            int size = builder.Write(Frame(9, 9), buffer);

            Assert.AreEqual(1, Decode(buffer, size).Length);
        }
    }

    public class TickClockTests
    {
        [Test]
        public void Runs_one_tick_per_50_ms_and_reports_the_fraction_into_the_next()
        {
            var clock = new TickClock(PredictionTestKit.Settings());

            Assert.AreEqual(0, clock.Advance(30));
            Assert.AreEqual(1, clock.Advance(30));
            Assert.AreEqual(0.2f, clock.Alpha, 1e-4f);
            Assert.AreEqual(2, clock.Advance(100));
        }

        [Test]
        public void A_hitch_runs_at_most_MaxTicksPerFrame_and_drops_the_rest()
        {
            var clock = new TickClock(PredictionTestKit.Settings(maxTicksPerFrame: 4));

            Assert.AreEqual(4, clock.Advance(1_000));
            Assert.AreEqual(800, clock.DroppedMs, 1e-6);
            Assert.AreEqual(0, clock.Advance(0));
        }
    }

    public class LocalPlayerMotionTests
    {
        [Test]
        public void Between_ticks_the_position_is_blended()
        {
            var motion = new LocalPlayerMotion(PredictionTestKit.Settings());
            motion.Reset(FixVec2.FromCm(0, 0));
            motion.OnTick(FixVec2.FromCm(0, 20));

            motion.GetVisualCm(0.25f, out float x, out float y);

            Assert.AreEqual(0f, x, 1e-4f);
            Assert.AreEqual(5f, y, 1e-4f);
        }

        [Test]
        public void A_small_correction_does_not_jump_and_then_fades()
        {
            var motion = new LocalPlayerMotion(PredictionTestKit.Settings(smoothingMs: 100));
            motion.Reset(FixVec2.FromCm(0, 100));
            motion.GetVisualCm(1f, out _, out float before);

            motion.OnCorrected(FixVec2.FromCm(0, -40));
            motion.GetVisualCm(1f, out _, out float right);
            motion.Update(100f);
            motion.GetVisualCm(1f, out _, out float later);

            Assert.AreEqual(before, right, 1e-4f, "no visual jump at the correction");
            Assert.AreEqual(60f + 40f * (float)System.Math.Exp(-1), later, 1e-3f);
        }

        [Test]
        public void A_correction_above_the_snap_distance_snaps()
        {
            var motion = new LocalPlayerMotion(PredictionTestKit.Settings(snapCm: 300));
            motion.Reset(FixVec2.FromCm(0, 0));

            motion.OnCorrected(FixVec2.FromCm(400, 0));
            motion.GetVisualCm(1f, out float x, out _);

            Assert.AreEqual(400f, x, 1e-4f);
        }

        [Test]
        public void Zero_smoothing_snaps_every_correction()
        {
            var motion = new LocalPlayerMotion(PredictionTestKit.Settings(smoothingMs: 0));
            motion.Reset(FixVec2.FromCm(0, 0));

            motion.OnCorrected(FixVec2.FromCm(10, 0));
            motion.GetVisualCm(1f, out float x, out _);

            Assert.AreEqual(10f, x, 1e-4f);
        }
    }
}
