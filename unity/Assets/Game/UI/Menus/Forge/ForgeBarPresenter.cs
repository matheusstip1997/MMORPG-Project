#nullable enable
using System.Globalization;
using System.Text;
using Game.UI.Config;
using Game.UI.HUD;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Forge
{
    /// <summary>
    /// The forge timing bar (spec 5.3 M3 decision 12, 9.13; WP-M3-10) inside the forge window's session panel (mounted by the
    /// HUD root, not placed by its own layout row): a track with the target window, a marker the client's clock moves every
    /// frame (<see cref="Animate"/>), the Strike button (one press per round), the scores of the hits so far with their total,
    /// and how the server judged the last press. Shown while the session's job plays the bar; the server scores every press.
    /// </summary>
    public sealed class ForgeBarPresenter : HudPresenter
    {
        private readonly ForgeBarModel _model;
        private VisualElement _root = null!;
        private VisualElement _track = null!;
        private VisualElement _target = null!;
        private VisualElement _marker = null!;
        private Button _strike = null!;
        private Label _hits = null!;
        private Label _outcome = null!;

        public ForgeBarPresenter(HudConfig config, HudView view, ForgeBarModel model)
            : base(config, view, model)
        {
            _model = model;
        }

        /// <summary>The bar's root, added to the forge window's session panel.</summary>
        public VisualElement Root => _root;

        public bool IsShown => _root.style.display == DisplayStyle.Flex;

        public Button StrikeButton => _strike;

        public VisualElement Marker => _marker;

        public VisualElement Target => _target;

        public string HitsText => _hits.text;

        public string OutcomeText => _outcome.text;

        protected override void Build()
        {
            _root = HudWidgets.Box("forge-bar");
            _root.style.flexShrink = 0; // its height in a long window: only the forge list shrinks and scrolls (wave 1 QA F2)
            // Only Strike takes the pointer: while the window's content overflows, the bar must not swallow a click meant for
            // the window's own buttons underneath (Finish, Cancel).
            _root.pickingMode = PickingMode.Ignore;
            _root.style.marginTop = 6;
            var title = HudWidgets.Label(Config.Text.Get(ForgeBarTextIds.Title), "forge-bar__title");
            title.style.color = Theme.Text;
            title.style.whiteSpace = WhiteSpace.Normal;
            title.pickingMode = PickingMode.Ignore;
            _root.Add(title);

            _track = HudWidgets.Box("forge-bar__track");
            _track.style.height = 16;
            _track.pickingMode = PickingMode.Ignore;
            _track.style.marginTop = 2;
            _track.style.marginBottom = 2;
            _track.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(_track, Theme.Border);
            _target = HudWidgets.Box("forge-bar__target");
            _target.style.position = Position.Absolute;
            _target.style.top = 0;
            _target.style.bottom = 0;
            var window = Theme.Accent;
            window.a = 0.45f;
            _target.style.backgroundColor = window;
            _target.pickingMode = PickingMode.Ignore;
            _track.Add(_target);
            _marker = HudWidgets.Box("forge-bar__marker");
            _marker.style.position = Position.Absolute;
            _marker.style.top = 0;
            _marker.style.bottom = 0;
            _marker.style.width = 3;
            _marker.style.backgroundColor = Theme.Text;
            _marker.pickingMode = PickingMode.Ignore;
            _track.Add(_marker);
            _root.Add(_track);

            var row = HudWidgets.Box("forge-bar__row");
            row.style.flexDirection = FlexDirection.Row;
            row.pickingMode = PickingMode.Ignore;
            _strike = new Button(() => _model.Strike()) { name = "forge_bar_strike", text = Config.Text.Get(ForgeBarTextIds.Strike) };
            _strike.style.color = Theme.Text;
            _strike.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(_strike, Theme.Border);
            row.Add(_strike);
            _outcome = HudWidgets.Label("", "forge-bar__outcome");
            _outcome.style.color = Theme.Accent;
            _outcome.pickingMode = PickingMode.Ignore;
            row.Add(_outcome);
            _root.Add(row);
            _hits = HudWidgets.Label("", "forge-bar__hits");
            _hits.style.color = Theme.Text;
            _hits.style.whiteSpace = WhiteSpace.Normal;
            _hits.pickingMode = PickingMode.Ignore;
            _root.Add(_hits);
        }

        protected override void Refresh()
        {
            Show(_root, _model.Shown);
            if (!_model.Shown)
                return;
            bool open = _model.Round != 0;
            Show(_track, open);
            if (open)
            {
                var bar = _model.Bar;
                int left = bar.TargetPermille - bar.HalfWidthPermille;
                _target.style.left = Length.Percent(left / 10f);
                _target.style.width = Length.Percent(bar.HalfWidthPermille * 2 / 10f);
            }
            _strike.SetEnabled(_model.CanStrike);
            string outcome = ForgeBarTextIds.Of(_model.LastOutcome);
            HudWidgets.SetText(_outcome, outcome.Length == 0 ? ""
                : Config.Text.Get(outcome, new TextArg("score", Number(_model.LastScore))));
            var scores = new StringBuilder();
            foreach (var score in _model.Scores)
                scores.Append(scores.Length == 0 ? "" : ", ").Append(Number(score));
            HudWidgets.SetText(_hits, Config.Text.Get(ForgeBarTextIds.Hits, new TextArg("scores", scores.ToString())) + "   "
                + Config.Text.Get(ForgeBarTextIds.Total, new TextArg("score", Number(_model.Total)),
                    new TextArg("max", Number(_model.Session?.ChallengeMax ?? 0))));
        }

        /// <summary>Every frame: the marker follows the client's clock, the Strike button the round's time.</summary>
        public override void Animate(double nowMs)
        {
            if (!_model.Shown || _model.Round == 0)
                return;
            _marker.style.left = Length.Percent(_model.MarkerPermille / 10f);
            _strike.SetEnabled(_model.CanStrike);
            HudWidgets.Border(_track, _model.PreviewScore > 0 ? Theme.Accent : Theme.Border);
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
