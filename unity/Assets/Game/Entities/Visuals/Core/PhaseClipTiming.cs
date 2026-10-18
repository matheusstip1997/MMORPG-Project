#nullable enable
using System;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// Maps an action phase onto a clip (pure, no Unity). One clip covers a whole action and is cut in three segments by
    /// two normalized marks: Startup is [0, startupEnd], Active and Channel are [startupEnd, activeEnd], Recovery is
    /// [activeEnd, 1]. Each segment is time-scaled to fill its phase exactly, so the clip follows the ActionDef timing.
    /// A channel loops its segment at the clip's own speed instead of stretching it over a long channel.
    /// </summary>
    public static class PhaseClipTiming
    {
        public static void Segment(ActionPresentationPhase phase, float startupEnd01, float activeEnd01, out float from01, out float to01)
        {
            startupEnd01 = Clamp01(startupEnd01);
            activeEnd01 = Math.Max(startupEnd01, Clamp01(activeEnd01));
            switch (phase)
            {
                case ActionPresentationPhase.Startup:
                    from01 = 0f;
                    to01 = startupEnd01;
                    break;
                case ActionPresentationPhase.Recovery:
                    from01 = activeEnd01;
                    to01 = 1f;
                    break;
                default:
                    from01 = startupEnd01;
                    to01 = activeEnd01;
                    break;
            }
        }

        /// <summary>Playback rate that makes the segment last exactly <paramref name="phaseDurationMs"/>.</summary>
        public static float PlaybackRate(float clipLengthS, float from01, float to01, int phaseDurationMs)
        {
            if (phaseDurationMs <= 0)
                return 0f;
            return (to01 - from01) * clipLengthS / (phaseDurationMs / 1000f);
        }

        /// <summary>
        /// Clip time (seconds) after <paramref name="elapsedMs"/> of the phase. A non-looping segment ends on its last
        /// frame and stays there; a looping one (channel) wraps at the clip's own speed. A zero-length segment shows its
        /// end frame.
        /// </summary>
        public static float ClipTime(float clipLengthS, float from01, float to01, int phaseDurationMs, float elapsedMs, bool loop)
        {
            float fromS = from01 * clipLengthS;
            float lengthS = (to01 - from01) * clipLengthS;
            if (lengthS <= 0f)
                return to01 * clipLengthS;
            float elapsedS = Math.Max(0f, elapsedMs) / 1000f;
            if (loop)
                return fromS + Repeat(elapsedS, lengthS);
            if (phaseDurationMs <= 0)
                return fromS + lengthS;
            float progress = Clamp01(elapsedS / (phaseDurationMs / 1000f));
            return fromS + progress * lengthS;
        }

        internal static float Repeat(float value, float length)
        {
            float wrapped = value - (float)Math.Floor(value / length) * length;
            return wrapped >= length ? 0f : wrapped;
        }

        internal static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
    }
}
