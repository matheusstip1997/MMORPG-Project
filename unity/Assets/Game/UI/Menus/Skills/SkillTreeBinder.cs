#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Skills;
using Shared.Data.Tables;
using Shared.Protocol;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.UI.Menus.Skills
{
    /// <summary>
    /// Connects the skill tree window (spec 3.5, 9.13 pattern; WP-M2-5) to the world client: the committed skill tree and
    /// the progression of the <see cref="WorldClientFeed"/> go into the <see cref="SkillTreeWindowViewModel"/>; its buttons
    /// and hotbar drops go out through <see cref="WorldClientFeed.Requests"/> one at a time; a refusal shows its
    /// <c>ReasonCodeStrings</c> text in the HUD toast, a committed learn, advance or reset a <c>UiStrings</c> toast. The toggle
    /// key is the <c>Hotkeys</c> row <c>toggleSkillTree</c>. Display time is the caller's clock in ms.
    /// </summary>
    public sealed class SkillTreeBinder : ISkillTreeWindowActions, IDisposable
    {
        private readonly SkillTreeWindowViewModel _window;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private readonly Func<Vector2, int> _hotbarSlotAt;
        private readonly UiText _text;
        private readonly InputAction? _toggle;
        private WorldClientFeed? _feed;

        /// <param name="hotbarSlotAt">The hotbar slot under a panel position, -1 for none (the HUD's hotbar presenters).</param>
        /// <param name="readToggleKey">False in tests: no Input System action is made for the toggle key.</param>
        public SkillTreeBinder(SkillTreeConfig config, UiText text, SkillTreeWindowViewModel window, ToastViewModel toast, int toastMs,
            Func<double> nowMs, Func<Vector2, int> hotbarSlotAt, bool readToggleKey = true)
        {
            _text = text;
            _window = window;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            _hotbarSlotAt = hotbarSlotAt;
            if (readToggleKey && config.ToggleBinding.Length > 0)
            {
                _toggle = new InputAction("ToggleSkillTree", InputActionType.Button, config.ToggleBinding);
                _toggle.Enable();
            }
        }

        public WorldClientFeed? Feed => _feed;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.SkillTreeReceived += OnTree;
            feed.SkillTreeAnswered += OnAnswer;
            feed.ProgressionReceived += OnProgression;
            feed.Disconnected += OnDisconnected;
            if (feed.Progression != null) // they may have come before the HUD bound
                OnProgression(feed.Progression);
            if (feed.SkillTree != null)
                OnTree(feed.SkillTree);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.SkillTreeReceived -= OnTree;
            _feed.SkillTreeAnswered -= OnAnswer;
            _feed.ProgressionReceived -= OnProgression;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose()
        {
            Unbind();
            _toggle?.Dispose();
        }

        /// <summary>Once per frame: the toggle key (ignored while the chat input has focus).</summary>
        public void Frame(double nowMs)
        {
            if (_toggle != null && _toggle.WasPressedThisFrame() && !(_feed?.TextInputActive ?? false))
                _window.Toggle();
        }

        public void SelectTab(string classId) => _window.SelectTab(classId);

        public void Select(string nodeId) => _window.Select(nodeId);

        public void Primary() => Send(_window.Primary());

        public void Unbar() => Send(_window.Unbar());

        public void Respec() => Send(_window.Respec());

        public void Advance(string classId) => Send(_window.Advance(classId));

        public void Drop(string nodeId, Vector2 panelPosition)
        {
            int slot = _hotbarSlotAt(panelPosition);
            if (slot > 0)
                Send(_window.Drop(nodeId, slot));
        }

        public void Close() => _window.SetOpen(false);

        private void Send(SkillIntent? intent)
        {
            if (intent == null || _feed?.Requests == null)
                return;
            var request = intent.Value;
            uint requestId = _feed.Requests.RequestSkillTree(request.Op, request.Target, request.Slot);
            if (requestId != 0)
                _window.SetPending(requestId);
        }

        private void OnTree(ClientSkillTree tree)
        {
            if (tree.Skills != null)
                _window.SetSkills(tree.Skills);
        }

        private void OnProgression(ProgressionState state) => _window.SetProgress(state.Level, state.SkillPoints);

        private void OnAnswer(SkillTreeResult result)
        {
            _window.OnAnswer(result);
            double now = _now();
            if (result.Reason != SkillTreeReason.None)
            {
                _toast.Show(result.Reason.ToString(), false, now, _toastMs);
                return;
            }
            switch (result.Op)
            {
                case SkillTreeOp.Learn:
                case SkillTreeOp.Evolve:
                    _toast.ShowText(SkillTreeTextIds.ToastLearned, "name", NodeName(result.Target), now, _toastMs);
                    break;
                case SkillTreeOp.Advance:
                    _toast.ShowText(SkillTreeTextIds.ToastAdvanced, "class", ClassName(result.Target), now, _toastMs);
                    break;
                case SkillTreeOp.Respec:
                    _toast.ShowText(SkillTreeTextIds.ToastRespecced, "", "", now, _toastMs);
                    break;
            }
        }

        private string NodeName(string nodeId) => _text.Get(UiStringCoverage.SkillNodeName(nodeId));

        private string ClassName(string classId) => _text.Get(UiStringCoverage.ClassName(classId));

        private void OnDisconnected(string reasonCode) => _window.Clear();
    }
}
