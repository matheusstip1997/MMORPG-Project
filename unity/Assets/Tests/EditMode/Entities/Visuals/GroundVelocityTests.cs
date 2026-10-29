#nullable enable
using Game.Gameplay.Entities.Visuals;
using NUnit.Framework;

namespace Game.Tests.EditMode.Entities.Visuals
{
    /// <summary>
    /// The directional blend's two parameters (R-2.2-01: WASD moves, the mouse faces): a known ground velocity and facing
    /// give the velocity along the facing and to its right. Ground axes x east, z north; the yaw is clockwise from north.
    /// </summary>
    public class GroundVelocityTests
    {
        private const float FrameS = 1f / 60f;
        private const float SmoothingS = 0.1f;
        private const float MaxSpeed = 3000f;
        private const float Speed = 400f;
        private const float Tolerance = 0.5f;

        /// <summary>Moves at (vx, vz) cm/s with the facing at <paramref name="yawDeg"/> for long enough to settle.</summary>
        private static GroundVelocity Settle(float vx, float vz, float yawDeg, GroundVelocity velocity = default)
        {
            for (int i = 0; i < 120; i++)
                velocity.Measure(vx * FrameS, vz * FrameS, yawDeg, FrameS, SmoothingS, MaxSpeed);
            return velocity;
        }

        [TestCase(0f, 0f, Speed, Speed, 0f)] // facing north, moving north: forward
        [TestCase(0f, 0f, -Speed, -Speed, 0f)] // facing north, moving south: backpedal
        [TestCase(0f, Speed, 0f, 0f, Speed)] // facing north, moving east: strafe right
        [TestCase(0f, -Speed, 0f, 0f, -Speed)] // facing north, moving west: strafe left
        [TestCase(90f, 0f, Speed, 0f, -Speed)] // facing east, moving north: to its left
        [TestCase(90f, Speed, 0f, Speed, 0f)] // facing east, moving east: forward
        [TestCase(180f, 0f, Speed, -Speed, 0f)] // facing south, moving north: backpedal
        [TestCase(270f, 0f, Speed, 0f, Speed)] // facing west, moving north: to its right
        public void A_velocity_and_a_facing_give_the_two_parameters(float yawDeg, float vx, float vz, float forward, float right)
        {
            var velocity = Settle(vx, vz, yawDeg);

            Assert.AreEqual(forward, velocity.ForwardCmPerSec, Tolerance);
            Assert.AreEqual(right, velocity.RightCmPerSec, Tolerance);
            Assert.AreEqual(Speed, velocity.SpeedCmPerSec, Tolerance);
        }

        [Test]
        public void A_diagonal_off_the_facing_splits_between_the_two_parameters()
        {
            // Facing north-east, moving north: 45 degrees to the facing's left.
            var velocity = Settle(0f, Speed, 45f);
            float half = Speed * (float)System.Math.Sqrt(0.5);

            Assert.AreEqual(half, velocity.ForwardCmPerSec, Tolerance);
            Assert.AreEqual(-half, velocity.RightCmPerSec, Tolerance);
        }

        [Test]
        public void A_snap_is_not_movement()
        {
            var velocity = Settle(0f, Speed, 0f);

            // A correction of 2 m in one frame.
            velocity.Measure(200f, 0f, 0f, FrameS, SmoothingS, MaxSpeed);

            Assert.AreEqual(Speed, velocity.ForwardCmPerSec, Tolerance);
            Assert.AreEqual(0f, velocity.RightCmPerSec, Tolerance);
        }

        [Test]
        public void A_turn_of_the_facing_blends_instead_of_popping()
        {
            // Walking north facing north, then the mouse swings the facing east in one frame: the movement is now to the
            // facing's left, reached over the smoothing time and not at once.
            var velocity = Settle(0f, Speed, 0f);
            velocity.Measure(0f, Speed * FrameS, 90f, FrameS, SmoothingS, MaxSpeed);

            Assert.Greater(velocity.ForwardCmPerSec, Speed * 0.5f);
            Assert.Less(velocity.RightCmPerSec, 0f);

            velocity = Settle(0f, Speed, 90f, velocity);
            Assert.AreEqual(0f, velocity.ForwardCmPerSec, Tolerance);
            Assert.AreEqual(-Speed, velocity.RightCmPerSec, Tolerance);
        }

        [Test]
        public void No_time_measures_nothing()
        {
            var velocity = Settle(0f, Speed, 0f);

            velocity.Measure(100f, 100f, 0f, 0f, SmoothingS, MaxSpeed);

            Assert.AreEqual(Speed, velocity.ForwardCmPerSec, Tolerance);
        }
    }
}
