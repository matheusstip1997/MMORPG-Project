#nullable enable
using System.Collections.Generic;
using System.Globalization;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Trade;
using Shared.Protocol;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Trade
{
    /// <summary>
    /// Spec 9.13 <c>trade_window</c> (spec 4.5 "M4 trade window"; WP-M4-4): placed by its <c>HudLayout</c> row while the server
    /// says the window is open. Two columns, the own offer (drop items from the inventory window here; click one to take it
    /// back) and the partner's (named through <c>DisplayIdentity</c>), each with its gold and its lock and confirm marks; the
    /// own gold field; Lock, Confirm (with the time left once both locked) and Cancel; a line while the swap saves. Text from
    /// <c>UiStrings</c> and <c>ReasonCodeStrings</c>; the server checks everything again at the confirm.
    /// TODO(WP-M4-11): item icons in the offer rows (text rows for now).
    /// </summary>
    public sealed class TradeWindowPresenter : HudPresenter
    {
        private readonly InventoryConfig _items;
        private readonly TradeWindowViewModel _model;
        private readonly ITradeWindowActions _actions;
        private readonly List<Button> _ownRows = new List<Button>();
        private readonly List<Label> _partnerRows = new List<Label>();
        private VisualElement _panel = null!;
        private Label _title = null!;
        private VisualElement _ownColumn = null!;
        private VisualElement _ownList = null!;
        private VisualElement _partnerList = null!;
        private Label _ownEmpty = null!;
        private Label _partnerEmpty = null!;
        private Label _ownState = null!;
        private Label _partnerHeader = null!;
        private Label _partnerGold = null!;
        private Label _partnerState = null!;
        private TextField _goldField = null!;
        private Button _setGold = null!;
        private Button _lock = null!;
        private Button _confirm = null!;
        private Button _cancel = null!;
        private Label _saving = null!;
        private bool _pointerInside;

        public TradeWindowPresenter(HudConfig config, HudView view, InventoryConfig items, TradeWindowViewModel model, ITradeWindowActions actions)
            : base(config, view, model)
        {
            _items = items;
            _model = model;
            _actions = actions;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _panel.style.display == DisplayStyle.Flex;

        public VisualElement Panel => _panel;

        public VisualElement OwnColumn => _ownColumn;

        public Button OwnRow(int index) => _ownRows[index];

        public Label PartnerRow(int index) => _partnerRows[index];

        public string Title => _title.text;

        public string PartnerGoldText => _partnerGold.text;

        public string OwnStateText => _ownState.text;

        public string PartnerStateText => _partnerState.text;

        public TextField GoldField => _goldField;

        public Button LockButton => _lock;

        public Button ConfirmButton => _confirm;

        public Button CancelButton => _cancel;

        public bool SavingShown => _saving.style.display == DisplayStyle.Flex;

        /// <summary>
        /// An inventory window drop at <paramref name="panelPosition"/>: true (taken, the stack offered) when it lands on the own
        /// column of the open window; false otherwise, so the next drop target can take it.
        /// </summary>
        public bool DropAt(UnityEngine.Vector2 panelPosition, byte container, InventorySlotEntry entry)
        {
            if (!_model.Open || !IsShown || !_ownColumn.worldBound.Contains(panelPosition))
                return false;
            _actions.DropOffer(container, entry);
            return true;
        }

        protected override void Build()
        {
            _panel = View.Place(HudElementIds.TradeWindow, HudWidgets.Panel("trade-window", Theme));
            _panel.pickingMode = PickingMode.Position;
            _panel.style.paddingLeft = 6;
            _panel.style.paddingRight = 6;
            _panel.style.paddingTop = 4;
            _panel.style.paddingBottom = 4;
            _panel.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _panel.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            _title = Text("", "trade-title");
            _title.style.color = Theme.Accent;
            _panel.Add(_title);

            var columns = HudWidgets.Box("trade-columns");
            columns.style.flexDirection = FlexDirection.Row;
            columns.pickingMode = PickingMode.Position;
            _panel.Add(columns);

            _ownColumn = Column("trade-own");
            _ownColumn.Add(Text(Config.Text.Get(TradeTextIds.You), "trade-column__title"));
            _ownList = HudWidgets.Box("trade-own__list");
            _ownList.pickingMode = PickingMode.Position;
            _ownColumn.Add(_ownList);
            _ownEmpty = Text(Config.Text.Get(TradeTextIds.DropHint), "trade-own__empty");
            _ownColumn.Add(_ownEmpty);
            var goldRow = HudWidgets.Box("trade-own__gold");
            goldRow.style.flexDirection = FlexDirection.Row;
            goldRow.pickingMode = PickingMode.Position;
            _goldField = new TextField { name = "trade_gold", value = "0" };
            _goldField.style.flexGrow = 1;
            _goldField.RegisterValueChangedCallback(change => _actions.EditGold(change.newValue));
            _goldField.RegisterCallback<FocusInEvent>(_ => _actions.FocusGold(true));
            _goldField.RegisterCallback<FocusOutEvent>(_ =>
            {
                _actions.FocusGold(false);
                _actions.SetGold(_goldField.value);
            });
            _goldField.RegisterCallback<KeyDownEvent>(key =>
            {
                if (key.keyCode is UnityEngine.KeyCode.Return or UnityEngine.KeyCode.KeypadEnter)
                    _actions.SetGold(_goldField.value);
            });
            _setGold = Button("trade_set_gold", Config.Text.Get(TradeTextIds.SetGold), () => _actions.SetGold(_goldField.value));
            goldRow.Add(_goldField);
            goldRow.Add(_setGold);
            _ownColumn.Add(goldRow);
            _ownState = Text("", "trade-own__state");
            _ownColumn.Add(_ownState);
            columns.Add(_ownColumn);

            var partnerColumn = Column("trade-partner");
            _partnerHeader = Text("", "trade-column__title");
            partnerColumn.Add(_partnerHeader);
            _partnerList = HudWidgets.Box("trade-partner__list");
            partnerColumn.Add(_partnerList);
            _partnerEmpty = Text(Config.Text.Get(TradeTextIds.Empty), "trade-partner__empty");
            partnerColumn.Add(_partnerEmpty);
            _partnerGold = Text("", "trade-partner__gold");
            partnerColumn.Add(_partnerGold);
            _partnerState = Text("", "trade-partner__state");
            partnerColumn.Add(_partnerState);
            columns.Add(partnerColumn);

            var buttons = HudWidgets.Box("trade-buttons");
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.marginTop = 6;
            buttons.pickingMode = PickingMode.Position;
            _lock = Button("trade_lock", Config.Text.Get(TradeTextIds.Lock), () => _actions.Lock());
            _confirm = Button("trade_confirm", Config.Text.Get(TradeTextIds.Confirm), () => _actions.Confirm());
            _cancel = Button("trade_cancel", Config.Text.Get(TradeTextIds.Cancel), () => _actions.Cancel());
            buttons.Add(_lock);
            buttons.Add(_confirm);
            buttons.Add(_cancel);
            _panel.Add(buttons);
            _saving = Text(Config.Text.Get(TradeTextIds.Saving), "trade-saving");
            _panel.Add(_saving);
        }

        protected override void Refresh()
        {
            Show(_panel, _model.Open);
            if (!_model.Open)
            {
                _pointerInside = false; // hidden under the pointer: no leave event comes
                return;
            }
            var name = new TextArg("name", _model.PartnerName);
            HudWidgets.SetText(_title, Config.Text.Get(TradeTextIds.Title, name));
            HudWidgets.SetText(_partnerHeader, Config.Text.Get(TradeTextIds.Partner, name));
            RefreshOwn();
            RefreshPartner();
            _lock.SetEnabled(_model.CanLock);
            _confirm.SetEnabled(_model.CanConfirm);
            _confirm.text = _model.SecondsLeft > 0 && !_model.OwnConfirmed
                ? Config.Text.Get(TradeTextIds.ConfirmCountdown, new TextArg("seconds", Number(_model.SecondsLeft)))
                : Config.Text.Get(TradeTextIds.Confirm);
            _cancel.SetEnabled(_model.CanCancel);
            Show(_saving, _model.Saving);
        }

        private void RefreshOwn()
        {
            var offers = _model.OwnOffer;
            while (_ownRows.Count < offers.Length)
            {
                int index = _ownRows.Count;
                var row = Button("trade_own_" + index, "", () => _actions.RemoveOffer(index));
                row.style.unityTextAlign = UnityEngine.TextAnchor.MiddleLeft;
                _ownRows.Add(row);
                _ownList.Add(row);
            }
            for (int i = 0; i < _ownRows.Count; i++)
            {
                bool used = i < offers.Length;
                Show(_ownRows[i], used);
                if (!used)
                    continue;
                _ownRows[i].text = OfferText(offers[i]);
                _ownRows[i].SetEnabled(_model.CanEdit);
            }
            Show(_ownEmpty, offers.Length == 0);
            if (_model.GoldDraft == null && _goldField.panel?.focusController?.focusedElement != _goldField)
                _goldField.SetValueWithoutNotify(Number(_model.OwnGold));
            _goldField.SetEnabled(_model.CanEdit);
            _setGold.SetEnabled(_model.CanEdit);
            HudWidgets.SetText(_ownState, StateText(_model.OwnLocked, _model.OwnConfirmed));
        }

        private void RefreshPartner()
        {
            var offers = _model.PartnerOffer;
            while (_partnerRows.Count < offers.Length)
            {
                var row = Text("", "trade-partner__row");
                _partnerRows.Add(row);
                _partnerList.Add(row);
            }
            for (int i = 0; i < _partnerRows.Count; i++)
            {
                bool used = i < offers.Length;
                Show(_partnerRows[i], used);
                if (used)
                    HudWidgets.SetText(_partnerRows[i], OfferText(offers[i]));
            }
            Show(_partnerEmpty, offers.Length == 0);
            HudWidgets.SetText(_partnerGold, Config.Text.Get(TradeTextIds.Gold, new TextArg("gold", Number(_model.PartnerGold))));
            HudWidgets.SetText(_partnerState, StateText(_model.PartnerLocked, _model.PartnerConfirmed));
        }

        private string OfferText(TradeOffer offer)
        {
            string item = _items.Item(offer.ItemNetId) is { } info ? Config.Text.Get(info.Def.NameId) : Number(offer.ItemNetId);
            if (offer.Enchant > 0)
                item = Config.Text.Get(TradeTextIds.OfferEnchanted, new TextArg("item", item), new TextArg("enchant", Number(offer.Enchant)));
            string line = Config.Text.Get(TradeTextIds.Offer, new TextArg("item", item), new TextArg("count", Number(offer.Count)));
            // What tells two offers of one item apart (review of #177): the rarity, a crafted quality, the gems socketed.
            string rarityId = _items.RarityTextId(offer.Rarity);
            string rarity = rarityId.Length > 0 ? Config.Text.Get(rarityId) : Number(offer.Rarity);
            string quality = offer.Quality > 0
                ? Config.Text.Get(InventoryTextIds.TooltipQuality, new TextArg("quality", Number(offer.Quality)))
                : string.Empty;
            string gems = offer.GemCount > 0 ? Config.Text.Get(TradeTextIds.OfferGems, new TextArg("count", Number(offer.GemCount))) : string.Empty;
            string details = Config.Text.Get(TradeTextIds.OfferDetails, new TextArg("rarity", rarity), new TextArg("quality", quality),
                new TextArg("gems", gems)).Trim();
            while (details.Contains("  "))
                details = details.Replace("  ", " "); // a missing quality or gems part leaves no gap
            return line + "\n" + details;
        }

        private string StateText(bool locked, bool confirmed) =>
            confirmed ? Config.Text.Get(TradeTextIds.Confirmed) : locked ? Config.Text.Get(TradeTextIds.Locked) : string.Empty;

        private VisualElement Column(string className)
        {
            var column = HudWidgets.Box(className);
            column.pickingMode = PickingMode.Position;
            column.style.flexGrow = 1;
            column.style.flexBasis = 0;
            column.style.marginRight = 6;
            HudWidgets.Border(column, Theme.Border);
            return column;
        }

        private Label Text(string text, string className)
        {
            var label = HudWidgets.Label(text, className);
            label.enableRichText = false; // names and item names are shown as typed
            label.style.color = Theme.Text;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private Button Button(string name, string text, System.Action onClick)
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

    /// <summary>
    /// The trade invite prompt <c>trade_invite</c> (WP-M4-4): "{name} wants to trade" with Accept and Decline and the time left,
    /// or, for the own invite, "Waiting for {name}" with Cancel. Shaped like the party's invite prompt (WP-M4-2).
    /// </summary>
    public sealed class TradeInvitePresenter : HudPresenter
    {
        private readonly TradeInviteViewModel _model;
        private readonly ITradeWindowActions _actions;
        private VisualElement _panel = null!;
        private Label _text = null!;
        private Button _accept = null!;
        private Button _decline = null!;
        private Button _cancel = null!;
        private bool _pointerInside;

        public TradeInvitePresenter(HudConfig config, HudView view, TradeInviteViewModel model, ITradeWindowActions actions)
            : base(config, view, model)
        {
            _model = model;
            _actions = actions;
        }

        public bool CapturesPointer => _model.Visible && _pointerInside;

        public bool IsShown => _panel.style.display == DisplayStyle.Flex;

        public string Text => _text.text;

        public Button AcceptButton => _accept;

        public Button DeclineButton => _decline;

        public Button CancelButton => _cancel;

        protected override void Build()
        {
            _panel = View.Place(HudElementIds.TradeInvite, HudWidgets.Panel("trade-invite", Theme));
            _panel.pickingMode = PickingMode.Position;
            _panel.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _panel.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);
            _panel.style.flexDirection = FlexDirection.Row;
            _panel.style.paddingLeft = 6;
            _panel.style.paddingRight = 6;
            _text = HudWidgets.Label("", "trade-invite__text");
            _text.enableRichText = false; // the partner's name as typed
            _text.style.color = Theme.Text;
            _text.style.flexGrow = 1;
            _panel.Add(_text);
            _accept = MakeButton("trade_accept", TradeTextIds.Accept, () => _actions.Accept());
            _decline = MakeButton("trade_decline", TradeTextIds.Decline, () => _actions.Decline());
            _cancel = MakeButton("trade_invite_cancel", TradeTextIds.Cancel, () => _actions.Cancel());
            _panel.Add(_accept);
            _panel.Add(_decline);
            _panel.Add(_cancel);
        }

        protected override void Refresh()
        {
            Show(_panel, _model.Visible);
            if (!_model.Visible)
            {
                _pointerInside = false;
                return;
            }
            var name = new TextArg("name", _model.PartnerName);
            var seconds = new TextArg("seconds", _model.SecondsLeft.ToString(CultureInfo.InvariantCulture));
            HudWidgets.SetText(_text, Config.Text.Get(_model.Incoming ? TradeTextIds.Invited : TradeTextIds.Inviting, name, seconds));
            Show(_accept, _model.Incoming);
            Show(_decline, _model.Incoming);
            Show(_cancel, !_model.Incoming);
        }

        private Button MakeButton(string name, string textId, System.Action onClick)
        {
            var button = new Button(onClick) { focusable = false, name = name, text = Config.Text.Get(textId) };
            button.style.color = Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(button, Theme.Border);
            return button;
        }
    }
}
