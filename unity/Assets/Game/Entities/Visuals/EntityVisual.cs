#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// One instantiated visual (body, weapons, animation). Presents actions and reactions through its
    /// <see cref="AnimationSet"/>, converting the phase start ticks to elapsed time with the tick the view was last
    /// advanced to. Without a rig or a usable set it stays static and ignores the presentation calls.
    /// </summary>
    public sealed class EntityVisual : IActionPresentation, IDisposable
    {
        private readonly AnimationSet? _set;
        private readonly VisualAnimator? _animator;
        private readonly VisualAnimationState? _state;
        private readonly List<AnimationClip> _clips = new List<AnimationClip>();
        private readonly float _tickMs;
        private readonly float _scale;
        private double _nowTick;

        /// <param name="scale">
        /// The uniform scale the instance is shown at (<c>EntityVisualDef.scalePercent</c> / 100). The set's reference speeds
        /// are the prefab's own; a body drawn twice as big covers twice the ground per step, so they scale with it.
        /// </param>
        public EntityVisual(GameObject instance, float tickMs, float scale)
        {
            Instance = instance;
            _tickMs = tickMs;
            _scale = scale > 0f ? scale : 1f;
            var rig = instance.GetComponent<EntityVisualRig>();
            var animator = rig != null ? rig.Animator : null;
            _set = rig != null ? rig.AnimationSet : null;
            if (animator == null || _set == null || _set.idle == null || _set.walk == null)
                return;

            var slotClips = new AnimationClip[LocomotionProfile.Slots];
            var profile = Profile(_set, _scale, slotClips);
            _clips = _set.ActionLayerClips();
            _animator = new VisualAnimator(animator, _set.idle, slotClips, _clips);
            if (!_animator.CanTurnLegs && profile.LegTurnMaxDeg > 0f)
            {
                Debug.LogWarning($"[Visuals] {instance.name}: animation set {_set.name} turns the legs but the body has no humanoid hips and spine; the legs stay on the facing.");
                profile = Profile(_set, _scale, slotClips, legTurn: false);
            }
            _state = new VisualAnimationState(profile, _set.crossFadeMs / 1000f, _set.phaseHoldMs);
        }

        public GameObject Instance { get; }

        public bool IsAnimated => _state != null;

        /// <summary>Exposed for tests and debugging views.</summary>
        public VisualAnimationState? State => _state;

        /// <summary>
        /// Advances the animation for a ground velocity in the facing's frame (<see cref="GroundVelocity"/>);
        /// <paramref name="nowTick"/> is the tick the entity is drawn at.
        /// </summary>
        public void Update(float deltaS, float forwardCmPerSec, float rightCmPerSec, double nowTick)
        {
            _nowTick = nowTick;
            if (_state == null || _animator == null || _set == null)
                return;
            _state.Advance(deltaS, forwardCmPerSec, rightCmPerSec);
            _animator.Apply(_state);
        }

        /// <summary>Advances the animation for a body moving straight along its facing at <paramref name="speedCmPerSec"/>.</summary>
        public void Update(float deltaS, float speedCmPerSec, double nowTick) => Update(deltaS, speedCmPerSec, 0f, nowTick);

        /// <summary>
        /// The set's locomotion for a body shown at <paramref name="scale"/>: the forward loops and the directional slots
        /// (an empty slot repeats its gait's forward loop; without a run loop the run slots repeat the walk ones), with the
        /// reference speeds scaled. Fills <paramref name="slotClips"/> with the clip of each slot. The set needs its idle and
        /// walk clips.
        /// </summary>
        public static LocomotionProfile Profile(AnimationSet set, float scale, AnimationClip[] slotClips, bool legTurn = true)
        {
            var walk = set.walk!;
            bool hasRun = set.run != null;
            var slots = new LocomotionSlot[LocomotionProfile.Slots];
            for (int gait = 0; gait < 2; gait++)
            {
                bool isWalk = gait == 0 || !hasRun;
                var forwardClip = isWalk ? walk : set.run!;
                float forwardReference = (isWalk ? set.walkReferenceSpeedCmPerSec : set.runReferenceSpeedCmPerSec) * scale;
                for (int direction = 0; direction < LocomotionProfile.Directions; direction++)
                {
                    var slot = set.Slot(isWalk, direction);
                    int index = LocomotionProfile.SlotIndex(gait == 0, direction);
                    if (slot != null && slot.clip != null)
                    {
                        slotClips[index] = slot.clip;
                        slots[index] = new LocomotionSlot(slot.clip.length, slot.reverse, slot.referenceSpeedCmPerSec * scale);
                    }
                    else
                    {
                        slotClips[index] = forwardClip;
                        slots[index] = new LocomotionSlot(forwardClip.length, false, forwardReference);
                    }
                }
            }
            return new LocomotionProfile(set.idle!.length, slots,
                legTurn ? set.legTurnMaxDeg : 0f, set.legTurnDegPerSec, set.legTurnHysteresisDeg);
        }

        /// <summary>Sets the tick base without advancing (a presentation call right after spawning).</summary>
        public void SyncTick(double nowTick) => _nowTick = nowTick;

        /// <summary>The animation set's entry for <paramref name="actionId"/> (its clip and its effect), or null.</summary>
        public ActionClipEntry? FindAction(string actionId) => _set != null ? _set.FindAction(actionId) : null;

        public void OnActionPhase(string actionId, ActionPresentationPhase phase, uint phaseStartTick, int phaseDurationMs)
        {
            if (_state == null || _set == null)
                return;
            var entry = _set.FindAction(actionId);
            if (entry == null || entry.clip == null)
                return;
            float elapsedMs = ElapsedMs(phaseStartTick);
            bool last = phase == ActionPresentationPhase.Recovery;
            if (phase == ActionPresentationPhase.Channel && entry.channelLoop != null)
            {
                _state.PlayPhase(_clips.IndexOf(entry.channelLoop), entry.channelLoop.length, 0f, 1f, phaseDurationMs, elapsedMs, loop: true, last);
                return;
            }
            PhaseClipTiming.Segment(phase, entry.startupEnd, entry.activeEnd, out float from01, out float to01);
            _state.PlayPhase(_clips.IndexOf(entry.clip), entry.clip.length, from01, to01, phaseDurationMs, elapsedMs,
                loop: phase == ActionPresentationPhase.Channel, last);
        }

        public void OnReaction(ReactionKind kind, uint startTick, int durationMs)
        {
            if (_state == null || _set == null)
                return;
            var clip = _set.Reaction(kind);
            if (clip == null)
            {
                _state.Stop();
                return;
            }
            bool dead = kind == ReactionKind.Dead;
            int duration = dead ? Mathf.RoundToInt(clip.length * 1000f) : durationMs;
            _state.PlayReaction(_clips.IndexOf(clip), clip.length, duration, ElapsedMs(startTick), hold: dead);
        }

        public void OnActionEnd() => _state?.Stop();

        public void Dispose()
        {
            _animator?.Dispose();
            if (Instance != null)
                VisualObjects.Destroy(Instance);
        }

        private float ElapsedMs(uint startTick) => (float)Math.Max(0.0, (_nowTick - startTick) * _tickMs);
    }
}
