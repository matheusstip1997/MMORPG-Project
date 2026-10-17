using Game.Gameplay.Entities;
using Game.Tests.EditMode.Prediction;
using NUnit.Framework;

namespace Game.Tests.EditMode.Entities
{
    public class SnapshotBufferTests
    {
        private static EntitySample Sample(uint tick, int x, ushort facing = 0) =>
            new EntitySample { Tick = tick, XCm = x, YCm = 0, ZCm = 2 * x, Facing = facing };

        private static EntityPose Pose(SnapshotBuffer buffer, double tick)
        {
            Assert.IsTrue(buffer.TrySample(tick, out var pose));
            return pose;
        }

        [Test]
        public void Blends_between_the_two_samples_around_the_render_time()
        {
            var buffer = new SnapshotBuffer(8);
            buffer.Add(Sample(10, 0));
            buffer.Add(Sample(11, 100));

            var pose = Pose(buffer, 10.25);

            Assert.AreEqual(25f, pose.XCm, 1e-3f);
            Assert.AreEqual(50f, pose.ZCm, 1e-3f);
        }

        [Test]
        public void Blends_across_a_gap_left_by_dropped_snapshots()
        {
            var buffer = new SnapshotBuffer(8);
            buffer.Add(Sample(10, 0));
            buffer.Add(Sample(14, 400));

            Assert.AreEqual(150f, Pose(buffer, 11.5).XCm, 1e-3f);
        }

        [Test]
        public void Holds_the_newest_sample_past_it_and_the_oldest_before_it()
        {
            var buffer = new SnapshotBuffer(8);
            buffer.Add(Sample(10, 0));
            buffer.Add(Sample(11, 100));

            Assert.AreEqual(100f, Pose(buffer, 15).XCm, 1e-3f, "no extrapolation");
            Assert.AreEqual(0f, Pose(buffer, 5).XCm, 1e-3f);
        }

        [Test]
        public void Reordered_samples_are_inserted_in_tick_order_and_duplicates_ignored()
        {
            var buffer = new SnapshotBuffer(8);
            buffer.Add(Sample(10, 0));
            buffer.Add(Sample(12, 200));
            Assert.IsTrue(buffer.Add(Sample(11, 50)));
            Assert.IsFalse(buffer.Add(Sample(11, 999)));

            Assert.AreEqual(3, buffer.Count);
            Assert.AreEqual(50f, Pose(buffer, 11).XCm, 1e-3f);
            Assert.AreEqual(125f, Pose(buffer, 11.5).XCm, 1e-3f);
        }

        [Test]
        public void Facing_turns_the_short_way_across_zero()
        {
            var buffer = new SnapshotBuffer(8);
            buffer.Add(Sample(10, 0, facing: 65_000));
            buffer.Add(Sample(11, 0, facing: 500));

            float facing = Pose(buffer, 10.5).Facing;

            Assert.AreEqual(65_000f + 518f, facing, 1e-2f); // halfway along the 1036-unit short arc
        }

        [Test]
        public void A_full_buffer_drops_its_oldest_sample_and_prune_keeps_one_before_the_render_time()
        {
            var buffer = new SnapshotBuffer(3);
            for (uint t = 1; t <= 5; t++)
                buffer.Add(Sample(t, (int)t));
            Assert.AreEqual(3, buffer.Count);

            buffer.Prune(4.5);

            Assert.AreEqual(2, buffer.Count);
            Assert.AreEqual(4.5f, Pose(buffer, 4.5).XCm, 1e-3f);
        }
    }

    public class InterpolationClockTests
    {
        [Test]
        public void Render_time_runs_the_interpolation_delay_behind_the_newest_snapshot()
        {
            var clock = new InterpolationClock(PredictionTestKit.Settings(delayMs: 100));

            clock.OnSnapshot(serverTick: 100, localNowMs: 0);

            Assert.AreEqual(98.0, clock.RenderTick, 1e-9); // 100 ms at 20 Hz is 2 ticks
        }

        [Test]
        public void Steady_snapshots_keep_the_render_time_on_target()
        {
            var clock = new InterpolationClock(PredictionTestKit.Settings(delayMs: 100));
            double now = 0;
            clock.OnSnapshot(100, now);
            for (uint tick = 101; tick <= 140; tick++)
            {
                for (int frame = 0; frame < 3; frame++)
                {
                    now += 50.0 / 3;
                    clock.Advance(now, 50.0 / 3);
                }
                clock.OnSnapshot(tick, now);
            }

            Assert.AreEqual(138.0, clock.RenderTick, 0.05);
            Assert.AreEqual(0, clock.Snaps);
        }

        [Test]
        public void A_small_offset_drifts_back_and_a_large_one_snaps()
        {
            var clock = new InterpolationClock(PredictionTestKit.Settings(delayMs: 100, catchUpMs: 500));
            clock.OnSnapshot(100, 0);

            // Snapshots suddenly 30 ms early: the target moves forward by 30 ms, the clock follows gradually.
            clock.OnSnapshot(101, 20);
            clock.Advance(20, 20);
            Assert.Less(clock.RenderTick, 99.6 - 0.01);
            Assert.Greater(clock.RenderTick, 98.4);
            Assert.AreEqual(0, clock.Snaps);

            // The server jumped far ahead (or the client stalled): snap.
            clock.OnSnapshot(200, 40);
            clock.Advance(40, 20);
            Assert.AreEqual(198.0, clock.RenderTick, 1e-9);
            Assert.AreEqual(1, clock.Snaps);
        }

        [Test]
        public void Older_snapshots_do_not_move_the_target()
        {
            var clock = new InterpolationClock(PredictionTestKit.Settings(delayMs: 100));
            clock.OnSnapshot(100, 0);
            clock.OnSnapshot(90, 10);
            clock.Advance(10, 10);

            Assert.AreEqual(98.2, clock.RenderTick, 1e-6);
        }
    }
}
