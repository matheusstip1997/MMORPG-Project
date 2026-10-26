#nullable enable
using Game.Gameplay.Entities.Visuals;
using NUnit.Framework;

namespace Game.Tests.EditMode.Entities.Visuals
{
    /// <summary>
    /// The leg turn of bodies without side loops: the hips turn toward the movement and the walk plays along the legs,
    /// forward or backward; hysteresis at the facing's sides, a rate limit, nothing standing still or during an action.
    /// </summary>
    public class LegTurnTests
    {
        private const float MaxDeg = 90f;
        private const float RateDegPerSec = 540f;
        private const float HysteresisDeg = 15f;
        private const float Speed = 400f;
        private const float Tolerance = 1e-3f;

        private static (float Forward, float Right) At(float degrees) =>
            (Speed * (float)System.Math.Cos(degrees * System.Math.PI / 180.0), Speed * (float)System.Math.Sin(degrees * System.Math.PI / 180.0));

        private static LegTurn Settle(float degrees, LegTurn turn = default)
        {
            var (forward, right) = At(degrees);
            for (int i = 0; i < 60; i++)
                turn.Advance(forward, right, 1f / 60f, MaxDeg, RateDegPerSec, HysteresisDeg);
            return turn;
        }

        [Test]
        public void A_strafe_turns_the_legs_fully_and_they_walk_forward_along_the_movement()
        {
            var turn = Settle(90f);

            Assert.AreEqual(90f, turn.TurnDeg, Tolerance);
            Assert.IsFalse(turn.Backward);
            LegTurn.Residual(0f, Speed, turn.TurnDeg, out float forward, out float right);
            Assert.AreEqual(Speed, forward, 0.01f);
            Assert.AreEqual(0f, right, 0.01f);
        }

        [Test]
        public void A_backpedal_keeps_the_legs_on_the_facing_and_walks_them_backward()
        {
            var turn = Settle(180f);

            Assert.AreEqual(0f, turn.TurnDeg, Tolerance);
            Assert.IsTrue(turn.Backward);
        }

        [Test]
        public void Backward_diagonals_turn_the_legs_away_from_the_movement()
        {
            // Back and to the left (225 degrees): the legs turn 45 degrees right and walk backward along the movement.
            var turn = Settle(225f);

            Assert.IsTrue(turn.Backward);
            Assert.AreEqual(45f, turn.TurnDeg, Tolerance);
            var (forward, right) = At(225f);
            LegTurn.Residual(forward, right, turn.TurnDeg, out float legsForward, out float legsRight);
            Assert.AreEqual(-Speed, legsForward, 0.01f);
            Assert.AreEqual(0f, legsRight, 0.01f);
        }

        [Test]
        public void Near_the_side_the_legs_keep_their_half_until_the_hysteresis_is_crossed()
        {
            var turn = Settle(80f);
            Assert.IsFalse(turn.Backward);

            turn = Settle(100f, turn); // 10 degrees past the side: still walking forward
            Assert.IsFalse(turn.Backward);
            Assert.AreEqual(90f, turn.TurnDeg, Tolerance, "clamped to the maximum");

            turn = Settle(110f, turn); // 20 degrees past: walking backward now
            Assert.IsTrue(turn.Backward);
            Assert.AreEqual(-70f, turn.TurnDeg, Tolerance);

            turn = Settle(80f, turn); // back within 15 degrees of the side: still backward
            Assert.IsTrue(turn.Backward);

            turn = Settle(70f, turn);
            Assert.IsFalse(turn.Backward);
        }

        [Test]
        public void A_snap_takes_the_nearer_half_at_once()
        {
            var turn = Settle(180f); // a backward dash
            Assert.IsTrue(turn.Backward);

            var (forward, right) = At(90f);
            turn.Snap(forward, right, MaxDeg);
            Assert.IsFalse(turn.Backward, "a pure strafe walks forward");
            Assert.AreEqual(90f, turn.TurnDeg, Tolerance);

            (forward, right) = At(-120f);
            turn.Snap(forward, right, MaxDeg);
            Assert.IsTrue(turn.Backward);
            Assert.AreEqual(60f, turn.TurnDeg, Tolerance);
        }

        [Test]
        public void The_turn_moves_at_its_rate()
        {
            var turn = new LegTurn();
            var (forward, right) = At(90f);

            turn.Advance(forward, right, 0.1f, MaxDeg, RateDegPerSec, HysteresisDeg);

            Assert.AreEqual(54f, turn.TurnDeg, Tolerance);
        }

        [Test]
        public void Without_a_maximum_the_legs_never_turn()
        {
            var turn = new LegTurn { TurnDeg = 30f, Backward = true };
            var (forward, right) = At(90f);

            turn.Advance(forward, right, 0.1f, 0f, RateDegPerSec, HysteresisDeg);

            Assert.AreEqual(0f, turn.TurnDeg, Tolerance);
            Assert.IsFalse(turn.Backward);
        }

        [Test]
        public void Standing_still_the_legs_return_to_the_facing()
        {
            var turn = Settle(90f);

            for (int i = 0; i < 60; i++)
                turn.Advance(0f, 0f, 1f / 60f, MaxDeg, RateDegPerSec, HysteresisDeg);

            Assert.AreEqual(0f, turn.TurnDeg, Tolerance);
        }

        [TestCase(190f, -170f)]
        [TestCase(-190f, 170f)]
        [TestCase(180f, 180f)]
        [TestCase(-180f, 180f)]
        [TestCase(540f, 180f)]
        public void Angles_wrap_into_minus_180_to_180(float degrees, float wrapped)
        {
            Assert.AreEqual(wrapped, LegTurn.Wrap(degrees), Tolerance);
        }
    }
}
