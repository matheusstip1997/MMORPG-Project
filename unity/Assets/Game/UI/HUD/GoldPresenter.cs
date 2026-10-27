#nullable enable
using System.Globalization;
using Game.UI.Config;
using Game.UI.ViewModels.Outpost;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>
    /// Spec 9.3 <c>gold</c> (WP-M2-4): the committed gold ("125 gold", <c>UiStrings</c> <c>hud.gold</c>) under the resource
    /// panel, placed by its <c>HudLayout</c> row; hidden until the server sent the balance.
    /// </summary>
    public sealed class GoldPresenter : HudPresenter
    {
        private readonly GoldViewModel _model;
        private VisualElement _box = null!;
        private Label _label = null!;

        public GoldPresenter(HudConfig config, HudView view, GoldViewModel model) : base(config, view, model)
        {
            _model = model;
        }

        public string Text => _label.text;

        public bool IsShown => _box.style.display == DisplayStyle.Flex;

        protected override void Build()
        {
            _box = View.Place(HudElementIds.Gold, HudWidgets.Panel("hud-gold", Theme));
            _label = HudWidgets.Label("", "hud-gold__text");
            _label.style.color = Theme.Accent;
            _box.Add(_label);
        }

        protected override void Refresh()
        {
            Show(_box, _model.Gold != null);
            if (_model.Gold is { } gold)
                HudWidgets.SetText(_label, Config.Text.Get(OutpostTextIds.Gold, new TextArg("gold", gold.ToString(CultureInfo.InvariantCulture))));
        }
    }
}
