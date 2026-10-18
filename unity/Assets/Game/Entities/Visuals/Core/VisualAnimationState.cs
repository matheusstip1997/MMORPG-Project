#nullable enable
using System;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// What one entity's animation should show this frame (pure, no Unity; <see cref="VisualAnimator"/> applies it).
    /// Two layers: locomotion (idle / walk / run from ground speed) and an action layer playing one clip segment at a
    /// time (an action phase or a reaction). While the action layer plays, locomotion is faded out completely, so an
    /// action that moves the entity (a dash) never fights the walk cycle; ground speed keeps being tracked so the blend
    /// resumes in the right gait. A new clip on the action layer (a flinch cutting an attack) cross-fades from the frozen
    /// pose of the old one. Clips are referred to by index; the caller owns the clip list.
    /// </summary>
    public sealed class VisualAnimationState
    {
        private enum EndMode
        {
            /// <summary>Fade back to locomotion when the segment ends (recovery, flinch, stagger).</summary>
            FadeOut,

            /// <summary>Hold the last frame for a grace period waiting for the next phase (startup, active, channel).</summary>
            HoldThenFade,

            /// <summary>Hold the last frame until something else plays (death).</summary>
            Hold,
        }

        private struct Track
        {
            public bool Playing;
            public int Clip;
            public float LengthS;
            public float From01;
            public float To01;
            public int DurationMs;
            public float ElapsedMs;
            public bool Loop;
            public EndMode End;

            public float TimeS => PhaseClipTiming.ClipTime(LengthS, From01, To01, DurationMs, ElapsedMs, Loop);
        }

        private readonly float _idleLengthS;
        private readonly float _walkLengthS;
        private readonly float _runLengthS;
        private readonly float _crossFadeS;
        private readonly float _phaseHoldMs;
        private Track _current;
        private bool _hasCurrent;
        private float _idleTimeS;
        private float _gaitCycle;

        /// <param name="runLengthS">Length of the run clip, or of the walk clip when the set has no run clip.</param>
        public VisualAnimationState(float idleLengthS, float walkLengthS, float runLengthS, float crossFadeS, float phaseHoldMs)
        {
            _idleLengthS = Math.Max(0.001f, idleLengthS);
            _walkLengthS = Math.Max(0.001f, walkLengthS);
            _runLengthS = Math.Max(0.001f, runLengthS);
            _crossFadeS = Math.Max(0f, crossFadeS);
            _phaseHoldMs = Math.Max(0f, phaseHoldMs);
            LocomotionWeight = 1f;
            ActionBlend = 1f;
            PreviousClip = -1;
            Locomotion = new LocomotionWeights { Idle = 1f, GaitRate = 1f };
        }

        /// <summary>1 = locomotion only, 0 = action layer only.</summary>
        public float LocomotionWeight { get; private set; }

        public LocomotionWeights Locomotion { get; private set; }

        public float IdleTimeS => PhaseClipTiming.Repeat(_idleTimeS, _idleLengthS);

        public float WalkTimeS => _gaitCycle * _walkLengthS;

        public float RunTimeS => _gaitCycle * _runLengthS;

        /// <summary>True while an action or reaction segment plays (not counting its fade back to locomotion).</summary>
        public bool IsActionPlaying => _hasCurrent && _current.Playing;

        /// <summary>Clip index on the action layer; -1 when nothing has played yet.</summary>
        public int ActionClip => _hasCurrent ? _current.Clip : -1;

        public float ActionTimeS => _hasCurrent ? _current.TimeS : 0f;

        /// <summary>Clip being faded out on the action layer, or -1.</summary>
        public int PreviousClip { get; private set; }

        public float PreviousTimeS { get; private set; }

        /// <summary>Weight of <see cref="ActionClip"/> against <see cref="PreviousClip"/> inside the action layer.</summary>
        public float ActionBlend { get; private set; }

        /// <summary>One segment of an action clip for one phase (see <see cref="PhaseClipTiming"/>).</summary>
        /// <param name="elapsedMs">How far into the phase the entity already is (late join).</param>
        public void PlayPhase(int clip, float clipLengthS, float from01, float to01, int durationMs, float elapsedMs, bool loop, bool last)
        {
            Start(new Track
            {
                Playing = true, Clip = clip, LengthS = clipLengthS, From01 = from01, To01 = to01, DurationMs = durationMs,
                ElapsedMs = elapsedMs, Loop = loop, End = last ? EndMode.FadeOut : EndMode.HoldThenFade,
            });
        }

        /// <summary>
        /// A whole reaction clip time-scaled to <paramref name="durationMs"/>; it cuts any action. With
        /// <paramref name="hold"/> (death) the last frame stays until something else plays.
        /// </summary>
        public void PlayReaction(int clip, float clipLengthS, int durationMs, float elapsedMs, bool hold)
        {
            Start(new Track
            {
                Playing = true, Clip = clip, LengthS = clipLengthS, From01 = 0f, To01 = 1f, DurationMs = durationMs,
                ElapsedMs = elapsedMs, Loop = false, End = hold ? EndMode.Hold : EndMode.FadeOut,
            });
        }

        /// <summary>Ends the action layer: fade back to locomotion.</summary>
        public void Stop()
        {
            if (_hasCurrent)
                _current.Playing = false;
        }

        /// <summary>Advances every layer by one frame.</summary>
        public void Advance(float deltaS, float speedCmPerSec, float walkReferenceCmPerSec, float runReferenceCmPerSec)
        {
            deltaS = Math.Max(0f, deltaS);
            float fadeStep = _crossFadeS > 0f ? deltaS / _crossFadeS : 1f;

            if (_hasCurrent && _current.Playing)
            {
                _current.ElapsedMs += deltaS * 1000f;
                if (Ended(_current))
                    _current.Playing = false;
            }

            float target = IsActionPlaying ? 0f : 1f;
            LocomotionWeight = MoveTowards(LocomotionWeight, target, fadeStep);
            ActionBlend = MoveTowards(ActionBlend, 1f, fadeStep);
            if (ActionBlend >= 1f)
                PreviousClip = -1;

            var weights = LocomotionBlend.Evaluate(speedCmPerSec, walkReferenceCmPerSec, runReferenceCmPerSec);
            Locomotion = weights;
            _idleTimeS += deltaS;
            // Walk and run share one gait cycle, so the feet stay in step while they cross-fade.
            float gaitWeight = weights.Walk + weights.Run;
            float cyclesPerSecond = gaitWeight > 0f
                ? (weights.Walk / _walkLengthS + weights.Run / _runLengthS) / gaitWeight
                : 1f / _walkLengthS;
            _gaitCycle = PhaseClipTiming.Repeat(_gaitCycle + deltaS * weights.GaitRate * cyclesPerSecond, 1f);
        }

        private void Start(Track track)
        {
            if (_hasCurrent && LocomotionWeight < 1f && track.Clip != _current.Clip)
            {
                // The old clip is visible: freeze it and cross-fade inside the action layer.
                PreviousClip = _current.Clip;
                PreviousTimeS = _current.TimeS;
                ActionBlend = 0f;
            }
            else if (!_hasCurrent || LocomotionWeight >= 1f)
            {
                PreviousClip = -1;
                ActionBlend = 1f;
            }
            _current = track;
            _hasCurrent = true;
            if (Ended(_current))
                _current.Playing = false;
        }

        private bool Ended(in Track track)
        {
            switch (track.End)
            {
                case EndMode.Hold:
                    return false;
                case EndMode.HoldThenFade:
                    return track.ElapsedMs >= track.DurationMs + _phaseHoldMs;
                default:
                    return !track.Loop && track.ElapsedMs >= track.DurationMs;
            }
        }

        private static float MoveTowards(float value, float target, float step) =>
            value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);
    }
}
