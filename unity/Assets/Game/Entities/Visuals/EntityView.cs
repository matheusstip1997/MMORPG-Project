#nullable enable
using Shared.Protocol;
using UnityEngine;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// One entity on screen: a root transform placed by <see cref="EntityViews"/>, holding either an
    /// <see cref="EntityVisual"/> or the capsule placeholder. Measures its own ground velocity in its facing's frame from
    /// where and how it is drawn (<see cref="GroundVelocity"/>), so animation needs nothing from prediction or networking.
    /// </summary>
    public sealed class EntityView : IActionPresentation
    {
        private Vector3 _lastPosition;
        private bool _hasLastPosition;
        private GroundVelocity _velocity;

        internal EntityView(Transform root, string? visualId, EntityKind kind, Color color)
        {
            Root = root;
            VisualId = visualId;
            Kind = kind;
            Color = color;
        }

        public Transform Root { get; }

        public string? VisualId { get; internal set; }

        public EntityKind Kind { get; internal set; }

        internal Color Color { get; set; }

        public EntityVisual? Visual { get; private set; }

        public GameObject? Capsule { get; internal set; }

        /// <summary>Waiting for a prefab to finish loading.</summary>
        internal bool Pending { get; set; }

        /// <summary>Smoothed horizontal speed, cm/s.</summary>
        public float SpeedCmPerSec => _velocity.SpeedCmPerSec;

        /// <summary>Smoothed ground velocity along the facing and to its right, cm/s (the directional blend's parameters).</summary>
        public GroundVelocity Velocity => _velocity;

        internal void SetVisual(EntityVisual? visual)
        {
            Visual?.Dispose();
            Visual = visual;
        }

        /// <summary>Measures the velocity from the drawn position and facing, and advances the animation.</summary>
        internal void Animate(float deltaS, double nowTick, float smoothingS, float maxSpeedCmPerSec)
        {
            var position = Root.position;
            if (_hasLastPosition && deltaS > 0f)
            {
                var step = (position - _lastPosition) * WorldUnits.CmPerUnit;
                // A snap (correction, teleport) is not walking: GroundVelocity keeps the last value.
                _velocity.Measure(step.x, step.z, Root.eulerAngles.y, deltaS, smoothingS, maxSpeedCmPerSec);
            }
            _lastPosition = position;
            _hasLastPosition = true;
            Visual?.Update(deltaS, _velocity.ForwardCmPerSec, _velocity.RightCmPerSec, nowTick);
        }

        internal void SyncTick(double nowTick) => Visual?.SyncTick(nowTick);

        /// <summary>Shows the actions' effects (set by <see cref="EntityViews"/>; null: none).</summary>
        internal ActionVfx? Vfx { get; set; }

        public void OnActionPhase(string actionId, ActionPresentationPhase phase, uint phaseStartTick, int phaseDurationMs)
        {
            Visual?.OnActionPhase(actionId, phase, phaseStartTick, phaseDurationMs);
            if (phase == ActionPresentationPhase.Active && Visual != null)
                Vfx?.Play(Visual.FindAction(actionId), Root);
        }

        public void OnReaction(ReactionKind kind, uint startTick, int durationMs) => Visual?.OnReaction(kind, startTick, durationMs);

        public void OnActionEnd() => Visual?.OnActionEnd();
    }
}
