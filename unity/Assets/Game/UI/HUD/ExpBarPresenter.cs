#nullable enable
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>
    /// Spec 9.3 <c>exp_bar</c> and <c>exp_note</c> (WP-M2-3): the exp into the current level against what the level needs
    /// (full at the level cap), and above it, while the band modifier of R-3.6-03 is below x1.0, the multiplier note
    /// (R-3.6-04: the client shows the exp multiplier). Display only (spec 9.1).
    /// </summary>
    public sealed class ExpBarPresenter : HudPresenter
    {
        private readonly ProgressViewModel _model;
        private VisualElement _fill = null!;
        private Label _note = null!;

        public ExpBarPresenter(HudConfig config, HudView view, ProgressViewModel model) : base(config, view, model)
        {
            _model = model;
        }

        protected override void Build()
        {
            View.Place(HudElementIds.ExpBar, HudWidgets.Bar("hud-exp", Theme.Exp, out _fill));
            _note = View.Place(HudElementIds.ExpNote, HudWidgets.Label("", "hud-exp__note"));
            _note.style.color = Theme.Text;
        }

        protected override void Refresh()
        {
            HudWidgets.SetFill(_fill, _model.ExpFraction);
            Show(_note, _model.ShowNote);
            HudWidgets.SetText(_note, _model.ShowNote
                ? Config.Text.Get(HudTextIds.ExpNote, new TextArg("multiplier", _model.MultiplierText))
                : "");
        }

        /// <summary>The bar's fill 0 to 1, for tests and QA checks.</summary>
        public float Fill => _model.ExpFraction;

        /// <summary>The note text while it shows; empty otherwise.</summary>
        public string NoteText => _note.style.display == DisplayStyle.Flex ? _note.text : "";
    }
}
