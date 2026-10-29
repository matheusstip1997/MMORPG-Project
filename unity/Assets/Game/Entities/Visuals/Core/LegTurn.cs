#nullable enable
using System;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// How far the legs turn away from the facing toward the movement (pure, no Unity; degrees clockwise, seen from above).
    /// For bodies without side loops (the placeholder packs): the hips turn toward the movement and the walk plays along
    /// the legs, forward or backward, while the spine turns back so the chest keeps the facing (R-2.2-01: the mouse faces,
    /// WASD moves). Two halves: moving within 90 degrees of the facing the legs walk forward along the movement, beyond it
    /// they walk backward away from it; the half only changes once the movement is <c>hysteresis</c> past 90 degrees, so
    /// a pure strafe does not flip the legs every frame. The turn is clamped to the profile's maximum and moves toward its
    /// target at the profile's rate.
    /// </summary>
    public struct LegTurn
    {
        /// <summary>The turn the legs have reached (before the moving and layer fades), degrees clockwise.</summary>
        public float TurnDeg;

        /// <summary>True while the legs walk backward (the movement is in the facing's back half).</summary>
        public bool Backward;

        /// <summary>Advances the turn toward the movement (<paramref name="forwardCmPerSec"/>, <paramref name="rightCmPerSec"/>).</summary>
        public void Advance(float forwardCmPerSec, float rightCmPerSec, float deltaS, float maxDeg, float degPerSec, float hysteresisDeg)
        {
            if (maxDeg <= 0f)
            {
                TurnDeg = 0f;
                Backward = false;
                return;
            }
            float target = Target(forwardCmPerSec, rightCmPerSec, maxDeg, hysteresisDeg);
            float step = Math.Max(0f, degPerSec * deltaS);
            TurnDeg = TurnDeg < target ? Math.Min(target, TurnDeg + step) : Math.Max(target, TurnDeg - step);
        }

        /// <summary>
        /// Puts the legs where the movement wants them at once, on the half nearer to it (no hysteresis, no rate): for
        /// while the legs are not shown (an action plays), so locomotion resumes along the movement of that moment and not
        /// on the half a dash left behind.
        /// </summary>
        public void Snap(float forwardCmPerSec, float rightCmPerSec, float maxDeg)
        {
            if (maxDeg <= 0f)
            {
                TurnDeg = 0f;
                Backward = false;
                return;
            }
            Backward = false; // a pure strafe walks forward
            TurnDeg = Target(forwardCmPerSec, rightCmPerSec, maxDeg, 0f);
        }

        private float Target(float forwardCmPerSec, float rightCmPerSec, float maxDeg, float hysteresisDeg)
        {
            if (forwardCmPerSec == 0f && rightCmPerSec == 0f)
                return 0f;
            float theta = (float)(Math.Atan2(rightCmPerSec, forwardCmPerSec) * (180.0 / Math.PI));
            float off = Math.Abs(theta);
            if (Backward && off < 90f - hysteresisDeg)
                Backward = false;
            else if (!Backward && off > 90f + hysteresisDeg)
                Backward = true;
            float legs = Backward ? Wrap(theta - 180f) : theta;
            return Math.Max(-maxDeg, Math.Min(maxDeg, legs));
        }

        /// <summary>The movement as seen from legs turned by <paramref name="turnDeg"/> (the vector rotated by -turn).</summary>
        public static void Residual(float forwardCmPerSec, float rightCmPerSec, float turnDeg, out float forward, out float right)
        {
            double radians = turnDeg * (Math.PI / 180.0);
            float cos = (float)Math.Cos(radians), sin = (float)Math.Sin(radians);
            forward = forwardCmPerSec * cos + rightCmPerSec * sin;
            right = rightCmPerSec * cos - forwardCmPerSec * sin;
        }

        /// <summary>An angle in degrees brought into (-180, 180].</summary>
        public static float Wrap(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f)
                degrees -= 360f;
            else if (degrees <= -180f)
                degrees += 360f;
            return degrees;
        }
    }
}
