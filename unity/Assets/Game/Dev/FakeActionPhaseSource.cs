#if UNITY_EDITOR || MMORPG_DEV_LOGIN
#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Entities.Visuals;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Gameplay.Dev
{
    /// <summary>
    /// Stand-in for the action machine (backlog D2) in the dev scene: plays scripted actions and reactions on the local
    /// player's view through <see cref="IActionPresentation"/>, so animation can be checked before D2 lands. The
    /// timings are dev placeholders, not ActionDef values; the real source is an adapter over D2's action listener.
    /// Keys: 1 attack (startup, active, recovery), 2 channel (startup, channel, recovery), F flinch, G stagger,
    /// K death, L back to locomotion.
    /// </summary>
    public sealed class FakeActionPhaseSource : MonoBehaviour
    {
        private struct Step
        {
            public ActionPresentationPhase Phase;
            public int DurationMs;
        }

        [SerializeField] private EntityViews? views;
        [SerializeField] private string attackActionId = "dev.attack";
        [SerializeField] private string channelActionId = "dev.channel";
        [SerializeField] private int[] attackPhasesMs = { 300, 100, 400 };
        [SerializeField] private int[] channelPhasesMs = { 300, 1500, 300 };
        [SerializeField] private int flinchMs = 250;
        [SerializeField] private int staggerMs = 900;
        [SerializeField] private float tickMs = 50f;

        private readonly List<Step> _steps = new List<Step>();
        private string _actionId = string.Empty;
        private int _step = -1;
        private float _stepElapsedMs;
        private double _tick;

        public void Configure(EntityViews target) => views = target;

        private void Update()
        {
            _tick += Time.deltaTime * 1000.0 / tickMs;
            var target = views != null ? views.LocalPresentation : null;
            var keyboard = Keyboard.current;
            if (target == null || keyboard == null)
                return;

            if (keyboard.digit1Key.wasPressedThisFrame)
                Begin(attackActionId, ActionPresentationPhase.Active, attackPhasesMs);
            else if (keyboard.digit2Key.wasPressedThisFrame)
                Begin(channelActionId, ActionPresentationPhase.Channel, channelPhasesMs);
            else if (keyboard.fKey.wasPressedThisFrame)
                React(target, ReactionKind.Flinched, flinchMs);
            else if (keyboard.gKey.wasPressedThisFrame)
                React(target, ReactionKind.Staggered, staggerMs);
            else if (keyboard.kKey.wasPressedThisFrame)
                React(target, ReactionKind.Dead, 0);
            else if (keyboard.lKey.wasPressedThisFrame)
            {
                _step = -1;
                target.OnActionEnd();
            }

            if (_step < 0)
                return;
            _stepElapsedMs += Time.deltaTime * 1000f;
            if (_stepElapsedMs < _steps[_step].DurationMs)
                return;
            _stepElapsedMs -= _steps[_step].DurationMs;
            _step++;
            if (_step >= _steps.Count)
                _step = -1;
            else
                Present(target);
        }

        private void Begin(string actionId, ActionPresentationPhase middle, int[] phasesMs)
        {
            _steps.Clear();
            _steps.Add(new Step { Phase = ActionPresentationPhase.Startup, DurationMs = phasesMs[0] });
            _steps.Add(new Step { Phase = middle, DurationMs = phasesMs[1] });
            _steps.Add(new Step { Phase = ActionPresentationPhase.Recovery, DurationMs = phasesMs[2] });
            _actionId = actionId;
            _step = 0;
            _stepElapsedMs = 0f;
            views!.SyncLocalTick(_tick);
            Present(views.LocalPresentation!);
        }

        private void React(IActionPresentation target, ReactionKind kind, int durationMs)
        {
            // A flinch or stagger cuts the scripted action (R-3.3-07).
            _step = -1;
            views!.SyncLocalTick(_tick);
            target.OnReaction(kind, (uint)_tick, durationMs);
        }

        private void Present(IActionPresentation target)
        {
            var step = _steps[_step];
            target.OnActionPhase(_actionId, step.Phase, (uint)(_tick - _stepElapsedMs / tickMs), step.DurationMs);
        }
    }
}
#endif
