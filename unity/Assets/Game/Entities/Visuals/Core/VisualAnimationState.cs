#nullable enable
using System;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// What one entity's animation should show this frame (pure, no Unity; <see cref="VisualAnimator"/> applies it).
    /// Two layers: locomotion and an action layer playing one clip segment at a time (an action phase or a reaction).
    /// Locomotion follows the ground velocity in the facing's frame (forward, right; <see cref="GroundVelocity"/>): the
    /// <see cref="LegTurn"/> turns the legs toward the movement, the rest of the direction blends the four directional
    /// slots (<see cref="LocomotionBlend.Directions"/>), and the speed blends idle / walk / run against the
    /// direction-weighted reference speeds. While the action layer plays, locomotion (and with it the leg turn) is faded out
    /// completely, so an action that moves the entity (a dash) never fights the walk cycle; the velocity keeps being
    /// tracked so the blend resumes in the right gait and direction. A new clip on the action layer (a flinch cutting an
    /// attack) cross-fades from the frozen pose of the old one. Clips are referred to by index; the caller owns the clip
    /// list.
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

        private readonly LocomotionProfile _profile;
        private readonly float _crossFadeS;
        private readonly float _phaseHoldMs;
        private readonly float[] _slotWeights = new float[LocomotionProfile.Slots];
        private Track _current;
        private bool _hasCurrent;
        private float _idleTimeS;
        private float _gaitCycle;
        private LegTurn _legTurn;

        public VisualAnimationState(LocomotionProfile profile, float crossFadeS, float phaseHoldMs)
        {
            _profile = profile;
            _crossFadeS = Math.Max(0f, crossFadeS);
            _phaseHoldMs = Math.Max(0f, phaseHoldMs);
            LocomotionWeight = 1f;
            ActionBlend = 1f;
            PreviousClip = -1;
            Locomotion = new LocomotionWeights { Idle = 1f, GaitRate = 1f };
            Directions = new DirectionWeights { Forward = 1f };
        }

        /// <summary>1 = locomotion only, 0 = action layer only.</summary>
        public float LocomotionWeight { get; private set; }

        /// <summary>Idle, walk and run weights and the gait rate.</summary>
        public LocomotionWeights Locomotion { get; private set; }

        /// <summary>The ground velocity along the facing, cm/s (the blend's first parameter, as last given).</summary>
        public float ForwardCmPerSec { get; private set; }

        /// <summary>The ground velocity to the facing's right, cm/s (the blend's second parameter, as last given).</summary>
        public float RightCmPerSec { get; private set; }

        /// <summary>Weights of the four directional slots, for the movement as the turned legs see it.</summary>
        public DirectionWeights Directions { get; private set; }

        /// <summary>
        /// The leg turn to show this frame, degrees clockwise: the turn reached, faded in with the walk (none standing
        /// still) and with the locomotion layer (none while an action plays).
        /// </summary>
        public float LegTurnDeg { get; private set; }

        /// <summary>True while the legs walk backward (the movement is in the facing's back half; leg-turn bodies only).</summary>
        public bool LegsBackward => _legTurn.Backward;

        public float IdleTimeS => PhaseClipTiming.Repeat(_idleTimeS, _profile.IdleLengthS);

        /// <summary>Weight of a directional slot (<see cref="LocomotionProfile.SlotIndex"/>) inside the locomotion layer; idle has the rest.</summary>
        public float SlotWeight(int slot) => _slotWeights[slot];

        /// <summary>Time of a directional slot's clip: the shared gait cycle, backward for a reversed slot.</summary>
        public float SlotTimeS(int slot)
        {
            var s = _profile.Slot(slot);
            return (s.Reverse ? 1f - _gaitCycle : _gaitCycle) * s.LengthS;
        }

        /// <summary>The forward walk's time (tests and debugging views).</summary>
        public float WalkTimeS => SlotTimeS(LocomotionProfile.SlotIndex(true, (int)LocomotionDirection.Forward));

        /// <summary>The forward run's time (tests and debugging views).</summary>
        public float RunTimeS => SlotTimeS(LocomotionProfile.SlotIndex(false, (int)LocomotionDirection.Forward));

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

        /// <summary>Advances every layer by one frame, for a ground velocity in the facing's frame (<see cref="GroundVelocity"/>).</summary>
        public void Advance(float deltaS, float forwardCmPerSec, float rightCmPerSec)
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

            AdvanceLocomotion(deltaS, forwardCmPerSec, rightCmPerSec);
        }

        private void AdvanceLocomotion(float deltaS, float forwardCmPerSec, float rightCmPerSec)
        {
            ForwardCmPerSec = forwardCmPerSec;
            RightCmPerSec = rightCmPerSec;
            float speed = (float)Math.Sqrt(forwardCmPerSec * forwardCmPerSec + rightCmPerSec * rightCmPerSec);
            int walkForward = LocomotionProfile.SlotIndex(true, (int)LocomotionDirection.Forward);
            int runForward = LocomotionProfile.SlotIndex(false, (int)LocomotionDirection.Forward);

            // The legs turn toward the movement and fade in with the walk (standing still they face the facing). While
            // an action hides locomotion they follow the movement at once, so it resumes along the movement of that moment.
            if (LocomotionWeight > 0f)
                _legTurn.Advance(forwardCmPerSec, rightCmPerSec, deltaS, _profile.LegTurnMaxDeg, _profile.LegTurnDegPerSec, _profile.LegTurnHysteresisDeg);
            else
                _legTurn.Snap(forwardCmPerSec, rightCmPerSec, _profile.LegTurnMaxDeg);
            var moving = LocomotionBlend.Evaluate(speed, _profile.Slot(walkForward).ReferenceCmPerSec, _profile.Slot(runForward).ReferenceCmPerSec);
            float legTurn = _legTurn.TurnDeg * (1f - moving.Idle);
            LegTurnDeg = legTurn * LocomotionWeight;

            // The rest of the direction, as the turned legs see it, picks the directional slots.
            LegTurn.Residual(forwardCmPerSec, rightCmPerSec, legTurn, out float legsForward, out float legsRight);
            var directions = LocomotionBlend.Directions(legsForward, legsRight);
            Directions = directions;
            float walkReference = 0f, runReference = 0f;
            for (int d = 0; d < LocomotionProfile.Directions; d++)
            {
                walkReference += directions[d] * _profile.Slot(true, d).ReferenceCmPerSec;
                runReference += directions[d] * _profile.Slot(false, d).ReferenceCmPerSec;
            }

            var weights = LocomotionBlend.Evaluate(speed, walkReference, runReference);
            Locomotion = weights;
            _idleTimeS += deltaS;
            // Every directional loop shares one gait cycle, so the feet stay in step while they cross-fade.
            float gaitWeight = 0f, cyclesPerSecond = 0f;
            for (int d = 0; d < LocomotionProfile.Directions; d++)
            {
                int walk = LocomotionProfile.SlotIndex(true, d), run = LocomotionProfile.SlotIndex(false, d);
                _slotWeights[walk] = weights.Walk * directions[d];
                _slotWeights[run] = weights.Run * directions[d];
                gaitWeight += _slotWeights[walk] + _slotWeights[run];
                cyclesPerSecond += _slotWeights[walk] / _profile.Slot(walk).LengthS + _slotWeights[run] / _profile.Slot(run).LengthS;
            }
            cyclesPerSecond = gaitWeight > 0f ? cyclesPerSecond / gaitWeight : 1f / _profile.Slot(walkForward).LengthS;
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
