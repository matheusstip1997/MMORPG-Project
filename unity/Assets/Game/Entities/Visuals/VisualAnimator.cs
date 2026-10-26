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
    /// (idle, walk, run) and an action mixer (one input per action-layer clip) under a root mixer. Every clip's time is
    /// set from the state each frame, so playback never depends on the clips' import loop settings and no
    /// AnimatorController asset is needed. Allocates only when built. Root motion stays off: the entity root alone places
    /// the visual, so humanoid clips must carry their body's vertical motion in the pose (root Y baked into the pose; the
    /// visual prefab builder sets it on the placeholder libraries).
    /// </summary>
    public sealed class VisualAnimator : IDisposable
    {
        private const int LocomotionInput = 0;
        private const int ActionInput = 1;

        private PlayableGraph _graph;
        private AnimationMixerPlayable _root;
        private AnimationMixerPlayable _locomotion;
        private AnimationMixerPlayable _actions;
        private AnimationClipPlayable _idle;
        private AnimationClipPlayable _walk;
        private AnimationClipPlayable _run;
        private readonly AnimationClipPlayable[] _actionClips;

        public VisualAnimator(Animator animator, AnimationClip idle, AnimationClip walk, AnimationClip run, IReadOnlyList<AnimationClip> actionClips)
        {
            animator.applyRootMotion = false;
            animator.runtimeAnimatorController = null;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            _graph = PlayableGraph.Create($"Visual {animator.name}");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var output = AnimationPlayableOutput.Create(_graph, "Visual", animator);

            _root = AnimationMixerPlayable.Create(_graph, 2);
            output.SetSourcePlayable(_root);

            _locomotion = AnimationMixerPlayable.Create(_graph, 3);
            _graph.Connect(_locomotion, 0, _root, LocomotionInput);
            _idle = Clip(idle, _locomotion, 0);
            _walk = Clip(walk, _locomotion, 1);
            _run = Clip(run, _locomotion, 2);

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

        public void Apply(VisualAnimationState state)
        {
            if (!_graph.IsValid())
                return;

            float locomotion = state.LocomotionWeight;
            _root.SetInputWeight(LocomotionInput, locomotion);
            _root.SetInputWeight(ActionInput, 1f - locomotion);

            var weights = state.Locomotion;
            _locomotion.SetInputWeight(0, weights.Idle);
            _locomotion.SetInputWeight(1, weights.Walk);
            _locomotion.SetInputWeight(2, weights.Run);
            _idle.SetTime(state.IdleTimeS);
            _walk.SetTime(state.WalkTimeS);
            _run.SetTime(state.RunTimeS);

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
        }

        public void Dispose()
        {
            if (_graph.IsValid())
                _graph.Destroy();
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
