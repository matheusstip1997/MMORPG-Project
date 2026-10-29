#nullable enable
using System.Collections.Generic;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Character;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Simulation.Stats;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Character
{
    /// <summary>
    /// Spec 9.13 M2 <c>character_sheet</c> (WP-M2-11), placed by its <c>HudLayout</c> row: a header with the title and a close
    /// button, the class, "Level 12   Rank E", the exp of the level's need and the unspent skill points
    /// (<c>ProgressionState</c>), then every stat of <c>OwnStats</c> in the sections of <c>CharacterSheetStats</c>
    /// ("Atk  124", "Critical rate  5%", "HP regeneration  3/s"), in a scroll view. WP-M2-13 (R-3.4-08): the unspent free
    /// attribute points under the skill points, and on each attribute row the points placed on it ("24 (+5)") and a "+"
    /// button while points are unspent, off while a placement waits for its answer. Text from <c>UiStrings</c>; redraws on
    /// the view model's <c>Changed</c> only. While the pointer is on the sheet, the mouse buttons are not attack or parry.
    /// </summary>
    public sealed class CharacterSheetPresenter : HudPresenter
    {
        private readonly CharacterSheetConfig _sheet;
        private readonly CharacterSheetViewModel _model;
        private readonly System.Action _close;
        private readonly System.Action<int>? _place;
        private readonly Dictionary<string, Label> _values = new Dictionary<string, Label>(System.StringComparer.Ordinal);
        private readonly Button?[] _plus = new Button?[Attributes.Count];
        private readonly Label?[] _placed = new Label?[Attributes.Count];
        private VisualElement _window = null!;
        private Label _class = null!;
        private Label _level = null!;
        private Label _exp = null!;
        private Label _points = null!;
        private Label _attributePoints = null!;
        private Label _waiting = null!;
        private ScrollView _stats = null!;
        private bool _pointerInside;

        /// <param name="place">A plus button's action with the attribute (<c>Attributes.Str</c> ... <c>Int</c>); null: no buttons.</param>
        public CharacterSheetPresenter(HudConfig config, HudView view, CharacterSheetConfig sheet, CharacterSheetViewModel model,
            System.Action close, System.Action<int>? place = null) : base(config, view, model)
        {
            _sheet = sheet;
            _model = model;
            _close = close;
            _place = place;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        /// <summary>A stat's value text as shown ("124", "5%"), for tests and QA checks; empty for an unknown stat.</summary>
        public string ValueText(string statId) => _values.TryGetValue(statId, out var label) ? label.text : "";

        /// <summary>The value label of a stat (layout tests).</summary>
        public Label? ValueLabel(string statId) => _values.TryGetValue(statId, out var label) ? label : null;

        public string ClassText => _class.text;

        public string LevelText => _level.text;

        public string ExpText => _exp.text;

        public string PointsText => _points.text;

        /// <summary>The unspent attribute points line (WP-M2-13).</summary>
        public string AttributePointsText => _attributePoints.text;

        /// <summary>The plus button of attribute <paramref name="attribute"/> (tests and QA clicks through the pointer); null without one.</summary>
        public Button? PlusButton(int attribute) => attribute >= 0 && attribute < _plus.Length ? _plus[attribute] : null;

        /// <summary>The placed points text of an attribute row ("(+5)"; empty with none).</summary>
        public string PlacedText(int attribute) => attribute >= 0 && attribute < _placed.Length ? _placed[attribute]?.text ?? "" : "";

        public bool WaitingShown => _waiting.style.display == DisplayStyle.Flex;

        protected override void Build()
        {
            _window = View.Place(HudElementIds.CharacterSheet, HudWidgets.Panel("char-sheet", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            var header = HudWidgets.Box("char-sheet__header");
            var title = HudWidgets.Label(Config.Text.Get(CharacterSheetTextIds.Title), "char-sheet__title");
            title.style.color = Theme.Accent;
            header.Add(title);
            var close = new Button(_close) { name = "char_close", text = Config.Text.Get(CharacterSheetTextIds.Close) };
            close.tooltip = Config.Text.Get(CharacterSheetTextIds.CloseLabel); // spec 9.8: a text label for an icon-only control
            close.AddToClassList("inv-close");
            close.style.color = Theme.Text;
            close.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(close, Theme.Border);
            header.Add(close);
            _window.Add(header);

            _class = Line("char-sheet__class");
            _level = Line("char-sheet__line");
            _exp = Line("char-sheet__line");
            _points = Line("char-sheet__line");
            _attributePoints = Line("char-sheet__line");
            _attributePoints.name = "char_attribute_points";
            _waiting = Line("char-sheet__line");
            _waiting.text = Config.Text.Get(CharacterSheetTextIds.Waiting);

            _stats = new ScrollView(ScrollViewMode.Vertical) { name = "char_stats" };
            _stats.AddToClassList("char-sheet__stats");
            _stats.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _window.Add(_stats);
            CharacterSheetSection? section = null;
            foreach (var line in _sheet.Lines)
            {
                if (line.Section != section)
                {
                    section = line.Section;
                    var heading = HudWidgets.Label(Config.Text.Get(CharacterSheetTextIds.Section(line.Section)), "char-sheet__section");
                    heading.style.color = Theme.Accent;
                    _stats.Add(heading);
                }
                var row = HudWidgets.Box("char-sheet__row");
                row.name = "char_stat_" + line.StatId;
                row.Add(HudWidgets.Label(Config.Text.Get(UiStringCoverage.StatId(line.StatId)), "char-sheet__name"));
                var value = HudWidgets.Label("", "char-sheet__value");
                int attribute = System.Array.IndexOf(StatIds.Attributes, line.StatId);
                if (attribute >= 0 && _place != null)
                {
                    // R-3.4-08: the placed points and the plus button beside the attribute's value.
                    var right = HudWidgets.Box("char-sheet__attribute");
                    right.Add(value);
                    var placed = HudWidgets.Label("", "char-sheet__placed");
                    placed.name = "char_placed_" + line.StatId;
                    placed.style.color = Theme.Accent;
                    right.Add(placed);
                    _placed[attribute] = placed;
                    int index = attribute;
                    var plus = new Button(() => _place(index)) { name = "char_plus_" + line.StatId, text = Config.Text.Get(CharacterSheetTextIds.Plus) };
                    plus.tooltip = Config.Text.Get(CharacterSheetTextIds.PlusLabel); // spec 9.8: a text label for an icon-only control
                    plus.focusable = false;
                    plus.AddToClassList("char-sheet__plus");
                    plus.style.color = Theme.Text;
                    plus.style.backgroundColor = Theme.Panel;
                    HudWidgets.Border(plus, Theme.Border);
                    right.Add(plus);
                    _plus[attribute] = plus;
                    row.Add(right);
                }
                else
                {
                    row.Add(value);
                }
                _values[line.StatId] = value;
                _stats.Add(row);
            }
        }

        private Label Line(string className)
        {
            var label = HudWidgets.Label("", className);
            label.style.color = Theme.Text;
            _window.Add(label);
            return label;
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
                return;
            var text = Config.Text;
            HudWidgets.SetText(_class, _model.ClassId.Length > 0
                ? text.Get(CharacterSheetTextIds.Class, new TextArg("class", text.Get(UiStringCoverage.ClassName(_model.ClassId))))
                : "");
            HudWidgets.SetText(_level, _model.HasProgression
                ? text.Get(CharacterSheetTextIds.Level, new TextArg("level", NumberText.Of(_model.Level)),
                    new TextArg("rank", text.Get(UiStringCoverage.RankId(_model.Rank))))
                : "");
            HudWidgets.SetText(_exp, !_model.HasProgression ? ""
                : _model.ExpToNext <= 0 ? text.Get(CharacterSheetTextIds.ExpMax)
                : text.Get(CharacterSheetTextIds.Exp, new TextArg("exp", NumberText.Of(_model.Exp)),
                    new TextArg("need", NumberText.Of(_model.ExpToNext))));
            HudWidgets.SetText(_points, _model.HasProgression
                ? text.Get(CharacterSheetTextIds.Points, new TextArg("points", NumberText.Of(_model.SkillPoints)))
                : "");
            HudWidgets.SetText(_attributePoints, _model.HasProgression
                ? text.Get(CharacterSheetTextIds.AttributePoints, new TextArg("points", NumberText.Of(_model.AttributePoints)))
                : "");
            for (int attribute = 0; attribute < Attributes.Count; attribute++)
            {
                int placed = _model.Placed(attribute);
                if (_placed[attribute] is { } placedLabel)
                    HudWidgets.SetText(placedLabel, _model.HasProgression && placed > 0
                        ? text.Get(CharacterSheetTextIds.Placed, new TextArg("points", NumberText.Of(placed)))
                        : "");
                if (_plus[attribute] is { } plus)
                {
                    Show(plus, _model.ShowsPlus);
                    plus.SetEnabled(_model.CanPlace);
                }
            }
            Show(_waiting, !_model.HasStats);
            Show(_stats, _model.HasStats);
            foreach (var line in _sheet.Lines)
            {
                HudWidgets.SetText(_values[line.StatId], _model.HasStats
                    ? text.Get(CharacterSheetTextIds.Value(line.Unit), new TextArg("value", NumberText.Of(_model.Stat(line.StatId))))
                    : "");
            }
        }
    }
}
