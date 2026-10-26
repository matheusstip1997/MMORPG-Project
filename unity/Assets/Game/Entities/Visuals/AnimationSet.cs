#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>One action's clip, cut into phase segments by two normalized marks (see <see cref="PhaseClipTiming"/>).</summary>
    [Serializable]
    public sealed class ActionClipEntry
    {
        [Tooltip("ActionDef id this clip presents (empty on the default entry).")]
        public string actionId = string.Empty;

        public AnimationClip? clip;

        [Tooltip("Where Startup ends and Active (or Channel) begins, as a fraction of the clip.")]
        [Range(0f, 1f)] public float startupEnd = 0.35f;

        [Tooltip("Where Active (or Channel) ends and Recovery begins, as a fraction of the clip.")]
        [Range(0f, 1f)] public float activeEnd = 0.55f;

        [Tooltip("Optional loop played during a Channel phase instead of looping the active segment.")]
        public AnimationClip? channelLoop;

        [Tooltip("Optional EntityVisualDef id of an effect shown at the entity when the Active phase starts (ActionVfx).")]
        public string vfxId = string.Empty;

        [Tooltip("How long the effect stays, in ms.")]
        [Min(0f)] public float vfxLifetimeMs = 600f;
    }

    /// <summary>
    /// One directional locomotion loop (walk or run toward one side of the body's facing). An empty slot plays the gait's
    /// forward clip. A placeholder may reuse another loop played in reverse (the forward walk read backward).
    /// </summary>
    [Serializable]
    public sealed class LocomotionClip
    {
        public AnimationClip? clip;

        [Tooltip("Play the clip backward (a forward loop reused as the backward one).")]
        public bool reverse;

        [Tooltip("Ground speed at which this loop's feet stay planted, at the prefab's own scale (measured by the visual prefab builder).")]
        [Min(0f)] public float referenceSpeedCmPerSec;

        public bool HasClip => clip != null;
    }

    /// <summary>
    /// The clips of one kind of body (art data, not gameplay: replacing the art means a new set, no code change).
    /// Reference speeds are the ground speeds at which the walk and run clips' feet match the ground; timings of actions
    /// always come from <c>ActionDef</c> through <see cref="IActionPresentation"/>. Locomotion follows the movement relative
    /// to the facing (R-2.2-01: WASD moves, the mouse faces): <see cref="walk"/> and <see cref="run"/> are the forward
    /// loops, the directional slots the backward and side ones, and the leg turn turns the hips toward the movement while
    /// the spine keeps the chest on the facing (for bodies without side loops).
    /// </summary>
    [CreateAssetMenu(menuName = "MMORPG/Visuals/Animation Set", fileName = "AnimationSet")]
    public sealed class AnimationSet : ScriptableObject
    {
        [Header("Locomotion")]
        public AnimationClip? idle;
        public AnimationClip? walk;

        [Tooltip("Optional; without it the walk clip is played faster.")]
        public AnimationClip? run;

        [Tooltip("Ground speed at which the walk clip's feet stay planted, at the prefab's own scale (EntityVisual scales it by EntityVisualDef.scalePercent).")]
        [Min(1f)] public float walkReferenceSpeedCmPerSec = 150f;

        [Tooltip("Ground speed at which the run clip's feet stay planted, at the prefab's own scale.")]
        [Min(1f)] public float runReferenceSpeedCmPerSec = 450f;

        [Header("Directional locomotion (walk and run are the forward loops)")]
        public LocomotionClip walkBackward = new LocomotionClip();
        public LocomotionClip walkLeft = new LocomotionClip();
        public LocomotionClip walkRight = new LocomotionClip();
        public LocomotionClip runBackward = new LocomotionClip();
        public LocomotionClip runLeft = new LocomotionClip();
        public LocomotionClip runRight = new LocomotionClip();

        [Header("Leg turn (humanoids)")]
        [Tooltip("How far the hips turn toward the movement while the spine keeps the chest on the facing; 0 = off.")]
        [Range(0f, 90f)] public float legTurnMaxDeg;

        [Tooltip("How fast the legs turn toward their target.")]
        [Min(0f)] public float legTurnDegPerSec;

        [Tooltip("Past 90 degrees off the facing the legs switch from walking forward to walking backward; they keep their side until the movement is this far past 90.")]
        [Range(0f, 45f)] public float legTurnHysteresisDeg;

        [Header("Blending")]
        [Min(0f)] public float crossFadeMs = 120f;

        [Tooltip("How long a phase's last frame is held while waiting for the next phase before going back to locomotion.")]
        [Min(0f)] public float phaseHoldMs = 250f;

        [Header("Actions")]
        [Tooltip("Played for an action id the list does not name.")]
        public ActionClipEntry defaultAction = new ActionClipEntry();

        public List<ActionClipEntry> actions = new List<ActionClipEntry>();

        [Header("Reactions")]
        public AnimationClip? flinch;
        public AnimationClip? stagger;
        public AnimationClip? death;

        /// <summary>
        /// The locomotion rules (art data, checked by the EditMode tests for every set). Reference speeds: the walk speed is
        /// positive and the run speed is above it, or equal when the set has no run clip; a directional slot with a clip has
        /// a reference of at least 1, and where a direction has both a walk and a run clip the run's is above the walk's; a
        /// slot without a clip is not reversed; a run slot needs a run clip. Leg turn: within its ranges, and a turn needs a
        /// rate of at least 1 degree per second. Null when they hold, else what is wrong.
        /// </summary>
        public string? LocomotionError()
        {
            if (walkReferenceSpeedCmPerSec < 1f)
                return $"{name}: walkReferenceSpeedCmPerSec {walkReferenceSpeedCmPerSec} must be at least 1";
            if (run != null && runReferenceSpeedCmPerSec <= walkReferenceSpeedCmPerSec)
                return $"{name}: runReferenceSpeedCmPerSec {runReferenceSpeedCmPerSec} must be above the walk's {walkReferenceSpeedCmPerSec}";
            if (run == null && runReferenceSpeedCmPerSec != walkReferenceSpeedCmPerSec)
                return $"{name}: without a run clip runReferenceSpeedCmPerSec must equal the walk's {walkReferenceSpeedCmPerSec}";

            for (int direction = 1; direction < LocomotionProfile.Directions; direction++)
            {
                var walkSlot = Slot(walk: true, direction);
                var runSlot = Slot(walk: false, direction);
                string side = ((LocomotionDirection)direction).ToString();
                foreach (var (slot, gait) in new[] { (walkSlot, "walk"), (runSlot, "run") })
                {
                    if (slot == null)
                        return $"{name}: the {gait} {side} slot is missing";
                    if (!slot.HasClip && slot.reverse)
                        return $"{name}: the {gait} {side} slot has no clip but is reversed";
                    if (slot.HasClip && slot.referenceSpeedCmPerSec < 1f)
                        return $"{name}: the {gait} {side} slot's referenceSpeedCmPerSec {slot.referenceSpeedCmPerSec} must be at least 1";
                }
                if (runSlot!.HasClip && run == null)
                    return $"{name}: the run {side} slot has a clip but the set has no run clip";
                if (walkSlot!.HasClip && runSlot.HasClip && runSlot.referenceSpeedCmPerSec <= walkSlot.referenceSpeedCmPerSec)
                    return $"{name}: the run {side} slot's reference {runSlot.referenceSpeedCmPerSec} must be above the walk's {walkSlot.referenceSpeedCmPerSec}";
            }

            if (legTurnMaxDeg < 0f || legTurnMaxDeg > 90f)
                return $"{name}: legTurnMaxDeg {legTurnMaxDeg} must be within 0..90";
            if (legTurnHysteresisDeg < 0f || legTurnHysteresisDeg > 45f)
                return $"{name}: legTurnHysteresisDeg {legTurnHysteresisDeg} must be within 0..45";
            if (legTurnMaxDeg > 0f && legTurnDegPerSec < 1f)
                return $"{name}: legTurnDegPerSec {legTurnDegPerSec} must be at least 1 when the legs turn";
            return null;
        }

        /// <summary>
        /// The directional slot of a gait (<see cref="LocomotionDirection"/>; the forward one is <see cref="walk"/> or
        /// <see cref="run"/> and has no slot: null).
        /// </summary>
        public LocomotionClip? Slot(bool walk, int direction) => (LocomotionDirection)direction switch
        {
            LocomotionDirection.Backward => walk ? walkBackward : runBackward,
            LocomotionDirection.Left => walk ? walkLeft : runLeft,
            LocomotionDirection.Right => walk ? walkRight : runRight,
            _ => null,
        };

        /// <summary>Every distinct clip the locomotion layer may play (idle, the forward loops and the directional slots).</summary>
        public List<AnimationClip> LocomotionClips()
        {
            var clips = new List<AnimationClip>();
            void Add(AnimationClip? clip)
            {
                if (clip != null && !clips.Contains(clip))
                    clips.Add(clip);
            }
            Add(idle);
            Add(walk);
            Add(run);
            for (int direction = 1; direction < LocomotionProfile.Directions; direction++)
            {
                Add(Slot(walk: true, direction)?.clip);
                Add(Slot(walk: false, direction)?.clip);
            }
            return clips;
        }

        /// <summary>The entry for <paramref name="actionId"/>, else the default one; null when neither has a clip.</summary>
        public ActionClipEntry? FindAction(string actionId)
        {
            foreach (var entry in actions)
            {
                if (entry != null && entry.clip != null && string.Equals(entry.actionId, actionId, StringComparison.Ordinal))
                    return entry;
            }
            return defaultAction != null && defaultAction.clip != null ? defaultAction : null;
        }

        public AnimationClip? Reaction(ReactionKind kind) => kind switch
        {
            ReactionKind.Flinched => flinch,
            ReactionKind.Staggered => stagger != null ? stagger : flinch,
            _ => death,
        };

        /// <summary>Every distinct clip the action layer may play (actions, channel loops, reactions).</summary>
        public List<AnimationClip> ActionLayerClips()
        {
            var clips = new List<AnimationClip>();
            void Add(AnimationClip? clip)
            {
                if (clip != null && !clips.Contains(clip))
                    clips.Add(clip);
            }
            Add(defaultAction?.clip);
            Add(defaultAction?.channelLoop);
            foreach (var entry in actions)
            {
                Add(entry?.clip);
                Add(entry?.channelLoop);
            }
            Add(flinch);
            Add(stagger);
            Add(death);
            return clips;
        }
    }
}
