#nullable enable
using System.Collections.Generic;
using System.Globalization;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Forge;
using Shared.Data.Tables;
using Shared.Simulation.Crafting;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Forge
{
    /// <summary>
    /// Spec 9.13 <c>gem_panel</c> (spec 5.3 R-5.3-05; WP-M3-9): the gems panel beside the forge window while it is open at a
    /// jeweler's bench, placed by its <c>HudLayout</c> row. The gear with gem slots (worn first) with its gems used and free;
    /// for the chosen gear each socketed gem with its stat and a Remove button and the removal cost, then the cut gem a socket
    /// would use (Next for another), the tier's success chance and Socket. Text from <c>UiStrings</c> and
    /// <c>ReasonCodeStrings</c>; the server checks every request and rolls the socket.
    /// </summary>
    public sealed class GemPanelPresenter : HudPresenter
    {
        private readonly GemConfig _gemConfig;
        private readonly GemPanelViewModel _model;
        private readonly IGemPanelActions _actions;
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<(Label Text, Button Remove)> _socketed = new List<(Label, Button)>();
        private VisualElement _panel = null!;
        private VisualElement _list = null!;
        private Label _empty = null!;
        private VisualElement _socketedBox = null!;
        private Label _cost = null!;
        private Label _gem = null!;
        private Button _nextGem = null!;
        private Label _odds = null!;
        private Button _socket = null!;
        private Label _waiting = null!;
        private bool _pointerInside;

        public GemPanelPresenter(HudConfig config, HudView view, GemConfig gemConfig, GemPanelViewModel model, IGemPanelActions actions)
            : base(config, view, model)
        {
            _gemConfig = gemConfig;
            _model = model;
            _actions = actions;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _panel.style.display == DisplayStyle.Flex;

        public VisualElement Panel => _panel;

        public Button Row(int index) => _rows[index];

        public int RowCount => _rows.Count;

        public Button SocketButton => _socket;

        public Button RemoveButton(int index) => _socketed[index].Remove;

        public string GemText => _gem.text;

        public string OddsText => _odds.text;

        public string SocketedText(int index) => _socketed[index].Text.text;

        protected override void Build()
        {
            _panel = View.Place(HudElementIds.GemPanel, HudWidgets.Panel("gem-panel", Theme));
            _panel.pickingMode = PickingMode.Position;
            _panel.style.paddingLeft = 6;
            _panel.style.paddingRight = 6;
            _panel.style.paddingTop = 4;
            _panel.style.paddingBottom = 4;
            _panel.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _panel.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            var title = HudWidgets.Label(Config.Text.Get(GemTextIds.Tab), "gem-title");
            title.style.color = Theme.Accent;
            _panel.Add(title);

            var scroll = new ScrollView(ScrollViewMode.Vertical) { name = "gem-gear" };
            scroll.style.maxHeight = 150;
            _list = scroll;
            _panel.Add(scroll);
            _empty = Text(Config.Text.Get(GemTextIds.Empty), "gem-empty");
            _panel.Add(_empty);

            _socketedBox = HudWidgets.Box("gem-socketed");
            _socketedBox.pickingMode = PickingMode.Position;
            _socketedBox.style.marginTop = 6;
            _panel.Add(_socketedBox);
            _cost = Text("", "gem-cost");
            _panel.Add(_cost);

            var gemRow = HudWidgets.Box("gem-choice");
            gemRow.style.flexDirection = FlexDirection.Row;
            gemRow.style.marginTop = 6;
            gemRow.pickingMode = PickingMode.Position;
            _gem = Text("", "gem-choice__text");
            _nextGem = Button("gem_next", Config.Text.Get(GemTextIds.Next), () => _actions.NextGem());
            gemRow.Add(_gem);
            gemRow.Add(_nextGem);
            _panel.Add(gemRow);
            _odds = Text("", "gem-odds");
            _panel.Add(_odds);
            _socket = Button("gem_socket", Config.Text.Get(GemTextIds.Socket), () => _actions.Socket());
            _panel.Add(_socket);
            _waiting = Text(Config.Text.Get(GemTextIds.Waiting), "gem-waiting");
            _panel.Add(_waiting);
        }

        protected override void Refresh()
        {
            Show(_panel, _model.Open);
            if (!_model.Open)
                return;
            RefreshRows();
            RefreshSocketed();
            RefreshGem();
            Show(_waiting, _model.Pending != 0);
        }

        private void RefreshRows()
        {
            var targets = _model.Targets;
            while (_rows.Count < targets.Count)
            {
                int index = _rows.Count;
                var row = Button("gem_row_" + index, "", () => _actions.SelectGear(index));
                row.style.unityTextAlign = UnityEngine.TextAnchor.MiddleLeft;
                _rows.Add(row);
                _list.Add(row);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = i < targets.Count;
                Show(_rows[i], used);
                if (!used)
                    continue;
                var target = targets[i];
                _rows[i].text = Config.Text.Get(GemTextIds.Item, new TextArg("item", Config.Text.Get(target.Item.NameId)),
                    new TextArg("used", Number(target.Gems.Count)), new TextArg("slots", Number(target.Item.GemSlots)));
                HudWidgets.Border(_rows[i], i == _model.Selected ? Theme.Accent : Theme.Border);
                _rows[i].SetEnabled(_model.Pending == 0);
            }
            Show(_empty, targets.Count == 0);
        }

        private void RefreshSocketed()
        {
            var target = _model.Target;
            int count = target?.Gems.Count ?? 0;
            while (_socketed.Count < count)
            {
                int index = _socketed.Count;
                var line = HudWidgets.Box("gem-socketed__line");
                line.style.flexDirection = FlexDirection.Row;
                line.pickingMode = PickingMode.Position;
                var text = Text("", "gem-socketed__text");
                var remove = Button("gem_remove_" + index, Config.Text.Get(GemTextIds.Remove), () => _actions.Remove(index));
                line.Add(text);
                line.Add(remove);
                _socketedBox.Add(line);
                _socketed.Add((text, remove));
            }
            string cost = string.Empty;
            for (int i = 0; i < _socketed.Count; i++)
            {
                bool used = i < count;
                Show(_socketed[i].Text.parent, used);
                if (!used)
                    continue;
                string gemId = target!.Value.Gems[i];
                HudWidgets.SetText(_socketed[i].Text, GemLine(Config.Text, _gemConfig.Rules, gemId, GemTextIds.Socketed));
                _socketed[i].Remove.SetEnabled(_model.CanRemove(i));
                if (cost.Length == 0 && _gemConfig.Rules?.Gem(gemId) is { } gem && _gemConfig.Rules.Odds(gem.Tier) is { } odds
                    && _gemConfig.Rules.Item(odds.RemovalCostItemId) is { } costItem)
                    cost = Config.Text.Get(GemTextIds.Cost, new TextArg("count", Number(odds.RemovalCostCount)),
                        new TextArg("item", Config.Text.Get(costItem.NameId)));
            }
            Show(_cost, cost.Length > 0);
            HudWidgets.SetText(_cost, cost);
        }

        private void RefreshGem()
        {
            var gem = _model.Gem;
            var target = _model.Target;
            bool free = target is { } t && t.HasFreeSlot;
            HudWidgets.SetText(_gem, gem == null
                ? Config.Text.Get(GemTextIds.NoGem)
                : Config.Text.Get(GemTextIds.ChosenGem, new TextArg("gem", ItemName(gem.ItemNetId)), new TextArg("count", Number(gem.Count))));
            _nextGem.SetEnabled(_model.GemCount > 1 && _model.Pending == 0);
            int percent = gem != null && _gemConfig.Rules?.Item(gem.ItemNetId) is { } item && _gemConfig.Rules.Gem(item.Id) is { } def
                          && _gemConfig.Rules.Odds(def.Tier) is { } odds ? odds.SuccessPercent : -1;
            Show(_odds, percent >= 0 && free);
            if (percent >= 0)
                HudWidgets.SetText(_odds, Config.Text.Get(GemTextIds.Odds, new TextArg("percent", Number(percent))));
            Show(_socket, free);
            _socket.SetEnabled(_model.CanSocket);
        }

        /// <summary>
        /// "Ruby: Attack +4": a socketed gem with its <c>GemDef</c> stat, by <paramref name="textId"/> (the panel's line or the
        /// tooltip's, <see cref="GemTextIds.TooltipGem"/>). Shared with the inventory tooltip.
        /// </summary>
        public static string GemLine(UiText text, GemRules? rules, string gemId, string textId)
        {
            var item = rules?.Item(gemId);
            string name = item != null ? text.Get(item.NameId) : gemId;
            string stat = rules?.Gem(gemId) is { } gem ? StatText(text, gem) : string.Empty;
            return text.Get(textId, new TextArg("gem", name), new TextArg("stat", stat));
        }

        private static string StatText(UiText text, GemDef gem)
        {
            string name = text.Get(GemConfig.StatNameId(gem));
            return gem.Percent != 0
                ? text.Get(InventoryTextIds.TooltipStatPercent, new TextArg("stat", name), new TextArg("value", Signed(gem.Percent)))
                : text.Get(InventoryTextIds.TooltipStat, new TextArg("stat", name), new TextArg("value", Signed(gem.Flat)));
        }

        private string ItemName(int netId) => _gemConfig.Rules?.Item(netId) is { } item ? Config.Text.Get(item.NameId) : Number(netId);

        private Label Text(string text, string className)
        {
            var label = HudWidgets.Label(text, className);
            label.style.color = Theme.Text;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private Button Button(string name, string text, System.Action onClick)
        {
            var button = new Button(onClick) { name = name, text = text };
            button.style.color = Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(button, Theme.Border);
            return button;
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Signed(long value) => value.ToString("+0;-0;0", CultureInfo.InvariantCulture);
    }
}
