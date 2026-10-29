#nullable enable
using System.Collections.Generic;
using System.Globalization;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Book;
using Shared.Data.Tables;
using Shared.Protocol;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Book
{
    /// <summary>
    /// Spec 9.13 (M2) <c>book_window</c> (spec 14.2, WP-M2-8): the Bystial book, placed by its <c>HudLayout</c> row. The slots
    /// the rank opens (four per line), each showing its card or "Empty"; the selected card's bonus with a take-out button;
    /// and the collection, one row per monster type with its placeholder card art (<c>BystialCardArt</c>), its tier and the
    /// kills toward the next tier. Select a card, then click a slot to place it (an occupied slot swaps). Text from
    /// <c>UiStrings</c>; the server decides every card and slot.
    /// </summary>
    public sealed class BookWindowPresenter : HudPresenter
    {
        private const int SlotsPerLine = 4;
        private const int SwatchPx = 12;

        private readonly BookConfig _book;
        private readonly BookWindowViewModel _model;
        private readonly IBookWindowActions _actions;
        private readonly List<Button> _slots = new List<Button>();
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<ushort> _rowCards = new List<ushort>();
        private VisualElement _window = null!;
        private VisualElement _slotGrid = null!;
        private ScrollView _list = null!;
        private Label _detail = null!;
        private Label _empty = null!;
        private Button _takeOut = null!;
        private bool _pointerInside;

        public BookWindowPresenter(HudConfig config, HudView view, BookConfig book, BookWindowViewModel model, IBookWindowActions actions)
            : base(config, view, model)
        {
            _book = book;
            _model = model;
            _actions = actions;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        public int SlotButtonCount => _slots.Count;

        public Button SlotButton(int slot) => _slots[slot];

        public int RowCount => _rows.Count;

        public Button Row(int index) => _rows[index];

        /// <summary>The text of collection row <paramref name="index"/>.</summary>
        public string RowLabel(int index) => _rows[index].Q<Label>(className: "book-row__text").text;

        public string DetailText => _detail.text;

        public bool TakeOutEnabled => _takeOut.enabledSelf;

        protected override void Build()
        {
            _window = View.Place(HudElementIds.BookWindow, HudWidgets.Panel("book-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.style.paddingBottom = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            var header = HudWidgets.Box("book-header");
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.pickingMode = PickingMode.Position;
            var title = HudWidgets.Label(Config.Text.Get(BookTextIds.Title), "book-title");
            title.style.color = Theme.Accent;
            header.Add(title);
            var close = Button("book_close", Config.Text.Get(BookTextIds.Close), () => _actions.CloseBook());
            close.tooltip = Config.Text.Get(BookTextIds.CloseLabel);
            header.Add(close);
            _window.Add(header);

            _window.Add(Caption(BookTextIds.Slots));
            _slotGrid = HudWidgets.Box("book-slots");
            _slotGrid.style.flexDirection = FlexDirection.Row;
            _slotGrid.style.flexWrap = Wrap.Wrap;
            _slotGrid.pickingMode = PickingMode.Position;
            _window.Add(_slotGrid);

            var detail = HudWidgets.Box("book-detail");
            detail.style.flexDirection = FlexDirection.Row;
            detail.style.alignItems = Align.Center;
            detail.pickingMode = PickingMode.Position;
            _detail = HudWidgets.Label("", "book-detail__text");
            _detail.style.color = Theme.Text;
            _detail.style.flexGrow = 1;
            _detail.style.flexShrink = 1;
            _detail.style.whiteSpace = WhiteSpace.Normal;
            detail.Add(_detail);
            _takeOut = Button("book_takeout", Config.Text.Get(BookTextIds.TakeOut), () => _actions.TakeOut());
            detail.Add(_takeOut);
            _window.Add(detail);

            _window.Add(Caption(BookTextIds.Collection));
            _empty = HudWidgets.Label(Config.Text.Get(BookTextIds.Empty), "book-empty");
            _empty.style.color = Theme.Text;
            _window.Add(_empty);
            _list = new ScrollView(ScrollViewMode.Vertical) { name = "book_list" };
            _list.style.flexGrow = 1;
            _list.style.flexShrink = 1;
            _list.pickingMode = PickingMode.Position;
            _window.Add(_list);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
                return;
            RefreshSlots();
            RefreshRows();
            var selected = _model.SelectedCard;
            HudWidgets.SetText(_detail, selected == null ? Config.Text.Get(BookTextIds.Hint) : BonusText(selected));
            Show(_takeOut, _model.CanTakeOut);
            _takeOut.SetEnabled(_model.CanTakeOut);
        }

        private void RefreshSlots()
        {
            while (_slots.Count < _model.SlotCount)
            {
                int slot = _slots.Count;
                var button = Button("book_slot_" + slot, "", () => _actions.PlaceInto(slot));
                button.style.width = Length.Percent(100f / SlotsPerLine - 2f);
                button.style.marginLeft = 1;
                button.style.marginRight = 1;
                button.style.overflow = Overflow.Hidden;
                _slots.Add(button);
                _slotGrid.Add(button);
            }
            for (int slot = 0; slot < _slots.Count; slot++)
            {
                bool open = slot < _model.SlotCount;
                Show(_slots[slot], open);
                if (!open)
                    continue;
                var card = _model.InSlot(slot);
                _slots[slot].text = card == null ? Config.Text.Get(BookTextIds.SlotEmpty) : Name(card.MonsterNetId);
                HudWidgets.Border(_slots[slot], _model.CanPlace(slot) ? Theme.Accent : Theme.Border);
            }
        }

        private void RefreshRows()
        {
            var cards = _model.Cards;
            Show(_empty, cards.Count == 0);
            while (_rows.Count < cards.Count)
            {
                int index = _rows.Count;
                var row = Button("book_row_" + index, "", () => _actions.SelectCard(_rowCards[index]));
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                var swatch = HudWidgets.Box("book-row__art");
                swatch.style.width = SwatchPx;
                swatch.style.height = SwatchPx;
                swatch.style.marginRight = 4;
                swatch.style.flexShrink = 0;
                row.Add(swatch);
                var label = HudWidgets.Label("", "book-row__text");
                label.style.color = Theme.Text;
                label.style.flexShrink = 1;
                row.Add(label);
                _rows.Add(row);
                _rowCards.Add(0);
                _list.Add(row);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = i < cards.Count;
                Show(_rows[i], used);
                if (!used)
                    continue;
                var card = cards[i];
                _rowCards[i] = card.MonsterNetId;
                _rows[i].Q<Label>(className: "book-row__text").text = RowText(card);
                _rows[i].Q(className: "book-row__art").style.backgroundColor = ArtColor(card.MonsterNetId);
                HudWidgets.Border(_rows[i], card.MonsterNetId == _model.Selected ? Theme.Accent : Theme.Border);
            }
        }

        /// <summary>"Demon (Normal), 100 / 500 kills, in slot 1".</summary>
        private string RowText(BookCard card)
        {
            string tier = card.Tier == BystialTier.None
                ? Config.Text.Get(BookTextIds.NotEarned)
                : Config.Text.Get(UiStringCoverage.BookTierId(card.Tier));
            string text = Config.Text.Get(BookTextIds.Card, new TextArg("name", Name(card.MonsterNetId)), new TextArg("tier", tier))
                          + "   " + (card.NextTierKills > 0
                              ? Config.Text.Get(BookTextIds.Counter, new TextArg("kills", Number(card.Kills)), new TextArg("next", Number(card.NextTierKills)))
                              : Config.Text.Get(BookTextIds.CounterTop, new TextArg("kills", Number(card.Kills))));
            if (card.Slot != BookLimits.NoSlot)
                text += "   " + Config.Text.Get(BookTextIds.Placed, new TextArg("slot", Number(card.Slot + 1)));
            return text;
        }

        /// <summary>The selected card's current tier: its stat bonus and its effect ("Max HP +10, Fire resistance +5%").</summary>
        private string BonusText(BookCard card)
        {
            var row = _book.Card(card.MonsterNetId)?.TierRow(card.Tier);
            if (row == null)
                return RowText(card);
            var family = _book.Card(card.MonsterNetId)!.Family;
            string value = row.Flat != 0 ? Signed(row.Flat) : Signed(row.Percent) + "%";
            string stat = Config.Text.Get(BookTextIds.Bonus, new TextArg("stat", Config.Text.Get(UiStringCoverage.BookStatId(row.StatId))),
                new TextArg("value", value));
            if (row.EffectKind == BystialEffectKind.None)
                return stat;
            string effect = Config.Text.Get(UiStringCoverage.BookEffectId(row.EffectKind), new TextArg("value", Number(row.EffectValue)),
                new TextArg("element", Config.Text.Get(UiStringCoverage.BookElementId(row.EffectElement))),
                new TextArg("family", Config.Text.Get(UiStringCoverage.BookFamilyId(family))));
            return stat + ", " + effect;
        }

        private string Name(ushort monsterNetId) =>
            _book.Card(monsterNetId) is { } card ? Config.Text.Get(card.NameId) : Number(monsterNetId);

        /// <summary>The card's placeholder art color (<c>BystialCardArt</c>); the border color without a row.</summary>
        private Color ArtColor(ushort monsterNetId) =>
            _book.Card(monsterNetId)?.Art is { } art && HudColor.TryParse(art.PlaceholderColor, out var c)
                ? new Color32(c.R, c.G, c.B, c.A)
                : Theme.Border;

        private VisualElement Caption(string textId)
        {
            var label = HudWidgets.Label(Config.Text.Get(textId), "book-caption");
            label.style.color = Theme.Accent;
            label.style.marginTop = 4;
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

        private static string Signed(int value) => (value > 0 ? "+" : "") + value.ToString(CultureInfo.InvariantCulture);
    }
}
