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
    /// Spec 9.13 (M2) <c>vendor_window</c> (WP-M2-4): an NPC vendor's stock (R-4.2-01: potions and food at fixed prices),
    /// placed by its <c>HudLayout</c> row: one row per item with its price, the selected row's count stepper, the total, the
    /// buy button, and the last purchase. Text from <c>UiStrings</c>; the server checks the gold and the bag. The window has the
    /// fixed size of its row: the stock list is a scroll view that takes the height left, and the header, stepper, buy button
    /// and last purchase never shrink, so a longer stock scrolls instead of running under the stepper and out of the window
    /// (play finding on the M3 stock of 8 rows).
    /// </summary>
    public sealed class VendorWindowPresenter : HudPresenter
    {
        private readonly VendorWindowViewModel _model;
        private readonly IVendorWindowActions _actions;
        private readonly List<Button> _rows = new List<Button>();
        private VisualElement _window = null!;
        private ScrollView _list = null!;
        private VisualElement _stepper = null!;
        private Label _total = null!;
        private Label _result = null!;
        private Button _buy = null!;
        private bool _pointerInside;

        public VendorWindowPresenter(HudConfig config, HudView view, VendorWindowViewModel model, IVendorWindowActions actions)
            : base(config, view, model)
        {
            _model = model;
            _actions = actions;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        public string TotalText => _total.text;

        public string ResultText => _result.text;

        public bool BuyEnabled => _buy.enabledSelf;

        public int RowCount => _rows.Count;

        public Button Row(int index) => _rows[index];

        /// <summary>The part of the stock list the player sees, the stepper row and the buy button, for layout tests.</summary>
        public VisualElement ListViewport => _list.contentViewport;

        public VisualElement Stepper => _stepper;

        public Button BuyButton => _buy;

        protected override void Build()
        {
            _window = View.Place(HudElementIds.VendorWindow, HudWidgets.Panel("vendor-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            var header = OutpostWidgets.Header(Config, Theme, OutpostTextIds.VendorTitle, "vendor_close", () => _actions.CloseVendor());
            header.style.flexShrink = 0;
            _window.Add(header);
            _list = new ScrollView(ScrollViewMode.Vertical) { name = "vendor_list" };
            _list.AddToClassList("vendor-list");
            _list.style.flexGrow = 1;
            _list.style.flexShrink = 1;
            _list.pickingMode = PickingMode.Position;
            _window.Add(_list);

            var stepper = _stepper = HudWidgets.Box("vendor-stepper");
            stepper.style.flexDirection = FlexDirection.Row;
            stepper.style.alignItems = Align.Center;
            stepper.style.flexShrink = 0;
            stepper.pickingMode = PickingMode.Position;
            stepper.Add(OutpostWidgets.Button(Theme, "vendor_less", Config.Text.Get(OutpostTextIds.Less), () => _actions.StepCount(-1)));
            _total = HudWidgets.Label("", "vendor-total");
            _total.style.color = Theme.Accent;
            _total.style.minWidth = 120;
            _total.style.unityTextAlign = TextAnchor.MiddleCenter;
            stepper.Add(_total);
            stepper.Add(OutpostWidgets.Button(Theme, "vendor_more", Config.Text.Get(OutpostTextIds.More), () => _actions.StepCount(1)));
            _window.Add(stepper);

            _buy = OutpostWidgets.Button(Theme, "vendor_buy", Config.Text.Get(OutpostTextIds.Buy), () => _actions.Buy());
            _buy.style.flexShrink = 0;
            _window.Add(_buy);
            _result = HudWidgets.Label("", "vendor-result");
            _result.style.color = Theme.Text;
            _result.style.flexShrink = 0;
            _window.Add(_result);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
                return;
            var stock = _model.Stock;
            while (_rows.Count < stock.Count)
            {
                int index = _rows.Count;
                var row = OutpostWidgets.Button(Theme, "vendor_row_" + index, "", () => _actions.SelectStock(index));
                row.style.unityTextAlign = TextAnchor.MiddleLeft;
                row.style.flexShrink = 0;
                // Compact rows (the theme button's 10 px padding made them 37 px tall): the M3 stock fits the row's height.
                row.style.paddingTop = 4;
                row.style.paddingBottom = 4;
                _rows.Add(row);
                _list.Add(row);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = i < stock.Count;
                Show(_rows[i], used);
                if (!used)
                    continue;
                _rows[i].text = Config.Text.Get(OutpostTextIds.StockRow, new TextArg("name", Config.Text.Get(stock[i].Item.Def.NameId)),
                    new TextArg("price", Number(stock[i].PriceGold)));
                HudWidgets.Border(_rows[i], i == _model.Selected ? Theme.Accent : Theme.Border);
            }

            HudWidgets.SetText(_total, _model.Selected == VendorWindowViewModel.None
                ? ""
                : Config.Text.Get(OutpostTextIds.Total, new TextArg("count", Number(_model.Count)), new TextArg("total", Number(_model.Total))));
            _buy.SetEnabled(_model.Confirm() != null);
            HudWidgets.SetText(_result, _model.LastBought is { } bought
                ? Config.Text.Get(OutpostTextIds.Bought, new TextArg("count", Number(bought.Count)), new TextArg("gold", Number(bought.Gold)))
                : "");
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
