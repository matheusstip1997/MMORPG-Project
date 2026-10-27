#nullable enable
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Outpost;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Overlays
{
    /// <summary>
    /// Spec 9.3 <c>npc_prompt</c> (WP-M2-4): "Press T to trade" at the NPC the interaction key talks to, like the pickup
    /// prompt (<see cref="PickupPromptOverlay"/>): placed by its <c>HudLayout</c> row around the NPC's projected point
    /// (<see cref="HudElementIds.WorldAnchored"/>); the text is the NPC kind's <c>UiStrings</c> row with the key name.
    /// </summary>
    public sealed class NpcPromptOverlay : WorldOverlay
    {
        private readonly NpcPromptViewModel _model;
        private readonly VisualElement _box;
        private readonly Label _label;
        private readonly Vector2 _offset;

        public NpcPromptOverlay(HudConfig config, VisualElement layer, NpcPromptViewModel model) : base(config, layer)
        {
            _model = model;
            _box = HudWidgets.Panel("hud-npc-prompt", config.Theme);
            _box.name = HudElementIds.NpcPrompt;
            HudWidgets.Border(_box, config.Theme.Accent);
            if (config.TryGetLayout(HudElementIds.NpcPrompt, out var row))
            {
                var rect = Game.UI.Presenters.UiLayout.Resolve(row.Anchor, row.Offset, row.Size, 0, 0);
                _offset = new Vector2(rect.x, rect.y);
                _box.style.width = rect.width;
                _box.style.height = rect.height;
            }
            _label = HudWidgets.Label("", "hud-pickup__text");
            _label.style.color = config.Theme.Text;
            _box.Add(_label);
            _box.style.display = DisplayStyle.None;
            layer.Add(_box);
            model.Changed += Refresh;
        }

        public bool IsShown { get; private set; }

        public uint NpcEntityId => IsShown ? _model.NpcEntityId : 0;

        public string Text => _label.text;

        public override void Dispose() => _model.Changed -= Refresh;

        private void Refresh()
        {
            if (_model.Visible)
                HudWidgets.SetText(_label, Config.Text.Get(_model.TextId, new TextArg("key", _model.KeyName)));
        }

        public override void Place(WorldClientFeed feed, double nowMs)
        {
            var views = feed.Views;
            var camera = feed.Camera;
            if (_model.Visible && views != null && camera != null && views.TryGetAnchor(_model.NpcEntityId, out var anchor)
                && TryProject(camera, anchor.position + Vector3.up * 2f, out var point))
            {
                Move(_box, point + _offset);
                _box.style.display = DisplayStyle.Flex;
                IsShown = true;
                return;
            }
            _box.style.display = DisplayStyle.None;
            IsShown = false;
        }
    }
}
