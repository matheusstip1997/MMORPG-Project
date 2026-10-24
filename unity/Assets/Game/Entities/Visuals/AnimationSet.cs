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

        [Tooltip("How long the effect stays, in ms, unless the action has a ground area (then the area's durationMs).")]
        [Min(0f)] public float vfxLifetimeMs = 600f;
    }

    /// <summary>
    /// The clips of one kind of body (art data, not gameplay: replacing the art means a new set, no code change).
    /// Reference speeds are the ground speeds at which the walk and run clips' feet match the ground; timings of actions
    /// always come from <c>ActionDef</c> through <see cref="IActionPresentation"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "MMORPG/Visuals/Animation Set", fileName = "AnimationSet")]
    public sealed class AnimationSet : ScriptableObject
    {
        [Header("Locomotion")]
        public AnimationClip? idle;
        public AnimationClip? walk;

        [Tooltip("Optional; without it the walk clip is played faster.")]
        public AnimationClip? run;

        [Min(1f)] public float walkReferenceSpeedCmPerSec = 150f;
        [Min(1f)] public float runReferenceSpeedCmPerSec = 450f;

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
