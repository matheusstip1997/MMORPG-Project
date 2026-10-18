#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Prediction;
using Shared.Simulation.Actions;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// Connects the action machine's phase reports (backlog D2: <see cref="LocalPlayerPredictor"/> and
    /// <see cref="RemoteActionTracker"/>) to the views' <see cref="IActionPresentation"/>, so animations follow the
    /// simulation's timings (spec 3.1, R-3.3-07). The local player is presented at once, in client ticks. Remote
    /// entities are reported when a snapshot arrives but drawn <c>InterpolationDelayMs</c> behind, so their phases wait
    /// until the render tick reaches the phase start (<see cref="ReleaseRemote"/>); otherwise a remote swing would lead
    /// its interpolated movement. Allocates only when the pending list grows.
    /// </summary>
    public sealed class ActionPresentationAdapter
    {
        private readonly Func<IActionPresentation?> _local;
        private readonly Func<uint, IActionPresentation?> _remote;
        private readonly List<ActionPhaseChange> _pending = new List<ActionPhaseChange>();

        /// <param name="local">The local player's view, or null while it is not shown.</param>
        /// <param name="remote">A remote entity's view by entity id, or null.</param>
        public ActionPresentationAdapter(Func<IActionPresentation?> local, Func<uint, IActionPresentation?> remote)
        {
            _local = local ?? throw new ArgumentNullException(nameof(local));
            _remote = remote ?? throw new ArgumentNullException(nameof(remote));
            Local = new LocalListener(this);
            Remote = new RemoteListener(this);
        }

        /// <summary>For <see cref="LocalPlayerPredictor.Listener"/>.</summary>
        public IActionPhaseListener Local { get; }

        /// <summary>For <see cref="RemoteActionTracker.Listener"/>.</summary>
        public IActionPhaseListener Remote { get; }

        public int PendingRemoteCount => _pending.Count;

        /// <summary>Presents the remote phases whose start the render tick has reached, in the order they arrived.</summary>
        public void ReleaseRemote(double renderTick)
        {
            int kept = 0;
            for (int i = 0; i < _pending.Count; i++)
            {
                var change = _pending[i];
                if (change.PhaseStartTick <= renderTick)
                {
                    var view = _remote(change.EntityId);
                    if (view != null)
                        Present(view, change);
                }
                else
                {
                    _pending[kept++] = change;
                }
            }
            _pending.RemoveRange(kept, _pending.Count - kept);
        }

        /// <summary>The entity left interest: its pending phases are dropped.</summary>
        public void Forget(uint entityId) => _pending.RemoveAll(change => change.EntityId == entityId);

        public void Clear() => _pending.Clear();

        /// <summary>One phase report as a presentation call (pure mapping).</summary>
        public static void Present(IActionPresentation view, in ActionPhaseChange change)
        {
            switch (change.Phase)
            {
                case ActionState.Startup:
                    view.OnActionPhase(change.ActionId, ActionPresentationPhase.Startup, change.PhaseStartTick, change.PhaseDurationMs);
                    break;
                case ActionState.Channel:
                    view.OnActionPhase(change.ActionId, ActionPresentationPhase.Channel, change.PhaseStartTick, change.PhaseDurationMs);
                    break;
                case ActionState.Active:
                    view.OnActionPhase(change.ActionId, ActionPresentationPhase.Active, change.PhaseStartTick, change.PhaseDurationMs);
                    break;
                case ActionState.Recovery:
                    view.OnActionPhase(change.ActionId, ActionPresentationPhase.Recovery, change.PhaseStartTick, change.PhaseDurationMs);
                    break;
                case ActionState.Flinched:
                    view.OnReaction(ReactionKind.Flinched, change.PhaseStartTick, change.PhaseDurationMs);
                    break;
                case ActionState.Staggered:
                    view.OnReaction(ReactionKind.Staggered, change.PhaseStartTick, change.PhaseDurationMs);
                    break;
                case ActionState.Dead:
                    view.OnReaction(ReactionKind.Dead, change.PhaseStartTick, 0);
                    break;
                default: // Idle: the action ended or was cancelled
                    view.OnActionEnd();
                    break;
            }
        }

        private sealed class LocalListener : IActionPhaseListener
        {
            private readonly ActionPresentationAdapter _owner;

            public LocalListener(ActionPresentationAdapter owner) => _owner = owner;

            public void OnActionPhase(in ActionPhaseChange change)
            {
                var view = _owner._local();
                if (view != null)
                    Present(view, change);
            }

            // Fired effects only look (no resolution on the client); no effect visuals yet. TODO(VFX): effect visuals.
            public void OnActionEffect(in ActionEffectEvent effect)
            {
            }
        }

        private sealed class RemoteListener : IActionPhaseListener
        {
            private readonly ActionPresentationAdapter _owner;

            public RemoteListener(ActionPresentationAdapter owner) => _owner = owner;

            public void OnActionPhase(in ActionPhaseChange change) => _owner._pending.Add(change);

            public void OnActionEffect(in ActionEffectEvent effect)
            {
            }
        }
    }
}
