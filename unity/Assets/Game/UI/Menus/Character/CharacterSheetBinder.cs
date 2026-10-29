#nullable enable
using System;
using System.Linq;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Character;
using Game.UI.ViewModels.Hud;
using Shared.Protocol;
using Shared.Simulation.Stats;
using UnityEngine.InputSystem;

namespace Game.UI.Menus.Character
{
    /// <summary>
    /// Connects the character sheet (spec 9.13 M2, WP-M2-11) to the world client: <see cref="WorldClientFeed.StatsReceived"/>
    /// (<c>OwnStats</c>), <see cref="WorldClientFeed.ProgressionReceived"/> and the class of the committed skill tree go into
    /// the <see cref="CharacterSheetViewModel"/>. WP-M2-13 (R-3.4-08): a plus button sends one free attribute point through
    /// <see cref="WorldClientFeed.AttributeRequests"/> (<see cref="Place"/>); the sheet waits for its answer
    /// (<see cref="WorldClientFeed.AttributeAnswered"/>) and shows a refusal as a toast; the new numbers come with the next
    /// <c>ProgressionState</c> and <c>OwnStats</c>. The toggle key is the <c>Hotkeys</c> row <c>toggleCharacter</c>.
    /// </summary>
    public sealed class CharacterSheetBinder : IDisposable
    {
        private readonly CharacterSheetConfig _config;
        private readonly CharacterSheetViewModel _sheet;
        private readonly string[] _statIds;
        private readonly InputAction? _toggle;
        private readonly ToastViewModel? _toast;
        private readonly int _toastMs;
        private readonly Func<double>? _now;
        private WorldClientFeed? _feed;

        /// <param name="readToggleKey">False in tests: no Input System action is made for the toggle key.</param>
        /// <param name="toast">Where a refused attribute point shows its reason (null: not shown).</param>
        public CharacterSheetBinder(CharacterSheetConfig config, CharacterSheetViewModel sheet, bool readToggleKey = true,
            ToastViewModel? toast = null, int toastMs = 0, Func<double>? nowMs = null)
        {
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            _config = config;
            _sheet = sheet;
            _statIds = config.Lines.Select(line => line.StatId).ToArray();
            if (readToggleKey && config.ToggleBinding.Length > 0)
            {
                _toggle = new InputAction("ToggleCharacter", InputActionType.Button, config.ToggleBinding);
                _toggle.Enable();
            }
        }

        public WorldClientFeed? Feed => _feed;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.StatsReceived += OnStats;
            feed.ProgressionReceived += OnProgression;
            feed.SkillTreeReceived += OnSkillTree;
            feed.AttributeAnswered += OnAttributeAnswer;
            feed.Disconnected += OnDisconnected;
            // They may have come before the HUD bound.
            if (feed.Stats is { HasValues: true } stats)
                OnStats(stats);
            if (feed.Progression != null)
                OnProgression(feed.Progression);
            if (feed.SkillTree != null)
                OnSkillTree(feed.SkillTree);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.StatsReceived -= OnStats;
            _feed.ProgressionReceived -= OnProgression;
            _feed.SkillTreeReceived -= OnSkillTree;
            _feed.AttributeAnswered -= OnAttributeAnswer;
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
                _sheet.Toggle();
        }

        public void Close() => _sheet.SetOpen(false);

        /// <summary>
        /// A plus button (R-3.4-08): one point on attribute <paramref name="attribute"/> (<c>Attributes.Str</c> ... <c>Int</c>),
        /// sent while no other placement waits; nothing while the sheet cannot place or nothing can send.
        /// </summary>
        public void Place(int attribute)
        {
            if (!_sheet.CanPlace || _feed?.AttributeRequests == null || attribute < 0 || attribute >= Attributes.Count)
                return;
            _sheet.SetPending(_feed.AttributeRequests.RequestAttributeSpend((byte)attribute));
        }

        private void OnAttributeAnswer(AttributeSpendResult result)
        {
            _sheet.OnAnswer(result.RequestId);
            if (result.Reason != AttributeReason.None)
                _toast?.Show(result.Reason.ToString(), false, _now?.Invoke() ?? 0, _toastMs);
        }

        private void OnStats(ClientStats stats)
        {
            if (stats.HasValues)
                _sheet.SetStats(_statIds, stats.Get);
        }

        private void OnProgression(ProgressionState state) => _sheet.SetProgression(state);

        private void OnSkillTree(ClientSkillTree tree)
        {
            if (tree.Skills != null)
                _sheet.SetClass(tree.Skills.ClassId);
        }

        private void OnDisconnected(string reasonCode) => _sheet.Clear();
    }
}
