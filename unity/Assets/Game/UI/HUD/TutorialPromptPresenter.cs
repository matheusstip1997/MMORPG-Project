#nullable enable
using System;
using System.Globalization;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Shared.Protocol;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>
    /// Spec 9.12 <c>tutorial_prompt</c> (spec 12.3, WP-M2-10): one contextual prompt at a time, top center under the boss bar. A
    /// scheduled prompt shows its <c>UiStrings</c> text (key names from the <see cref="TutorialBinder.KeyArgs"/>), a dismiss
    /// hint and a close button, with the accent border (news); the level gate's refusal shows the <c>RegionLevelTooLow</c>
    /// reason with the level it needs, with the refusal border and no dismiss (it ends when the push ends).
    /// </summary>
    public sealed class TutorialPromptPresenter : HudPresenter
    {
        private readonly TutorialPromptViewModel _model;
        private readonly TutorialBinder _binder;
        private VisualElement _panel = null!;
        private Label _text = null!;
        private Label _hint = null!;
        private Button _close = null!;
        private bool _pointerInside;

        public TutorialPromptPresenter(HudConfig config, HudView view, TutorialPromptViewModel model, TutorialBinder binder)
            : base(config, view, model)
        {
            _model = model;
            _binder = binder;
        }

        /// <summary>A click on the prompt's close button is not an attack; the rest of the panel lets clicks through.</summary>
        public bool CapturesPointer => _model.Visible && _pointerInside;

        /// <summary>The text shown now, empty while hidden (tests, the QA smoke).</summary>
        public string Text => _panel.style.display == DisplayStyle.Flex ? _text.text : "";

        public Button CloseButton => _close;

        protected override void Build()
        {
            // The panel lets clicks through to the world (it sits near the character); only its close button takes them.
            _panel = View.Place(HudElementIds.TutorialPrompt, HudWidgets.Panel("tutorial-prompt", Theme));
            _panel.style.flexDirection = FlexDirection.Row;
            _panel.style.alignItems = Align.Center;
            _panel.style.paddingLeft = 8;
            _panel.style.paddingRight = 4;

            _text = HudWidgets.Label("", "tutorial-prompt__text");
            _text.style.color = Theme.Text;
            _text.style.flexGrow = 1;
            _text.style.flexShrink = 1;
            _text.style.whiteSpace = WhiteSpace.Normal;
            _panel.Add(_text);
            _hint = HudWidgets.Label("", "tutorial-prompt__hint");
            _hint.style.color = Theme.Border;
            _hint.style.marginLeft = 6;
            _panel.Add(_hint);
            _close = new Button(() => _binder.Dismiss()) { name = "tutorial_close", text = Config.Text.Get(HudTextIds.TutorialClose) };
            _close.style.color = Theme.Text;
            _close.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(_close, Theme.Border);
            _close.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _close.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);
            _panel.Add(_close);
        }

        protected override void Refresh()
        {
            Show(_panel, _model.Visible);
            if (!_model.Visible)
            {
                _pointerInside = false;
                return;
            }
            if (_model.ShowsGate)
            {
                HudWidgets.SetText(_text, Config.Reasons.Text(nameof(ClientReasonCode.RegionLevelTooLow),
                    new TextArg("level", _model.GateLevel.ToString(CultureInfo.InvariantCulture))));
                HudWidgets.Border(_panel, Theme.Hp);
                Show(_hint, false);
                Show(_close, false);
                return;
            }
            HudWidgets.SetText(_text, Config.Text.Get(_model.PromptId, _binder.KeyArgs));
            HudWidgets.SetText(_hint, Config.Text.Get(HudTextIds.TutorialDismiss, new TextArg("key", _model.DismissKey)));
            HudWidgets.Border(_panel, Theme.Accent);
            Show(_hint, _model.DismissKey.Length > 0);
            Show(_close, true);
        }
    }
}
