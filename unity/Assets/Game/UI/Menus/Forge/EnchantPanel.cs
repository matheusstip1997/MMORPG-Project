#nullable enable
using System.Collections.Generic;
using System.Globalization;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Forge;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Forge
{
    /// <summary>What the player does in the enchant tab; the binder turns it into requests (WP-M3-8).</summary>
    public interface IEnchantPanelActions
    {
        void ChooseEnchant(int row);

        void Enchant();

        void ShowEnchantTab(bool active);
    }

    /// <summary>
    /// The enchant tab of the station window (spec 5.3 R-5.3-04, 9.13; WP-M3-8), drawn inside <c>forge_window</c> by
    /// <see cref="ForgeWindowPresenter"/> when the tab is chosen: the items of the character's jobs that enchant at this station
    /// with "+N", the stones a try takes and those held, the table's odds of the chosen item's level, and the Enchant button.
    /// Text from <c>UiStrings</c>; the outcome comes from the server after its save.
    /// </summary>
    public sealed class EnchantPanel
    {
        private readonly UiText _text;
        private readonly HudTheme _theme;
        private readonly IEnchantPanelActions _actions;
        private readonly List<Button> _rows = new List<Button>();
        private VisualElement _root = null!;
        private VisualElement _list = null!;
        private Label _empty = null!;
        private Label _stones = null!;
        private Label _odds = null!;
        private Button _enchant = null!;
        private Label _waiting = null!;

        public EnchantPanel(UiText text, HudTheme theme, EnchantPanelViewModel model, IEnchantPanelActions actions)
        {
            _text = text;
            _theme = theme;
            Model = model;
            _actions = actions;
        }

        public EnchantPanelViewModel Model { get; }

        public IEnchantPanelActions Actions => _actions;

        public VisualElement Root => _root;

        public int RowCount => _rows.Count;

        public Button Row(int index) => _rows[index];

        public Button EnchantButton => _enchant;

        public string StonesText => _stones.text;

        public string OddsText => _odds.text;

        public bool IsShown => _root.style.display == DisplayStyle.Flex;

        public VisualElement Build()
        {
            _root = HudWidgets.Box("enchant-panel");
            _root.pickingMode = PickingMode.Position;
            _root.style.marginTop = 4;
            _list = HudWidgets.Box("enchant-list");
            _list.pickingMode = PickingMode.Position;
            _root.Add(_list);
            _empty = Text(_text.Get(EnchantTextIds.Empty), "enchant-empty");
            _root.Add(_empty);
            _stones = Text("", "enchant-stones");
            _root.Add(_stones);
            _odds = Text("", "enchant-odds");
            _root.Add(_odds);
            _enchant = new Button(() => _actions.Enchant()) { name = "enchant_button", text = _text.Get(EnchantTextIds.Button) };
            Style(_enchant);
            _root.Add(_enchant);
            _waiting = Text(_text.Get(EnchantTextIds.Waiting), "enchant-waiting");
            _root.Add(_waiting);
            return _root;
        }

        public void Refresh(bool shown)
        {
            Show(_root, shown);
            if (!shown)
                return;
            var candidates = Model.Candidates;
            while (_rows.Count < candidates.Count)
            {
                int index = _rows.Count;
                var row = new Button(() => _actions.ChooseEnchant(index)) { name = "enchant_row_" + index };
                Style(row);
                row.style.unityTextAlign = UnityEngine.TextAnchor.MiddleLeft;
                _rows.Add(row);
                _list.Add(row);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = i < candidates.Count;
                Show(_rows[i], used);
                if (!used)
                    continue;
                var candidate = candidates[i];
                _rows[i].text = _text.Get(candidate.Maxed ? EnchantTextIds.ItemMaxed : EnchantTextIds.Item,
                    new TextArg("name", _text.Get(candidate.Item.NameId)), new TextArg("level", Number(candidate.Level)));
                HudWidgets.Border(_rows[i], i == Model.Selected ? _theme.Accent : _theme.Border);
            }
            Show(_empty, candidates.Count == 0);
            var chosen = Model.Chosen;
            Show(_stones, chosen != null);
            Show(_odds, Model.Odds != null);
            Show(_enchant, chosen != null);
            if (chosen is { } item)
                HudWidgets.SetText(_stones, _text.Get(EnchantTextIds.Stones, new TextArg("stone", _text.Get(item.Stone.NameId)),
                    new TextArg("held", Number(Model.StonesHeld)), new TextArg("count", Number(item.StoneCount))));
            if (Model.Odds is { } odds)
                HudWidgets.SetText(_odds, _text.Get(EnchantTextIds.Odds, new TextArg("plus", Number(odds.PlusPercent)),
                    new TextArg("same", Number(odds.SamePercent)), new TextArg("minus", Number(odds.MinusPercent))));
            _enchant.SetEnabled(Model.CanEnchant);
            Show(_waiting, Model.PendingRequest != 0);
        }

        private Label Text(string text, string className)
        {
            var label = HudWidgets.Label(text, className);
            label.style.color = _theme.Text;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private void Style(Button button)
        {
            button.style.color = _theme.Text;
            button.style.backgroundColor = _theme.Panel;
            HudWidgets.Border(button, _theme.Border);
        }

        private static void Show(VisualElement element, bool visible) =>
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
