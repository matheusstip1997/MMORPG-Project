#nullable enable
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>
    /// Spec 9.3 <c>exp_bar</c> and <c>exp_note</c> (WP-M2-3): the exp into the current level against what the level needs
    /// (full at the level cap), and above it, while the band modifier of R-3.6-03 is below x1.0, the multiplier note
    /// (R-3.6-04: the client shows the exp multiplier). WP-M2-11: the <c>exp_label</c> line in the note's box shows the
    /// level, rank and percent on the left (the note keeps the right), and hovering the line or the bar shows the exp of
    /// the level's need. Display only (spec 9.1).
    /// </summary>
    public sealed class ExpBarPresenter : HudPresenter
    {
        private readonly ProgressViewModel _model;
        private VisualElement _fill = null!;
        private Label _note = null!;
        private Label _label = null!;
        private VisualElement _tooltip = null!;
        private Label _tooltipText = null!;

        public ExpBarPresenter(HudConfig config, HudView view, ProgressViewModel model) : base(config, view, model)
        {
            _model = model;
        }

        protected override void Build()
        {
            var bar = View.Place(HudElementIds.ExpBar, HudWidgets.Bar("hud-exp", Theme.Exp, out _fill));
            // WP-M2-11: the level line under the note, so the note draws over it where both show.
            _label = View.Place(HudElementIds.ExpLabel, HudWidgets.Label("", "hud-exp__label"));
            _label.style.color = Theme.Text;
            _note = View.Place(HudElementIds.ExpNote, HudWidgets.Label("", "hud-exp__note"));
            _note.style.color = Theme.Text;
            BuildTooltip(bar);
        }

        // WP-M2-11: the exp of the level's need on hover of the line or the bar.
        private void BuildTooltip(VisualElement bar)
        {
            _tooltip = HudWidgets.Box("hud-tooltip");
            _tooltip.AddToClassList("hud-exp__tooltip");
            _tooltip.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(_tooltip, Theme.Border);
            _tooltipText = HudWidgets.Label("", "hud-tooltip__line");
            _tooltip.Add(_tooltipText);
            Show(_tooltip, false);
            _label.Add(_tooltip);
            foreach (var target in new[] { _label, bar })
            {
                target.pickingMode = PickingMode.Position;
                target.RegisterCallback<PointerEnterEvent>(_ => Show(_tooltip, _model.HasValues));
                target.RegisterCallback<PointerLeaveEvent>(_ => Show(_tooltip, false));
            }
        }

        protected override void Refresh()
        {
            HudWidgets.SetFill(_fill, _model.ExpFraction);
            Show(_note, _model.ShowNote);
            HudWidgets.SetText(_note, _model.ShowNote
                ? Config.Text.Get(HudTextIds.ExpNote, new TextArg("multiplier", _model.MultiplierText))
                : "");
            HudWidgets.SetText(_label, _model.HasValues
                ? Config.Text.Get(HudTextIds.ExpLabel, new TextArg("level", NumberText.Of(_model.Level)),
                    new TextArg("rank", Config.Text.Get(UiStringCoverage.RankId(_model.Rank))),
                    new TextArg("percent", NumberText.Of(_model.Percent)))
                : "");
            HudWidgets.SetText(_tooltipText, !_model.HasValues ? ""
                : _model.ExpToNext <= 0 ? Config.Text.Get(HudTextIds.ExpTooltipMax)
                : Config.Text.Get(HudTextIds.ExpTooltip, new TextArg("exp", NumberText.Of(_model.Exp)),
                    new TextArg("need", NumberText.Of(_model.ExpToNext))));
            if (!_model.HasValues)
                Show(_tooltip, false);
        }

        /// <summary>The bar's fill 0 to 1, for tests and QA checks.</summary>
        public float Fill => _model.ExpFraction;

        /// <summary>The note text while it shows; empty otherwise.</summary>
        public string NoteText => _note.style.display == DisplayStyle.Flex ? _note.text : "";

        /// <summary>The level, rank and percent line (WP-M2-11); empty without progression.</summary>
        public string LabelText => _label.text;

        /// <summary>The hover text while it shows; empty otherwise.</summary>
        public string TooltipText => _tooltip.style.display == DisplayStyle.Flex ? _tooltipText.text : "";

        /// <summary>The line the hover listens on (tests send pointer events to it).</summary>
        public VisualElement LabelElement => _label;
    }
}
