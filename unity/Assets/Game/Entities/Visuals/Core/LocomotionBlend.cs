#nullable enable
using System;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>Idle, walk and run weights (sum 1) and the gait playback rate for one ground speed.</summary>
    public struct LocomotionWeights
    {
        public float Idle;
        public float Walk;
        public float Run;

        /// <summary>Playback rate of the walk and run clips (1 = the clip's own speed).</summary>
        public float GaitRate;
    }

    /// <summary>
    /// Ground speed to an idle / walk / run blend (pure, no Unity). The reference speeds are art data: the speed at which
    /// each clip's feet match the ground (<see cref="AnimationSet"/>). Below the walk speed, walk fades in over idle at
    /// its own rate; between walk and run the two cross-fade and play at the rate that keeps the feet planted; above the
    /// run speed, run plays faster, up to <see cref="MaxGaitRate"/>.
    /// </summary>
    public static class LocomotionBlend
    {
        /// <summary>Clips sped up beyond this look broken; the feet slide instead.</summary>
        public const float MaxGaitRate = 2.5f;

        public static LocomotionWeights Evaluate(float speedCmPerSec, float walkReferenceCmPerSec, float runReferenceCmPerSec)
        {
            var weights = new LocomotionWeights();
            if (walkReferenceCmPerSec <= 0f || speedCmPerSec <= 0f)
            {
                weights.Idle = 1f;
                weights.GaitRate = 1f;
                return weights;
            }

            if (speedCmPerSec < walkReferenceCmPerSec)
            {
                weights.Walk = speedCmPerSec / walkReferenceCmPerSec;
                weights.Idle = 1f - weights.Walk;
                weights.GaitRate = 1f;
                return weights;
            }

            if (runReferenceCmPerSec <= walkReferenceCmPerSec || speedCmPerSec >= runReferenceCmPerSec)
            {
                bool hasRun = runReferenceCmPerSec > walkReferenceCmPerSec;
                float reference = hasRun ? runReferenceCmPerSec : walkReferenceCmPerSec;
                if (hasRun)
                    weights.Run = 1f;
                else
                    weights.Walk = 1f;
                weights.GaitRate = Math.Min(MaxGaitRate, speedCmPerSec / reference);
                return weights;
            }

            float t = (speedCmPerSec - walkReferenceCmPerSec) / (runReferenceCmPerSec - walkReferenceCmPerSec);
            weights.Walk = 1f - t;
            weights.Run = t;
            float blendedReference = walkReferenceCmPerSec + t * (runReferenceCmPerSec - walkReferenceCmPerSec);
            weights.GaitRate = Math.Min(MaxGaitRate, speedCmPerSec / blendedReference);
            return weights;
        }

        /// <summary>Frame-rate independent exponential smoothing of a measured speed.</summary>
        public static float Smooth(float current, float measured, float deltaS, float timeConstantS)
        {
            if (timeConstantS <= 0f || deltaS <= 0f)
                return deltaS <= 0f ? current : measured;
            float k = 1f - (float)Math.Exp(-deltaS / timeConstantS);
            return current + (measured - current) * k;
        }
    }
}
