#nullable enable
using System;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// An entity's measured ground velocity in its facing's frame (pure, no Unity): how fast it moves along its facing
    /// (<see cref="ForwardCmPerSec"/>) and to its right (<see cref="RightCmPerSec"/>), the two parameters of the
    /// directional locomotion blend. Measured from where the entity is drawn each frame (predicted for the local player,
    /// interpolated for the others), so animation needs nothing from prediction or networking. Each component is smoothed
    /// in the facing's frame, so a turn of the facing (the mouse, or the facing unlocking after an action) blends instead
    /// of popping. A step faster than the snap limit (a correction, a teleport) is not movement and keeps the last value.
    /// Ground axes: x east, z north, like Unity's; the yaw is clockwise from +z (Unity's yaw).
    /// </summary>
    public struct GroundVelocity
    {
        public float ForwardCmPerSec;
        public float RightCmPerSec;

        public float SpeedCmPerSec => (float)Math.Sqrt(ForwardCmPerSec * ForwardCmPerSec + RightCmPerSec * RightCmPerSec);

        /// <param name="stepXCm">Ground step along x since the last frame, cm.</param>
        /// <param name="stepZCm">Ground step along z since the last frame, cm.</param>
        /// <param name="yawDeg">The facing this frame, degrees clockwise from +z.</param>
        public void Measure(float stepXCm, float stepZCm, float yawDeg, float deltaS, float smoothingS, float maxSpeedCmPerSec)
        {
            if (deltaS <= 0f)
                return;
            ToFacing(stepXCm / deltaS, stepZCm / deltaS, yawDeg, out float forward, out float right);
            if (forward * forward + right * right > maxSpeedCmPerSec * maxSpeedCmPerSec)
                return;
            ForwardCmPerSec = LocomotionBlend.Smooth(ForwardCmPerSec, forward, deltaS, smoothingS);
            RightCmPerSec = LocomotionBlend.Smooth(RightCmPerSec, right, deltaS, smoothingS);
        }

        /// <summary>A ground vector (x east, z north) in the frame of a facing <paramref name="yawDeg"/> clockwise from +z.</summary>
        public static void ToFacing(float x, float z, float yawDeg, out float forward, out float right)
        {
            double radians = yawDeg * (Math.PI / 180.0);
            float sin = (float)Math.Sin(radians), cos = (float)Math.Cos(radians);
            forward = x * sin + z * cos;
            right = x * cos - z * sin;
        }
    }
}
