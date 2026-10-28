#nullable enable
using System;
using System.Linq;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Character;
using Shared.Protocol;
using UnityEngine.InputSystem;

namespace Game.UI.Menus.Character
{
    /// <summary>
    /// Connects the character sheet (spec 9.13 M2, WP-M2-11) to the world client: <see cref="WorldClientFeed.StatsReceived"/>
    /// (<c>OwnStats</c>), <see cref="WorldClientFeed.ProgressionReceived"/> and the class of the committed skill tree go into
    /// the <see cref="CharacterSheetViewModel"/>. The sheet sends nothing. The toggle key is the <c>Hotkeys</c> row
    /// <c>toggleCharacter</c>.
    /// </summary>
    public sealed class CharacterSheetBinder : IDisposable
    {
        private readonly CharacterSheetConfig _config;
        private readonly CharacterSheetViewModel _sheet;
        private readonly string[] _statIds;
        private readonly InputAction? _toggle;
        private WorldClientFeed? _feed;

        /// <param name="readToggleKey">False in tests: no Input System action is made for the toggle key.</param>
        public CharacterSheetBinder(CharacterSheetConfig config, CharacterSheetViewModel sheet, bool readToggleKey = true)
        {
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
