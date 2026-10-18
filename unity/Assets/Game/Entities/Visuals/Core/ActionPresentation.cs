#nullable enable

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>Action phases a view can present (spec 3.1). A channel action runs Startup, Channel, Recovery (no Active).</summary>
    public enum ActionPresentationPhase
    {
        Startup = 0,
        Active = 1,
        Channel = 2,
        Recovery = 3,
    }

    /// <summary>Reactions that cut whatever action is playing (spec 3.3: R-3.3-07 flinch, poise-break stagger, death).</summary>
    public enum ReactionKind
    {
        Flinched = 0,
        Staggered = 1,
        Dead = 2,
    }

    /// <summary>
    /// What the entity views show of the action machine (backlog D2). Visuals follow the action timings in config,
    /// never the other way round: every call carries the phase's start tick and duration from <c>ActionDef</c>, and the
    /// view time-scales its clip to them. A view joined late (start tick in the past) starts mid-clip.
    /// The real source is an adapter over D2's action listener; until then a dev source drives it.
    /// </summary>
    public interface IActionPresentation
    {
        /// <param name="actionId">The <c>ActionDef</c> id (not the wire netId).</param>
        /// <param name="phaseStartTick">Server tick the phase started at (the view's own tick base).</param>
        /// <param name="phaseDurationMs">Length of the phase; for a channel, how long it runs.</param>
        void OnActionPhase(string actionId, ActionPresentationPhase phase, uint phaseStartTick, int phaseDurationMs);

        /// <summary>A flinch or stagger cuts the current action; <see cref="ReactionKind.Dead"/> holds its last frame.</summary>
        /// <param name="durationMs">Length of the flinch or stagger; ignored for <see cref="ReactionKind.Dead"/>.</param>
        void OnReaction(ReactionKind kind, uint startTick, int durationMs);

        /// <summary>The action ended or was cancelled: back to locomotion.</summary>
        void OnActionEnd();
    }
}
