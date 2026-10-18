#nullable enable
using Shared.Protocol;
using UnityEngine;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// One entity on screen: a root transform placed by <see cref="EntityViews"/>, holding either an
    /// <see cref="EntityVisual"/> or the capsule placeholder. Measures its own ground speed from where it is drawn, so
    /// animation needs nothing from prediction or networking.
    /// </summary>
    public sealed class EntityView : IActionPresentation
    {
        private Vector3 _lastPosition;
        private bool _hasLastPosition;

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
        public float SpeedCmPerSec { get; private set; }

        internal void SetVisual(EntityVisual? visual)
        {
            Visual?.Dispose();
            Visual = visual;
        }

        /// <summary>Measures the speed from the drawn position and advances the animation.</summary>
        internal void Animate(float deltaS, double nowTick, float smoothingS, float maxSpeedCmPerSec)
        {
            var position = Root.position;
            if (_hasLastPosition && deltaS > 0f)
            {
                var step = position - _lastPosition;
                step.y = 0f;
                float measured = step.magnitude * WorldUnits.CmPerUnit / deltaS;
                // A snap (correction, teleport) is not walking.
                if (measured > maxSpeedCmPerSec)
                    measured = SpeedCmPerSec;
                SpeedCmPerSec = LocomotionBlend.Smooth(SpeedCmPerSec, measured, deltaS, smoothingS);
            }
            _lastPosition = position;
            _hasLastPosition = true;
            Visual?.Update(deltaS, SpeedCmPerSec, nowTick);
        }

        internal void SyncTick(double nowTick) => Visual?.SyncTick(nowTick);

        public void OnActionPhase(string actionId, ActionPresentationPhase phase, uint phaseStartTick, int phaseDurationMs) =>
            Visual?.OnActionPhase(actionId, phase, phaseStartTick, phaseDurationMs);

        public void OnReaction(ReactionKind kind, uint startTick, int durationMs) => Visual?.OnReaction(kind, startTick, durationMs);

        public void OnActionEnd() => Visual?.OnActionEnd();
    }
}
