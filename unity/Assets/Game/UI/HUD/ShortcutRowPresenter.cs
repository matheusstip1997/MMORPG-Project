#nullable enable
using System.Collections.Generic;
using Game.UI.Config;
using Game.UI.ViewModels;
using Game.UI.ViewModels.Hud;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>
    /// Spec 9.3 <c>shortcut_row</c> (WP-M2-11), bottom right: the arrow (<c>&gt;</c> expanded, <c>&lt;</c> collapsed; it hides
    /// every button and only itself remains), then one button per <c>HudLayout.buttons</c> id whose window exists ("INV",
    /// "SK", ... from <c>UiStrings</c> <c>hud.shortcut.&lt;id&gt;</c>). A click toggles that window; an open window's button
    /// has the accent border. Hovering a button shows its name and key ("Inventory (I)") above the row, the text label of
    /// spec 9.8. While the pointer is on the row, the mouse buttons are not attack or parry.
    /// </summary>
    public sealed class ShortcutRowPresenter : HudPresenter
    {
        private readonly ShortcutRowViewModel _model;
        private readonly IReadOnlyDictionary<string, ShortcutTarget> _targets;
        private readonly List<Button> _buttons = new List<Button>();
        private readonly List<ShortcutTarget> _buttonTargets = new List<ShortcutTarget>();
        private Button _arrow = null!;
        private VisualElement _tooltip = null!;
        private Label _tooltipText = null!;
        private int _pointerInside;

        public ShortcutRowPresenter(HudConfig config, HudView view, ShortcutRowViewModel model,
            IReadOnlyDictionary<string, ShortcutTarget> targets)
            : base(config, view, Sources(model, targets))
        {
            _model = model;
            _targets = targets;
        }

        public bool CapturesPointer => _pointerInside > 0;

        /// <summary>The arrow (tests and the QA smoke click it).</summary>
        public Button Arrow => _arrow;

        /// <summary>The button of a shortcut id, or null when the row has none (no window yet, or not in the layout row).</summary>
        public Button? ButtonFor(string buttonId) => _buttons.Find(b => b.name == ElementName(buttonId));

        /// <summary>The visible buttons' labels, left to right (tests).</summary>
        public string[] VisibleLabels() =>
            _buttons.FindAll(b => b.style.display == DisplayStyle.Flex).ConvertAll(b => b.text).ToArray();

        public string ArrowText => _arrow.text;

        /// <summary>The hover text while it shows; empty otherwise.</summary>
        public string TooltipText => _tooltip.style.display == DisplayStyle.Flex ? _tooltipText.text : "";

        public static string ElementName(string buttonId) => "shortcut_" + buttonId;

        protected override void Build()
        {
            var row = View.Place(HudElementIds.ShortcutRow, HudWidgets.Box("hud-shortcuts"));
            _tooltip = HudWidgets.Box("hud-tooltip");
            _tooltip.AddToClassList("hud-shortcut__tooltip");
            _tooltip.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(_tooltip, Theme.Border);
            _tooltipText = HudWidgets.Label("", "hud-tooltip__line");
            _tooltip.Add(_tooltipText);
            Show(_tooltip, false);
            row.Add(_tooltip);

            _arrow = NewButton("shortcut_arrow", "", _model.ToggleCollapsed, Config.Text.Get(HudTextIds.ShortcutArrowName));
            Show(_arrow, _model.Collapsible);
            row.Add(_arrow);
            foreach (string id in _model.Buttons)
            {
                if (!_targets.TryGetValue(id, out var target))
                    continue; // a button of the row whose window this client does not build
                var button = NewButton(ElementName(id), Config.Text.Get(HudTextIds.ShortcutLabel(id)), target.Toggle,
                    Config.Text.Get(HudTextIds.ShortcutName(id), new TextArg("key", target.KeyName)));
                _buttons.Add(button);
                _buttonTargets.Add(target);
                row.Add(button);
            }
        }

        private Button NewButton(string name, string text, System.Action onClick, string hoverText)
        {
            var button = new Button(onClick) { name = name, text = text };
            button.AddToClassList("hud-text");
            button.AddToClassList("hud-shortcut");
            button.style.color = Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(button, Theme.Border);
            button.RegisterCallback<PointerEnterEvent>(_ =>
            {
                _pointerInside++;
                HudWidgets.SetText(_tooltipText, hoverText);
                Show(_tooltip, true);
            });
            button.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                _pointerInside = System.Math.Max(0, _pointerInside - 1);
                if (_pointerInside == 0)
                    Show(_tooltip, false);
            });
            return button;
        }

        protected override void Refresh()
        {
            string arrow = Config.Text.Get(_model.Collapsed ? HudTextIds.ShortcutExpand : HudTextIds.ShortcutCollapse);
            if (_arrow.text != arrow)
                _arrow.text = arrow;
            for (int i = 0; i < _buttons.Count; i++)
            {
                Show(_buttons[i], !_model.Collapsed);
                HudWidgets.Border(_buttons[i], _buttonTargets[i].IsOpen() ? Theme.Accent : Theme.Border);
            }
        }

        private static ViewModel[] Sources(ShortcutRowViewModel model, IReadOnlyDictionary<string, ShortcutTarget> targets)
        {
            var sources = new List<ViewModel> { model };
            foreach (var target in targets.Values)
                sources.Add(target.Window);
            return sources.ToArray();
        }
    }
}
