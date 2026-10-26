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
        private double _nowTick;

        public EntityVisual(GameObject instance, float tickMs)
        {
            Instance = instance;
            _tickMs = tickMs;
            var rig = instance.GetComponent<EntityVisualRig>();
            var animator = rig != null ? rig.Animator : null;
            _set = rig != null ? rig.AnimationSet : null;
            if (animator == null || _set == null || _set.idle == null || _set.walk == null)
                return;

            var run = _set.run != null ? _set.run : _set.walk;
            _clips = _set.ActionLayerClips();
            _animator = new VisualAnimator(animator, _set.idle, _set.walk, run, _clips);
            _state = new VisualAnimationState(_set.idle.length, _set.walk.length, run.length, _set.crossFadeMs / 1000f, _set.phaseHoldMs);
        }

        public GameObject Instance { get; }

        public bool IsAnimated => _state != null;

        /// <summary>Exposed for tests and debugging views.</summary>
        public VisualAnimationState? State => _state;

        /// <summary>Advances the animation; <paramref name="nowTick"/> is the tick the entity is drawn at.</summary>
        public void Update(float deltaS, float speedCmPerSec, double nowTick)
        {
            _nowTick = nowTick;
            if (_state == null || _animator == null || _set == null)
                return;
            _state.Advance(deltaS, speedCmPerSec, _set.walkReferenceSpeedCmPerSec, _set.runReferenceSpeedCmPerSec);
            _animator.Apply(_state);
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
