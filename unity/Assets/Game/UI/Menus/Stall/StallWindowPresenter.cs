#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Stall;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Stall
{
    /// <summary>
    /// Spec 9.13 <c>stall_window</c> (spec 4.5; M4 decision 7; WP-M4-8), placed by its <c>HudLayout</c> row. The header with the
    /// title (a buyer: the owner's display name; the owner: "Your stall"; a free place: "Open a stall") and close; the time to the
    /// stall's close and its count of the cap; one row per listing (the item, the units left, the unit price) with Choose for a buyer
    /// or Take back for the owner; for a buyer, the chosen row's count (within its units), the total and Buy; for the owner and at
    /// a free place, the Normal bag's stacks to pick from, the count and unit price fields, and Open stall or Add; Close stall for
    /// the owner. Buttons are off while a request waits for its answer. Text from <c>UiStrings</c>, rich text off for anything the
    /// server filled in; the server decides everything.
    /// </summary>
    public sealed class StallWindowPresenter : HudPresenter
    {
        private const long MsPerHour = 3_600_000L;

        private readonly StallConfig _stall;
        private readonly StallWindowViewModel _model;
        private readonly IStallWindowActions _actions;
        private readonly Func<long> _utcNowMs;
        private readonly List<VisualElement> _rows = new List<VisualElement>();
        private readonly List<long> _rowListings = new List<long>();
        private readonly List<Button> _choices = new List<Button>();
        private VisualElement _window = null!;
        private Label _title = null!;
        private Label _info = null!;
        private Label _empty = null!;
        private ScrollView _list = null!;
        private VisualElement _listing = null!;
        private Label _pickHint = null!;
        private ScrollView _choiceList = null!;
        private TextField _count = null!;
        private TextField _price = null!;
        private int _textRevision; // the model's TextRevision last written into the count field
        private bool _countFocused; // the fields' focus, from their focus events (as the auction window keeps it)
        private bool _priceFocused;
        private Button _submit = null!;
        private Button _closeStall = null!;
        private VisualElement _buyRow = null!;
        private TextField _buyCount = null!;
        private Label _total = null!;
        private Button _buy = null!;
        private bool _buyFocused;
        private long _shownBuySelected; // the chosen listing the count field last showed
        private bool _pointerInside;

        /// <param name="utcNowMs">The time the close countdown is shown against (UTC milliseconds).</param>
        public StallWindowPresenter(HudConfig config, HudView view, StallConfig stall, StallWindowViewModel model, IStallWindowActions actions,
            Func<long> utcNowMs)
            : base(config, view, model)
        {
            _stall = stall;
            _model = model;
            _actions = actions;
            _utcNowMs = utcNowMs;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        public string TitleText => _title.text;

        public int RowCount => _rowListings.Count;

        /// <summary>The Choose (a buyer) or Take back (the owner) button of listing row <paramref name="index"/>.</summary>
        public Button RowButton(int index) => _rows[index].Q<Button>();

        public string RowText(int index) => _rows[index].Q<Label>(className: "stall-row__text").text;

        public int ChoiceCount => _model.Choices.Count;

        public Button Choice(int index) => _choices[index];

        public TextField CountField => _count;

        public TextField PriceField => _price;

        public Button SubmitButton => _submit;

        public Button CloseStallButton => _closeStall;

        public TextField BuyCountField => _buyCount;

        public Button BuyButton => _buy;

        public string TotalText => _total.text;

        public bool BuyRowShown => _buyRow.style.display == DisplayStyle.Flex;

        public bool ListingShown => _listing.style.display == DisplayStyle.Flex;

        protected override void Build()
        {
            _window = View.Place(HudElementIds.StallWindow, HudWidgets.Panel("stall-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.style.paddingBottom = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            var header = HudWidgets.Box("stall-header");
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.pickingMode = PickingMode.Position;
            _title = Text("", "stall-title");
            _title.style.color = Theme.Accent;
            header.Add(_title);
            var close = Button("stall_close", Config.Text.Get(StallTextIds.Close), () => _actions.CloseWindow());
            close.tooltip = Config.Text.Get(StallTextIds.CloseLabel);
            header.Add(close);
            _window.Add(header);

            _info = Text("", "stall-info");
            _window.Add(_info);
            _empty = Text(Config.Text.Get(StallTextIds.Empty), "stall-empty");
            _window.Add(_empty);
            _list = new ScrollView(ScrollViewMode.Vertical) { name = "stall_list" };
            _list.style.flexGrow = 1;
            _list.style.flexShrink = 1;
            _list.pickingMode = PickingMode.Position;
            _window.Add(_list);

            // The buyer's part (user decision 2026-11-14, "Choose the count", as in the auction window): the chosen row's count,
            // kept within its units, the total, and Buy.
            _buyRow = HudWidgets.Box("stall-buy");
            _buyRow.style.flexDirection = FlexDirection.Row;
            _buyRow.style.alignItems = Align.Center;
            _buyRow.pickingMode = PickingMode.Position;
            _buyCount = Field("stall_buy_count", StallTextIds.CountLabel, text => _actions.EditBuyCount(text));
            _buyCount.SetValueWithoutNotify("1");
            _buyRow.Add(_buyCount);
            _total = Text("", "stall-total");
            _total.style.marginLeft = 6;
            _total.style.marginRight = 6;
            _buyRow.Add(_total);
            _buy = Button("stall_buy", Config.Text.Get(StallTextIds.Buy), () => _actions.Buy());
            _buyRow.Add(_buy);
            _window.Add(_buyRow);

            // The owner's part: a stack of the bag, a count and a unit price, then Open stall or Add.
            _listing = HudWidgets.Box("stall-listing");
            _listing.pickingMode = PickingMode.Position;
            _pickHint = Text(Config.Text.Get(StallTextIds.PickHint), "stall-pick-hint");
            _pickHint.style.whiteSpace = WhiteSpace.Normal;
            _listing.Add(_pickHint);
            _choiceList = new ScrollView(ScrollViewMode.Vertical) { name = "stall_choices" };
            _choiceList.style.maxHeight = 120;
            _choiceList.pickingMode = PickingMode.Position;
            _listing.Add(_choiceList);
            var fields = HudWidgets.Box("stall-fields");
            fields.style.flexDirection = FlexDirection.Row;
            fields.pickingMode = PickingMode.Position;
            _count = Field("stall_count", StallTextIds.CountLabel, text => _actions.EditCount(text));
            _price = Field("stall_price", StallTextIds.PriceLabel, text => _actions.EditPrice(text));
            fields.Add(_count);
            fields.Add(_price);
            _listing.Add(fields);
            _submit = Button("stall_submit", "", () => _actions.List());
            _listing.Add(_submit);
            _window.Add(_listing);
            _closeStall = Button("stall_close_stall", Config.Text.Get(StallTextIds.CloseStall), () => _actions.CloseStall());
            _window.Add(_closeStall);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
            {
                _pointerInside = false; // a hidden window captures nothing (no leave event comes)
                return;
            }
            var mode = _model.Mode;
            HudWidgets.SetText(_title, mode switch
            {
                StallWindowMode.Browse => Config.Text.Get(StallTextIds.TitleBrowse, new TextArg("name", _model.OwnerName)),
                StallWindowMode.Manage => Config.Text.Get(StallTextIds.TitleManage),
                _ => Config.Text.Get(StallTextIds.TitleOpen),
            });
            var state = _model.State;
            Show(_info, state != null);
            if (state != null)
            {
                long left = Math.Max(0, state.ClosesUtcMs - _utcNowMs());
                HudWidgets.SetText(_info, Config.Text.Get(StallTextIds.ClosesIn, new TextArg("hours", Number((left + MsPerHour - 1) / MsPerHour)))
                    + "   " + Config.Text.Get(StallTextIds.Count, new TextArg("count", Number(state.Rows.Length)), new TextArg("cap", Number(state.Cap))));
            }
            Show(_empty, state != null && state.Rows.Length == 0);
            RefreshRows();

            bool browse = mode == StallWindowMode.Browse;
            Show(_buyRow, browse);
            if (browse)
            {
                // The field shows the model's count: on a new choice always, else when it does not have the focus, and with the focus
                // when the typed number is above the row's units (Buy takes no focus, so what is shown is what is sent).
                if (_model.BuySelected != _shownBuySelected || !_buyFocused || Above(_buyCount.value, _model.BuyCount))
                    WriteBuyCount();
                _shownBuySelected = _model.BuySelected;
                HudWidgets.SetText(_total, _model.BuySelectedRow == null ? ""
                    : Config.Text.Get(StallTextIds.Total, new TextArg("gold", Number(_model.BuyTotal))));
                _buy.SetEnabled(_model.CanBuy);
            }

            bool owner = mode is StallWindowMode.Manage or StallWindowMode.Open;
            Show(_listing, owner);
            Show(_closeStall, mode == StallWindowMode.Manage);
            if (owner)
            {
                RefreshChoices();
                bool setByWindow = _model.TextRevision != _textRevision;
                _textRevision = _model.TextRevision;
                if ((setByWindow || !_countFocused) && _count.value != _model.CountText)
                    _count.SetValueWithoutNotify(_model.CountText);
                if (!_priceFocused && _price.value != _model.PriceText)
                    _price.SetValueWithoutNotify(_model.PriceText);
                _submit.text = Config.Text.Get(mode == StallWindowMode.Open ? StallTextIds.OpenStall : StallTextIds.Add);
                _submit.SetEnabled(_model.CanList);
                _closeStall.SetEnabled(_model.Pending == 0);
            }
        }

        private void RefreshRows()
        {
            var rows = _model.Rows;
            while (_rows.Count < rows.Count)
            {
                int index = _rows.Count;
                var row = HudWidgets.Box("stall-row");
                row.style.flexDirection = FlexDirection.Row;
                row.style.justifyContent = Justify.SpaceBetween;
                row.pickingMode = PickingMode.Position;
                var label = Text("", "stall-row__text");
                label.style.flexShrink = 1;
                label.style.whiteSpace = WhiteSpace.Normal;
                row.Add(label);
                row.Add(Button("stall_row_" + index, "", () => RowPressed(index)));
                _rows.Add(row);
                _list.Add(row);
            }
            _rowListings.Clear();
            bool owner = _model.Mode == StallWindowMode.Manage;
            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = i < rows.Count;
                Show(_rows[i], used);
                if (!used)
                    continue;
                var entry = rows[i];
                _rowListings.Add(entry.ListingId);
                // The enchant level, rarity and quality on the row, as on the auction's (a buy commits at once).
                string item = ListingItems.ItemText(_stall.Inventory, Config.Text, StallTextIds.ItemCount, StallTextIds.ItemDetails, entry.Item);
                _rows[i].Q<Label>(className: "stall-row__text").text = Config.Text.Get(StallTextIds.Row, new TextArg("item", item),
                    new TextArg("price", Number(entry.UnitPrice)));
                var button = _rows[i].Q<Button>();
                button.text = Config.Text.Get(owner ? StallTextIds.Cancel : StallTextIds.Select);
                button.SetEnabled(_model.Pending == 0);
                HudWidgets.Border(_rows[i], !owner && entry.ListingId == _model.BuySelected ? Theme.Accent : Theme.Border);
            }
        }

        private void RowPressed(int index)
        {
            if (index >= _rowListings.Count)
                return;
            if (_model.Mode == StallWindowMode.Manage)
                _actions.CancelListing(_rowListings[index]);
            else
                _actions.SelectRow(_rowListings[index]); // a buyer chooses the row; Buy below sends the count
        }

        private void RefreshChoices()
        {
            var choices = _model.Choices;
            while (_choices.Count < choices.Count)
            {
                int index = _choices.Count;
                var button = Button("stall_choice_" + index, "", () => _actions.SelectChoice(index));
                _choices.Add(button);
                _choiceList.Add(button);
            }
            for (int i = 0; i < _choices.Count; i++)
            {
                bool used = i < choices.Count;
                Show(_choices[i], used);
                if (!used)
                    continue;
                var choice = choices[i];
                _choices[i].text = choice.Entry != null
                    ? ListingItems.ItemText(_stall.Inventory, Config.Text, StallTextIds.ItemCount, StallTextIds.ItemDetails, choice.Entry)
                    : ListingItems.ItemCount(_stall.Inventory, Config.Text, StallTextIds.ItemCount, choice.ItemNetId, choice.Count);
                HudWidgets.Border(_choices[i], i == _model.Selected ? Theme.Accent : Theme.Border);
            }
        }

        /// <summary>
        /// A field took or lost the focus (its focus events; tests call it): the gameplay keys are off while one has it, and a focused
        /// field keeps what is typed until the window itself sets the count (<see cref="StallWindowViewModel.TextRevision"/>).
        /// </summary>
        public void FieldFocus(TextField field, bool focused)
        {
            if (field == _count)
                _countFocused = focused;
            else if (field == _price)
                _priceFocused = focused;
            else if (field == _buyCount)
            {
                _buyFocused = focused;
                if (!focused)
                    WriteBuyCount(); // losing the focus shows what the model kept
            }
            _actions.FocusField(focused);
            Refresh();
        }

        private void WriteBuyCount()
        {
            string count = Number(_model.BuyCount);
            if (_buyCount.value != count)
                _buyCount.SetValueWithoutNotify(count);
        }

        private static bool Above(string text, int kept) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int typed) && kept > 0 && typed > kept;

        private TextField Field(string name, string labelId, Action<string> onChange)
        {
            var field = new TextField(Config.Text.Get(labelId)) { name = name };
            field.style.flexGrow = 1;
            field.RegisterValueChangedCallback(change => onChange(change.newValue));
            field.RegisterCallback<FocusInEvent>(_ => FieldFocus(field, true));
            field.RegisterCallback<FocusOutEvent>(_ => FieldFocus(field, false));
            return field;
        }

        private Label Text(string text, string className)
        {
            var label = HudWidgets.Label(text, className);
            label.enableRichText = false; // item names, a player's name and numbers: shown as written, never as markup
            label.style.color = Theme.Text;
            return label;
        }

        private Button Button(string name, string text, Action onClick)
        {
            var button = new Button(onClick) { focusable = false, name = name, text = text };
            button.enableRichText = false;
            button.style.color = Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(button, Theme.Border);
            return button;
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
