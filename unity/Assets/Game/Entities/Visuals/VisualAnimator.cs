#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Game.Gameplay.Entities.Visuals
{
    /// <summary>
    /// Applies a <see cref="VisualAnimationState"/> to an Animator through a manual Playables graph: a locomotion mixer
    /// (idle and the eight directional walk and run loops, <see cref="LocomotionProfile"/>) and an action mixer (one input
    /// per action-layer clip) under a root mixer. Every clip's time is set from the state each frame, so playback never
    /// depends on the clips' import loop settings and no AnimatorController asset is needed. Allocates only when built.
    /// Root motion stays off: the entity root alone places the visual, so humanoid clips must carry their body's vertical
    /// motion in the pose (root Y baked into the pose; the visual prefab builder sets it on the placeholder libraries).
    /// On a humanoid the state's leg turn is written to the bones right after the graph evaluates: the hips turn about
    /// the body's up axis and the spine bones turn back by the same angle in equal shares, so the chest keeps the facing.
    /// The next evaluation rewrites every humanoid bone, so nothing accumulates.
    /// </summary>
    public sealed class VisualAnimator : IDisposable
    {
        private const int LocomotionInput = 0;
        private const int ActionInput = 1;

        // Idle is the locomotion mixer's first input, the directional slots follow in LocomotionProfile.SlotIndex order.
        private const int FirstSlotInput = 1;

        private static readonly HumanBodyBones[] SpineChain = { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest };

        private PlayableGraph _graph;
        private AnimationMixerPlayable _root;
        private AnimationMixerPlayable _locomotion;
        private AnimationMixerPlayable _actions;
        private AnimationClipPlayable _idle;
        private readonly AnimationClipPlayable[] _slots = new AnimationClipPlayable[LocomotionProfile.Slots];
        private readonly AnimationClipPlayable[] _actionClips;
        private readonly Transform _body;
        private readonly Transform? _hips;
        private readonly Transform[] _spine;

        /// <param name="slotClips">The clip of each directional slot, <see cref="LocomotionProfile.Slots"/> entries in <see cref="LocomotionProfile.SlotIndex"/> order.</param>
        public VisualAnimator(Animator animator, AnimationClip idle, IReadOnlyList<AnimationClip> slotClips, IReadOnlyList<AnimationClip> actionClips)
        {
            if (slotClips.Count != LocomotionProfile.Slots)
                throw new ArgumentException($"expected {LocomotionProfile.Slots} locomotion slots, got {slotClips.Count}", nameof(slotClips));
            animator.applyRootMotion = false;
            animator.runtimeAnimatorController = null;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            _body = animator.transform;
            _spine = Array.Empty<Transform>();
            if (animator.isHuman)
            {
                _hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                var spine = new List<Transform>();
                foreach (var bone in SpineChain)
                {
                    var transform = animator.GetBoneTransform(bone);
                    if (transform != null)
                        spine.Add(transform);
                }
                _spine = spine.ToArray();
            }

            _graph = PlayableGraph.Create($"Visual {animator.name}");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var output = AnimationPlayableOutput.Create(_graph, "Visual", animator);

            _root = AnimationMixerPlayable.Create(_graph, 2);
            output.SetSourcePlayable(_root);

            _locomotion = AnimationMixerPlayable.Create(_graph, FirstSlotInput + LocomotionProfile.Slots);
            _graph.Connect(_locomotion, 0, _root, LocomotionInput);
            _idle = Clip(idle, _locomotion, 0);
            for (int i = 0; i < LocomotionProfile.Slots; i++)
                _slots[i] = Clip(slotClips[i], _locomotion, FirstSlotInput + i);

            _actions = AnimationMixerPlayable.Create(_graph, Math.Max(1, actionClips.Count));
            _graph.Connect(_actions, 0, _root, ActionInput);
            _actionClips = new AnimationClipPlayable[actionClips.Count];
            for (int i = 0; i < actionClips.Count; i++)
                _actionClips[i] = Clip(actionClips[i], _actions, i);

            _root.SetInputWeight(LocomotionInput, 1f);
            _root.SetInputWeight(ActionInput, 0f);
            output.SetWeight(1f);
        }

        public bool IsValid => _graph.IsValid();

        /// <summary>True when the body can show a leg turn (a humanoid with hips and at least one spine bone).</summary>
        public bool CanTurnLegs => _hips != null && _spine.Length > 0;

        public void Apply(VisualAnimationState state)
        {
            if (!_graph.IsValid())
                return;

            float locomotion = state.LocomotionWeight;
            _root.SetInputWeight(LocomotionInput, locomotion);
            _root.SetInputWeight(ActionInput, 1f - locomotion);

            _locomotion.SetInputWeight(0, state.Locomotion.Idle);
            _idle.SetTime(state.IdleTimeS);
            for (int i = 0; i < _slots.Length; i++)
            {
                _locomotion.SetInputWeight(FirstSlotInput + i, state.SlotWeight(i));
                _slots[i].SetTime(state.SlotTimeS(i));
            }

            for (int i = 0; i < _actionClips.Length; i++)
                _actions.SetInputWeight(i, 0f);
            int current = state.ActionClip;
            int previous = state.PreviousClip;
            if (previous >= 0 && previous < _actionClips.Length && previous != current)
            {
                _actions.SetInputWeight(previous, 1f - state.ActionBlend);
                _actionClips[previous].SetTime(state.PreviousTimeS);
            }
            if (current >= 0 && current < _actionClips.Length)
            {
                _actions.SetInputWeight(current, previous == current || previous < 0 ? 1f : state.ActionBlend);
                _actionClips[current].SetTime(state.ActionTimeS);
            }

            _graph.Evaluate(0f);
            TurnLegs(state.LegTurnDeg);
        }

        public void Dispose()
        {
            if (_graph.IsValid())
                _graph.Destroy();
        }

        private void TurnLegs(float degrees)
        {
            if (degrees == 0f || !CanTurnLegs)
                return;
            var up = _body.up;
            _hips!.rotation = Quaternion.AngleAxis(degrees, up) * _hips.rotation;
            var back = Quaternion.AngleAxis(-degrees / _spine.Length, up);
            foreach (var bone in _spine)
                bone.rotation = back * bone.rotation;
        }

        private AnimationClipPlayable Clip(AnimationClip clip, AnimationMixerPlayable mixer, int input)
        {
            var playable = AnimationClipPlayable.Create(_graph, clip);
            playable.SetApplyFootIK(false);
            playable.SetSpeed(0);
            _graph.Connect(playable, 0, mixer, input);
            return playable;
        }
    }
}
