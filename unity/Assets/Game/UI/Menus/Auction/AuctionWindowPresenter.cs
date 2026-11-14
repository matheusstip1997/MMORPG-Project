#nullable enable
using System;
using System.Collections.Generic;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Auction;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Auction
{
    /// <summary>
    /// Spec 9.13 <c>auction_window</c> (spec 4.5; M4 decision 6; WP-M4-7), placed by its <c>HudLayout</c> row. The header with the
    /// title, the three tabs and close; the notice line (the <c>ReasonCodeStrings</c> text of a sold-out, busy or changed listing);
    /// then one tab. Search: the name field and Search, the category, rarity, job and armour buttons (each steps through "any" and
    /// its values), the level range, the page (one button per row: the item, its count, the unit price and the time left; no seller,
    /// user answer 2026-11-13), Previous and Next, the count to buy, its total and Buy. My listings: the count of the cap and one row
    /// per listing with Cancel. Sell: the hint, the bag's tradable stacks to pick (or a drop from the inventory window), the chosen
    /// stack, its count, the unit price, the duration, the listing fee (never returned) and List. Buttons that would send are off
    /// while an answer is awaited. Text from <c>UiStrings</c>, rich text off; the server decides every listing and every sale.
    /// </summary>
    public sealed class AuctionWindowPresenter : HudPresenter
    {
        private const long MsPerMinute = 60_000L;
        private const long MsPerHour = 60 * MsPerMinute;

        private readonly AuctionConfig _auction;
        private readonly AuctionWindowViewModel _model;
        private readonly IAuctionWindowActions _actions;
        private readonly Func<long> _utcNowMs;
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<long> _rowIds = new List<long>();
        private readonly List<VisualElement> _mineRows = new List<VisualElement>();
        private readonly List<long> _mineIds = new List<long>();
        private readonly List<Button> _picks = new List<Button>();
        private VisualElement _window = null!;
        private Button _tabSearch = null!;
        private Button _tabMine = null!;
        private Button _tabSell = null!;
        private Label _notice = null!;
        private Label _waiting = null!;
        private VisualElement _searchPanel = null!;
        private TextField _name = null!;
        private Button _search = null!;
        private Button _category = null!;
        private Button _rarity = null!;
        private Button _job = null!;
        private Button _armor = null!;
        private TextField _levelMin = null!;
        private TextField _levelMax = null!;
        private Label _empty = null!;
        private ScrollView _list = null!;
        private Button _previous = null!;
        private Button _next = null!;
        private TextField _buyCount = null!;
        private Label _total = null!;
        private Button _buy = null!;
        private VisualElement _minePanel = null!;
        private Label _mineCount = null!;
        private Label _mineEmpty = null!;
        private ScrollView _mineList = null!;
        private VisualElement _sellPanel = null!;
        private ScrollView _pickList = null!;
        private Label _sellItem = null!;
        private TextField _sellCount = null!;
        private TextField _sellPrice = null!;
        private Button _duration = null!;
        private Label _fee = null!;
        private Button _listButton = null!;
        private bool _pointerInside;
        private Label _tooMany = null!;
        private bool _buyFocused;
        private bool _sellFocused;
        private long _shownSelected;

        /// <param name="utcNowMs">The time the rows' time left is shown against (UTC milliseconds).</param>
        public AuctionWindowPresenter(HudConfig config, HudView view, AuctionConfig auction, AuctionWindowViewModel model,
            IAuctionWindowActions actions, Func<long> utcNowMs)
            : base(config, view, model)
        {
            _auction = auction;
            _model = model;
            _actions = actions;
            _utcNowMs = utcNowMs;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        public Button TabButton(AuctionTab tab) => tab switch
        {
            AuctionTab.Mine => _tabMine,
            AuctionTab.Sell => _tabSell,
            _ => _tabSearch,
        };

        public TextField NameField => _name;

        public Button SearchButton => _search;

        public Button CategoryButton => _category;

        public Button PreviousButton => _previous;

        public Button NextButton => _next;

        public int RowCount => _rowIds.Count;

        public Button Row(int index) => _rows[index];

        public string RowLabel(int index) => _rows[index].Q<Label>(className: "auction-row__text").text;

        public TextField BuyCountField => _buyCount;

        public string TotalText => _total.text;

        public Button BuyButton => _buy;

        public string NoticeText => _notice.style.display == DisplayStyle.Flex ? _notice.text : "";

        public bool EmptyShown => _empty.style.display == DisplayStyle.Flex;

        public string EmptyText => _empty.text;

        public string TooManyText => _tooMany.style.display == DisplayStyle.Flex ? _tooMany.text : "";

        public string MineCountText => _mineCount.text;

        public int MineRowCount => _mineIds.Count;

        public string MineRowLabel(int index) => _mineRows[index].Q<Label>(className: "auction-mine__text").text;

        public Button MineCancel(int index) => _mineRows[index].Q<Button>(className: "auction-mine__cancel");

        public int PickCount => _picks.Count == 0 ? 0 : CountShown(_picks);

        public Button Pick(int index) => _picks[index];

        public string SellItemText => _sellItem.text;

        public TextField SellCountField => _sellCount;

        public TextField SellPriceField => _sellPrice;

        public Button DurationButton => _duration;

        public string FeeText => _fee.text;

        public Button ListButton => _listButton;

        public bool WaitingShown => _waiting.style.display == DisplayStyle.Flex;

        /// <summary>
        /// An inventory window drop at <paramref name="panelPosition"/>: true (taken: the stack to list, on the Sell tab) when it lands
        /// on the open window; false otherwise, so the next drop target can take it.
        /// </summary>
        public bool DropAt(UnityEngine.Vector2 panelPosition, byte container, InventorySlotEntry entry)
        {
            if (!_model.Open || !IsShown || !_window.worldBound.Contains(panelPosition))
                return false;
            _actions.PickSell(container, entry);
            return true;
        }

        protected override void Build()
        {
            _window = View.Place(HudElementIds.AuctionWindow, HudWidgets.Panel("auction-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.style.paddingBottom = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            var header = RowBox("auction-header");
            header.style.justifyContent = Justify.SpaceBetween;
            var title = HudWidgets.Label(Config.Text.Get(AuctionTextIds.Title), "auction-title");
            title.style.color = Theme.Accent;
            header.Add(title);
            var close = Button("auction_close", Config.Text.Get(AuctionTextIds.Close), () => _actions.CloseAuction());
            close.tooltip = Config.Text.Get(AuctionTextIds.CloseLabel);
            header.Add(close);
            _window.Add(header);

            var tabs = RowBox("auction-tabs");
            _tabSearch = Button("auction_tab_search", Config.Text.Get(AuctionTextIds.TabSearch), () => _actions.SelectTab(AuctionTab.Search));
            _tabMine = Button("auction_tab_mine", Config.Text.Get(AuctionTextIds.TabMine), () => _actions.SelectTab(AuctionTab.Mine));
            _tabSell = Button("auction_tab_sell", Config.Text.Get(AuctionTextIds.TabSell), () => _actions.SelectTab(AuctionTab.Sell));
            tabs.Add(_tabSearch);
            tabs.Add(_tabMine);
            tabs.Add(_tabSell);
            _window.Add(tabs);

            _notice = Text("", "auction-notice");
            _notice.name = "auction_notice";
            _notice.style.color = Theme.Accent;
            _notice.style.whiteSpace = WhiteSpace.Normal;
            _window.Add(_notice);

            BuildSearch();
            BuildMine();
            BuildSell();

            _waiting = Text(Config.Text.Get(AuctionTextIds.Waiting), "auction-waiting");
            _window.Add(_waiting);
        }

        private void BuildSearch()
        {
            _searchPanel = Panel("auction-search");
            var nameRow = RowBox("auction-search__name");
            _name = Field("auction_name", "", _actions.EditName);
            _name.textEdition.placeholder = Config.Text.Get(AuctionTextIds.NameHint);
            _name.style.flexGrow = 1;
            _name.RegisterCallback<KeyDownEvent>(key =>
            {
                if (key.keyCode is UnityEngine.KeyCode.Return or UnityEngine.KeyCode.KeypadEnter)
                    _actions.Search();
            });
            nameRow.Add(_name);
            _search = Button("auction_search", Config.Text.Get(AuctionTextIds.Search), () => _actions.Search());
            nameRow.Add(_search);
            _searchPanel.Add(nameRow);

            var filters = RowBox("auction-search__filters");
            filters.style.flexWrap = Wrap.Wrap;
            _category = Button("auction_category", "", () => _actions.CycleCategory());
            _rarity = Button("auction_rarity", "", () => _actions.CycleRarity());
            _job = Button("auction_job", "", () => _actions.CycleJob());
            _armor = Button("auction_armor", "", () => _actions.CycleArmor());
            filters.Add(_category);
            filters.Add(_rarity);
            filters.Add(_job);
            filters.Add(_armor);
            _searchPanel.Add(filters);

            var levels = RowBox("auction-search__levels");
            levels.Add(Text(Config.Text.Get(AuctionTextIds.LevelMin), "auction-label"));
            _levelMin = Field("auction_level_min", "", _actions.EditLevelMin);
            _levelMin.style.width = 48;
            levels.Add(_levelMin);
            levels.Add(Text(Config.Text.Get(AuctionTextIds.LevelMax), "auction-label"));
            _levelMax = Field("auction_level_max", "", _actions.EditLevelMax);
            _levelMax.style.width = 48;
            levels.Add(_levelMax);
            _searchPanel.Add(levels);

            _empty = Text("", "auction-empty");
            _searchPanel.Add(_empty);
            _tooMany = Text("", "auction-too-many");
            _tooMany.name = "auction_too_many";
            _tooMany.style.whiteSpace = WhiteSpace.Normal;
            _tooMany.style.color = Theme.Accent;
            _searchPanel.Add(_tooMany);
            _list = new ScrollView(ScrollViewMode.Vertical) { name = "auction_list" };
            _list.style.flexGrow = 1;
            _list.style.flexShrink = 1;
            _list.pickingMode = PickingMode.Position;
            _searchPanel.Add(_list);

            var pager = RowBox("auction-pager");
            _previous = Button("auction_previous", Config.Text.Get(AuctionTextIds.Previous), () => _actions.Previous());
            _next = Button("auction_next", Config.Text.Get(AuctionTextIds.Next), () => _actions.Next());
            pager.Add(_previous);
            pager.Add(_next);
            _searchPanel.Add(pager);

            var buyRow = RowBox("auction-buy");
            buyRow.Add(Text(Config.Text.Get(AuctionTextIds.Count), "auction-label"));
            _buyCount = Field("auction_buy_count", "1", _actions.EditBuyCount);
            _buyCount.style.width = 56;
            _buyCount.RegisterCallback<FocusInEvent>(_ => _buyFocused = true);
            _buyCount.RegisterCallback<FocusOutEvent>(_ =>
            {
                _buyFocused = false;
                WriteBuyCount(); // what the model kept (at most the row's units) once typing ends
            });
            buyRow.Add(_buyCount);
            _total = Text("", "auction-total");
            _total.style.flexGrow = 1;
            buyRow.Add(_total);
            _buy = Button("auction_buy", Config.Text.Get(AuctionTextIds.Buy), () => _actions.Buy());
            buyRow.Add(_buy);
            _searchPanel.Add(buyRow);
            _window.Add(_searchPanel);
        }

        private void BuildMine()
        {
            _minePanel = Panel("auction-mine");
            _mineCount = Text("", "auction-mine__count");
            _minePanel.Add(_mineCount);
            _mineEmpty = Text(Config.Text.Get(AuctionTextIds.MineEmpty), "auction-mine__empty");
            _minePanel.Add(_mineEmpty);
            _mineList = new ScrollView(ScrollViewMode.Vertical) { name = "auction_mine_list" };
            _mineList.style.flexGrow = 1;
            _mineList.style.flexShrink = 1;
            _mineList.pickingMode = PickingMode.Position;
            _minePanel.Add(_mineList);
            _window.Add(_minePanel);
        }

        private void BuildSell()
        {
            _sellPanel = Panel("auction-sell");
            var hint = Text(Config.Text.Get(AuctionTextIds.SellHint), "auction-sell__hint");
            hint.style.whiteSpace = WhiteSpace.Normal;
            _sellPanel.Add(hint);
            _pickList = new ScrollView(ScrollViewMode.Vertical) { name = "auction_pick_list" };
            _pickList.style.flexGrow = 1;
            _pickList.style.flexShrink = 1;
            _pickList.pickingMode = PickingMode.Position;
            _sellPanel.Add(_pickList);
            _sellItem = Text("", "auction-sell__item");
            _sellItem.style.color = Theme.Accent;
            _sellItem.style.whiteSpace = WhiteSpace.Normal;
            _sellPanel.Add(_sellItem);

            var numbers = RowBox("auction-sell__numbers");
            numbers.Add(Text(Config.Text.Get(AuctionTextIds.Count), "auction-label"));
            _sellCount = Field("auction_sell_count", "", _actions.EditSellCount);
            _sellCount.style.width = 56;
            _sellCount.RegisterCallback<FocusInEvent>(_ => _sellFocused = true);
            _sellCount.RegisterCallback<FocusOutEvent>(_ =>
            {
                _sellFocused = false;
                WriteSellCount();
            });
            numbers.Add(_sellCount);
            numbers.Add(Text(Config.Text.Get(AuctionTextIds.Price), "auction-label"));
            _sellPrice = Field("auction_sell_price", "", _actions.EditSellPrice);
            _sellPrice.style.width = 96;
            numbers.Add(_sellPrice);
            _sellPanel.Add(numbers);

            var listRow = RowBox("auction-sell__list");
            _duration = Button("auction_duration", "", () => _actions.CycleDuration());
            listRow.Add(_duration);
            _fee = Text("", "auction-sell__fee");
            _fee.style.flexGrow = 1;
            listRow.Add(_fee);
            _listButton = Button("auction_list_button", Config.Text.Get(AuctionTextIds.List), () => _actions.List());
            listRow.Add(_listButton);
            _sellPanel.Add(listRow);
            _window.Add(_sellPanel);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
            {
                _pointerInside = false; // a hidden window captures nothing (no leave event comes)
                return;
            }
            HudWidgets.Border(_tabSearch, _model.Tab == AuctionTab.Search ? Theme.Accent : Theme.Border);
            HudWidgets.Border(_tabMine, _model.Tab == AuctionTab.Mine ? Theme.Accent : Theme.Border);
            HudWidgets.Border(_tabSell, _model.Tab == AuctionTab.Sell ? Theme.Accent : Theme.Border);
            Show(_notice, _model.Notice.Length > 0);
            if (_model.Notice.Length > 0)
                HudWidgets.SetText(_notice, Config.Reasons.Text(_model.Notice));
            Show(_waiting, _model.PendingAction != 0 || _model.PendingSearch != 0);
            Show(_searchPanel, _model.Tab == AuctionTab.Search);
            Show(_minePanel, _model.Tab == AuctionTab.Mine);
            Show(_sellPanel, _model.Tab == AuctionTab.Sell);
            switch (_model.Tab)
            {
                case AuctionTab.Search:
                    RefreshSearch();
                    break;
                case AuctionTab.Mine:
                    RefreshMine();
                    break;
                case AuctionTab.Sell:
                    RefreshSell();
                    break;
            }
        }

        private void RefreshSearch()
        {
            _category.text = Config.Text.Get(AuctionTextIds.Category, new TextArg("kind", _model.CategoryIndex < 0
                ? Config.Text.Get(AuctionTextIds.CategoryAny)
                : Config.Text.Get(AuctionTextIds.CategoryOf(AuctionConfig.Categories[_model.CategoryIndex]))));
            string rarity = Config.Text.Get(AuctionTextIds.Any);
            if (_model.RarityIndex >= 0 && _model.RarityIndex < _auction.RarityTiers.Count)
            {
                string id = _auction.Inventory.RarityTextId(_auction.RarityTiers[_model.RarityIndex]);
                rarity = Config.Text.Get(AuctionTextIds.RarityAtLeast, new TextArg("rarity", id.Length > 0 ? Config.Text.Get(id)
                    : NumberText.Of(_auction.RarityTiers[_model.RarityIndex])));
            }
            _rarity.text = Config.Text.Get(AuctionTextIds.Rarity, new TextArg("rarity", rarity));
            _job.text = Config.Text.Get(AuctionTextIds.Job, new TextArg("job", _model.JobIndex >= 0 && _model.JobIndex < _auction.Jobs.Count
                ? Config.Text.Get(_auction.Jobs[_model.JobIndex].NameId)
                : Config.Text.Get(AuctionTextIds.Any)));
            _armor.text = Config.Text.Get(AuctionTextIds.Armor, new TextArg("tier", _model.ArmorTier == 0
                ? Config.Text.Get(AuctionTextIds.Any)
                : Config.Text.Get(InventoryTextIds.ArmorTier((ArmorClass)_model.ArmorTier))));
            _search.SetEnabled(_model.CanSearch);
            _previous.SetEnabled(_model.CanPrevious);
            _next.SetEnabled(_model.CanNext);
            bool empty = _model.HasPage && _model.Rows.Count == 0;
            Show(_empty, empty);
            if (empty)
                HudWidgets.SetText(_empty, Config.Text.Get(_model.NoMatch ? AuctionTextIds.NoMatch : AuctionTextIds.Empty));
            RefreshRows();
            // The field shows the model's count: on a selection change always, else when it does not have the focus.
            if (_model.Selected != _shownSelected || !_buyFocused)
                WriteBuyCount();
            _shownSelected = _model.Selected;
            bool tooMany = _model.HasPage && _model.TooManyMatches;
            Show(_tooMany, tooMany);
            if (tooMany)
                HudWidgets.SetText(_tooMany, Config.Text.Get(AuctionTextIds.TooMany, new TextArg("max", NumberText.Of(_auction.SearchItemIdsMax))));
            HudWidgets.SetText(_total, _model.SelectedRow == null ? ""
                : Config.Text.Get(AuctionTextIds.Total, new TextArg("gold", NumberText.Of(_model.BuyTotal))));
            _buy.SetEnabled(_model.CanBuy);
        }

        private void RefreshRows()
        {
            var rows = _model.Rows;
            while (_rows.Count < rows.Count)
            {
                int index = _rows.Count;
                var row = Button("auction_row_" + index, "", () => _actions.SelectRow(_rowIds[index]));
                row.style.flexDirection = FlexDirection.Row;
                var label = Text("", "auction-row__text");
                label.style.flexShrink = 1;
                label.style.whiteSpace = WhiteSpace.Normal;
                row.Add(label);
                _rows.Add(row);
                _list.Add(row);
            }
            _rowIds.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = i < rows.Count;
                Show(_rows[i], used);
                if (!used)
                    continue;
                var entry = rows[i];
                _rowIds.Add(entry.ListingId);
                _rows[i].Q<Label>(className: "auction-row__text").text = RowText(entry);
                HudWidgets.Border(_rows[i], entry.ListingId == _model.Selected ? Theme.Accent : Theme.Border);
            }
        }

        private void RefreshMine()
        {
            HudWidgets.SetText(_mineCount, Config.Text.Get(AuctionTextIds.MineCount, new TextArg("count", NumberText.Of(_model.Mine.Count)),
                new TextArg("cap", NumberText.Of(_model.MineCap))));
            Show(_mineEmpty, _model.HasMine && _model.Mine.Count == 0);
            var mine = _model.Mine;
            while (_mineRows.Count < mine.Count)
            {
                int index = _mineRows.Count;
                var row = RowBox("auction-mine__row");
                row.name = "auction_mine_row_" + index;
                var label = Text("", "auction-mine__text");
                label.style.flexGrow = 1;
                label.style.flexShrink = 1;
                label.style.whiteSpace = WhiteSpace.Normal;
                row.Add(label);
                var cancel = Button("auction_mine_cancel_" + index, Config.Text.Get(AuctionTextIds.Cancel),
                    () => _actions.CancelListing(_mineIds[index]));
                cancel.AddToClassList("auction-mine__cancel");
                row.Add(cancel);
                _mineRows.Add(row);
                _mineList.Add(row);
            }
            _mineIds.Clear();
            for (int i = 0; i < _mineRows.Count; i++)
            {
                bool used = i < mine.Count;
                Show(_mineRows[i], used);
                if (!used)
                    continue;
                _mineIds.Add(mine[i].ListingId);
                _mineRows[i].Q<Label>(className: "auction-mine__text").text = RowText(mine[i]);
                _mineRows[i].Q<Button>(className: "auction-mine__cancel").SetEnabled(_model.CanCancel);
            }
        }

        private void RefreshSell()
        {
            var stacks = _model.Sellable;
            while (_picks.Count < stacks.Count)
            {
                int index = _picks.Count;
                var pick = Button("auction_pick_" + index, "",
                    () => _actions.PickSell(_model.SellableContainer, _model.Sellable[index]));
                pick.style.whiteSpace = WhiteSpace.Normal;
                _picks.Add(pick);
                _pickList.Add(pick);
            }
            for (int i = 0; i < _picks.Count; i++)
            {
                bool used = i < stacks.Count;
                Show(_picks[i], used);
                if (!used)
                    continue;
                _picks[i].text = AuctionBinder.ItemText(_auction, Config.Text, stacks[i]);
                bool chosen = _model.SellItem != null && _model.SellContainer == _model.SellableContainer
                                                      && _model.SellItem.Slot == stacks[i].Slot;
                HudWidgets.Border(_picks[i], chosen ? Theme.Accent : Theme.Border);
            }
            HudWidgets.SetText(_sellItem, _model.SellItem == null ? "" : AuctionBinder.ItemText(_auction, Config.Text, _model.SellItem));
            if (!_sellFocused)
                WriteSellCount();
            int hours = (int)(Math.Max(0, DurationMs()) / MsPerHour);
            _duration.text = Config.Text.Get(AuctionTextIds.Duration, new TextArg("hours", NumberText.Of(hours)));
            _duration.SetEnabled(_auction.DurationsMs.Count > 1);
            long fee = _model.SellItem == null || _model.SellPrice <= 0 ? 0 : _auction.FeeFor(_model.SellPrice, Math.Max(1, _model.SellCount));
            HudWidgets.SetText(_fee, Config.Text.Get(AuctionTextIds.Fee, new TextArg("fee", NumberText.Of(fee))));
            _listButton.SetEnabled(_model.CanList);
        }

        private long DurationMs() =>
            _model.DurationIndex < _auction.DurationsMs.Count ? _auction.DurationsMs[_model.DurationIndex] : 0;

        /// <summary>"Demon Ash x4   15 gold each   23 h left" (no seller: user answer 2026-11-13).</summary>
        private string RowText(ListingEntry entry) =>
            Config.Text.Get(AuctionTextIds.Row, new TextArg("item", AuctionBinder.ItemText(_auction, Config.Text, entry.Item)),
                new TextArg("price", NumberText.Of(entry.UnitPrice)), new TextArg("time", TimeLeft(entry.ExpiresUtcMs)));

        private string TimeLeft(long expiresUtcMs)
        {
            long left = Math.Max(0, expiresUtcMs - _utcNowMs());
            return left >= MsPerHour
                ? Config.Text.Get(AuctionTextIds.Hours, new TextArg("hours", NumberText.Of(left / MsPerHour)))
                : Config.Text.Get(AuctionTextIds.Minutes, new TextArg("minutes", NumberText.Of((left + MsPerMinute - 1) / MsPerMinute)));
        }

        private void WriteBuyCount()
        {
            string count = NumberText.Of(_model.BuyCount);
            if (_buyCount.value != count)
                _buyCount.SetValueWithoutNotify(count);
        }

        private void WriteSellCount()
        {
            string count = _model.SellCount > 0 ? NumberText.Of(_model.SellCount) : "";
            if (_sellCount.value != count)
                _sellCount.SetValueWithoutNotify(count);
        }

        private TextField Field(string name, string value, Action<string> onChange)
        {
            var field = new TextField { name = name, value = value };
            field.RegisterValueChangedCallback(change => onChange(change.newValue));
            field.RegisterCallback<FocusInEvent>(_ => _actions.FocusField(true));
            field.RegisterCallback<FocusOutEvent>(_ => _actions.FocusField(false));
            return field;
        }

        private static VisualElement RowBox(string className)
        {
            var row = HudWidgets.Box(className);
            row.style.flexDirection = FlexDirection.Row;
            row.pickingMode = PickingMode.Position;
            return row;
        }

        private static VisualElement Panel(string className)
        {
            var panel = HudWidgets.Box(className);
            panel.style.flexGrow = 1;
            panel.style.flexShrink = 1;
            panel.pickingMode = PickingMode.Position;
            return panel;
        }

        private static int CountShown(List<Button> buttons)
        {
            int shown = 0;
            foreach (var button in buttons)
            {
                if (button.style.display != DisplayStyle.None)
                    shown++;
            }
            return shown;
        }

        private Label Text(string text, string className)
        {
            var label = HudWidgets.Label(text, className);
            label.enableRichText = false; // item names and numbers: shown as written, never as markup
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
    }
}
