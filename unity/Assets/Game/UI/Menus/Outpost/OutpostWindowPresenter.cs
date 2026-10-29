#nullable enable
using System.Collections.Generic;
using System.Globalization;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Outpost;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Outpost
{
    /// <summary>
    /// Spec 9.13 (M2) <c>outpost_window</c> (WP-M2-4): the outpost's sale window, placed by its <c>HudLayout</c> row. A list of
    /// the sellable stacks of the committed bag (name and count; sell-only and not stolen), the selected stack's count
    /// stepper (less, more, all), the server's quote ("20 for 196 gold, 10 to 9 each"), the sell button (only with a quote
    /// for the current selection), and the last sale. Text from <c>UiStrings</c>; redraws on the view model's
    /// <c>Changed</c> only; the server decides every price and sale.
    /// </summary>
    public sealed class OutpostWindowPresenter : HudPresenter
    {
        private readonly OutpostConfig _outpost;
        private readonly OutpostWindowViewModel _model;
        private readonly IOutpostWindowActions _actions;
        private readonly List<Button> _rows = new List<Button>();
        private VisualElement _window = null!;
        private VisualElement _list = null!;
        private Label _empty = null!;
        private Label _count = null!;
        private Label _quote = null!;
        private Label _result = null!;
        private Button _sell = null!;
        private bool _pointerInside;

        public OutpostWindowPresenter(HudConfig config, HudView view, OutpostConfig outpost, OutpostWindowViewModel model,
            IOutpostWindowActions actions) : base(config, view, model)
        {
            _outpost = outpost;
            _model = model;
            _actions = actions;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        /// <summary>The texts shown, for tests and QA checks.</summary>
        public string QuoteText => _quote.text;

        public string ResultText => _result.text;

        public string CountText => _count.text;

        public bool SellEnabled => _sell.enabledSelf;

        public int RowCount => _rows.Count;

        public Button Row(int index) => _rows[index];

        protected override void Build()
        {
            _window = View.Place(HudElementIds.OutpostWindow, HudWidgets.Panel("outpost-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            _window.Add(OutpostWidgets.Header(Config, Theme, OutpostTextIds.SellTitle, "outpost_close", () => _actions.Close()));
            // WP-M2-6: the outpost's quest board (the server refuses it at an outpost that offers no quests).
            _window.Add(OutpostWidgets.Button(Theme, "outpost_quests", Config.Text.Get(QuestTextIds.Open), () => _actions.OpenQuests()));
            _list = HudWidgets.Box("outpost-list");
            _list.pickingMode = PickingMode.Position;
            _window.Add(_list);
            _empty = HudWidgets.Label(Config.Text.Get(OutpostTextIds.Empty), "outpost-empty");
            _empty.style.color = Theme.Text;
            _window.Add(_empty);

            var stepper = HudWidgets.Box("outpost-stepper");
            stepper.style.flexDirection = FlexDirection.Row;
            stepper.style.alignItems = Align.Center;
            stepper.pickingMode = PickingMode.Position;
            stepper.Add(OutpostWidgets.Button(Theme, "outpost_less", Config.Text.Get(OutpostTextIds.Less), () => _actions.Step(-1)));
            _count = HudWidgets.Label("", "outpost-count");
            _count.style.color = Theme.Text;
            _count.style.minWidth = 48;
            _count.style.unityTextAlign = TextAnchor.MiddleCenter;
            stepper.Add(_count);
            stepper.Add(OutpostWidgets.Button(Theme, "outpost_more", Config.Text.Get(OutpostTextIds.More), () => _actions.Step(1)));
            stepper.Add(OutpostWidgets.Button(Theme, "outpost_all", Config.Text.Get(OutpostTextIds.AllUnits), () => _actions.SelectAll()));
            _window.Add(stepper);

            _quote = HudWidgets.Label("", "outpost-quote");
            _quote.style.color = Theme.Accent;
            _window.Add(_quote);
            _sell = OutpostWidgets.Button(Theme, "outpost_sell", Config.Text.Get(OutpostTextIds.Sell), () => _actions.Sell());
            _window.Add(_sell);
            _result = HudWidgets.Label("", "outpost-result");
            _result.style.color = Theme.Text;
            _window.Add(_result);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
                return;
            var stacks = _model.Stacks;
            while (_rows.Count < stacks.Count)
            {
                int index = _rows.Count;
                var row = OutpostWidgets.Button(Theme, "outpost_row_" + index, "", () => OnRow(index));
                row.style.unityTextAlign = TextAnchor.MiddleLeft;
                _rows.Add(row);
                _list.Add(row);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = i < stacks.Count;
                Show(_rows[i], used);
                if (!used)
                    continue;
                var stack = stacks[i];
                string name = _outpost.Inventory.Item(stack.ItemNetId) is { } item ? Config.Text.Get(item.Def.NameId) : "#" + stack.ItemNetId;
                _rows[i].text = Config.Text.Get(OutpostTextIds.Stack, new TextArg("name", name), new TextArg("count", Number(stack.Count)));
                HudWidgets.Border(_rows[i], stack.Slot == _model.SelectedSlot ? Theme.Accent : Theme.Border);
            }
            Show(_empty, stacks.Count == 0);

            bool selected = _model.SelectedSlot != OutpostWindowViewModel.None;
            HudWidgets.SetText(_count, selected ? Config.Text.Get(OutpostTextIds.Count, new TextArg("count", Number(_model.Count))) : "");
            HudWidgets.SetText(_quote, QuoteLine());
            _sell.SetEnabled(_model.Confirm() != null);
            HudWidgets.SetText(_result, _model.LastSold is { } sold
                ? Config.Text.Get(OutpostTextIds.Sold, new TextArg("count", Number(sold.Count)), new TextArg("gold", Number(sold.Gold)))
                : "");
        }

        private string QuoteLine()
        {
            if (_model.Quote == QuoteState.Pending)
                return Config.Text.Get(OutpostTextIds.QuotePending);
            if (_model.Quote != QuoteState.Ready || _model.LastQuote == null)
                return "";
            var quote = _model.LastQuote;
            return Config.Text.Get(OutpostTextIds.Quote, new TextArg("count", Number(quote.Count)), new TextArg("total", Number(quote.Total)),
                new TextArg("first", Number(quote.FirstUnit)), new TextArg("last", Number(quote.LastUnit)));
        }

        private void OnRow(int index)
        {
            if (index < _model.Stacks.Count)
                _actions.SelectStack(_model.Stacks[index].Slot);
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Small builders shared by the outpost and vendor windows (WP-M2-4).</summary>
    internal static class OutpostWidgets
    {
        public static Button Button(HudTheme theme, string name, string text, System.Action onClick)
        {
            var button = new Button(onClick) { name = name, text = text };
            button.style.color = theme.Text;
            button.style.backgroundColor = theme.Panel;
            HudWidgets.Border(button, theme.Border);
            return button;
        }

        /// <summary>A title and a close button (with a text label for screen readers, spec 9.8).</summary>
        public static VisualElement Header(HudConfig config, HudTheme theme, string titleId, string closeName, System.Action close)
        {
            var header = HudWidgets.Box("outpost-header");
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.pickingMode = PickingMode.Position;
            var title = HudWidgets.Label(config.Text.Get(titleId), "outpost-title");
            title.style.color = theme.Accent;
            header.Add(title);
            var button = Button(theme, closeName, config.Text.Get(OutpostTextIds.Close), close);
            button.tooltip = config.Text.Get(OutpostTextIds.CloseLabel);
            header.Add(button);
            return header;
        }
    }
}
