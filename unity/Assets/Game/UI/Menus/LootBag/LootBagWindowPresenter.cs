#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.LootBag;
using Shared.Protocol;
using UnityEngine.UIElements;

namespace Game.UI.Menus.LootBag
{
    /// <summary>
    /// Spec 9.13 <c>loot_bag_window</c> (spec 4.3 R-4.3-03, 4.4 R-4.4-01; WP-M4-9), placed by its <c>HudLayout</c> row. The title
    /// says whose bag it is to the viewer (the owner is never named, rule 8): the own bag, a party member's bag (what is taken goes
    /// to its owner by mail), someone else's bag (taking marks the items stolen), or someone else's bag inside its owner window
    /// (locked). Then the owner window's and the bag's time left, one row per stack (a click takes it), Take all and Close; the
    /// buttons are off while a take waits for its answer or the bag is locked. Text from <c>UiStrings</c>, rich text off.
    /// </summary>
    public sealed class LootBagWindowPresenter : HudPresenter
    {
        private readonly LootBagConfig _bags;
        private readonly LootBagWindowViewModel _model;
        private readonly ILootBagWindowActions _actions;
        private readonly Func<double> _nowMs;
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<byte> _rowIndexes = new List<byte>();
        private VisualElement _window = null!;
        private Label _title = null!;
        private Label _time = null!;
        private Label _note = null!;
        private Label _empty = null!;
        private ScrollView _list = null!;
        private Button _takeAll = null!;
        private bool _pointerInside;

        /// <param name="nowMs">The display clock the countdowns are drawn against (the binder's).</param>
        public LootBagWindowPresenter(HudConfig config, HudView view, LootBagConfig bags, LootBagWindowViewModel model,
            ILootBagWindowActions actions, Func<double> nowMs)
            : base(config, view, model)
        {
            _bags = bags;
            _model = model;
            _actions = actions;
            _nowMs = nowMs;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        public string TitleText => _title.text;

        public string TimeText => _time.text;

        public string NoteText => _note.style.display == DisplayStyle.Flex ? _note.text : "";

        public int RowCount => _rowIndexes.Count;

        public Button Row(int index) => _rows[index];

        public string RowLabel(int index) => _rows[index].text;

        public Button TakeAllButton => _takeAll;

        protected override void Build()
        {
            _window = View.Place(HudElementIds.LootBagWindow, HudWidgets.Panel("loot-bag-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.style.paddingBottom = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            var header = HudWidgets.Box("loot-bag-header");
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.pickingMode = PickingMode.Position;
            _title = Text("", "loot-bag-title");
            _title.style.color = Theme.Accent;
            header.Add(_title);
            var close = Button("loot_bag_close", Config.Text.Get(LootBagTextIds.Close), () => _actions.CloseBag());
            close.tooltip = Config.Text.Get(LootBagTextIds.CloseLabel);
            header.Add(close);
            _window.Add(header);

            _time = Text("", "loot-bag-time");
            _time.style.whiteSpace = WhiteSpace.Normal;
            _window.Add(_time);
            _note = Text("", "loot-bag-note");
            _note.style.whiteSpace = WhiteSpace.Normal;
            _note.style.color = Theme.Accent;
            _window.Add(_note);
            _empty = Text(Config.Text.Get(LootBagTextIds.Empty), "loot-bag-empty");
            _window.Add(_empty);
            _list = new ScrollView(ScrollViewMode.Vertical) { name = "loot_bag_list" };
            _list.style.flexGrow = 1;
            _list.style.flexShrink = 1;
            _list.pickingMode = PickingMode.Position;
            _window.Add(_list);
            _takeAll = Button("loot_bag_take_all", Config.Text.Get(LootBagTextIds.TakeAll), () => _actions.TakeAll());
            _window.Add(_takeAll);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            var content = _model.Content;
            if (!_model.Open || content == null)
            {
                _pointerInside = false; // a hidden window captures nothing (no leave event comes)
                return;
            }
            HudWidgets.SetText(_title, Config.Text.Get(content.Access switch
            {
                LootBagAccess.Own => LootBagTextIds.TitleOwn,
                LootBagAccess.CollectForOwner => LootBagTextIds.TitleForOwner,
                LootBagAccess.TakeStolen => LootBagTextIds.TitleOther,
                _ => LootBagTextIds.TitleLocked,
            }));
            double now = _nowMs();
            long window = _model.WindowLeftMs(now), expires = _model.ExpiresLeftMs(now);
            string time = Config.Text.Get(LootBagTextIds.Expires, new TextArg("minutes", Number((expires + 59_999) / 60_000)));
            if (window > 0 && content.Access != LootBagAccess.Own)
                time = Config.Text.Get(LootBagTextIds.Window, new TextArg("seconds", Number((window + 999) / 1_000))) + "  " + time;
            HudWidgets.SetText(_time, time);
            string note = content.Access switch
            {
                LootBagAccess.CollectForOwner => Config.Text.Get(LootBagTextIds.ForOwnerNote),
                LootBagAccess.TakeStolen => Config.Text.Get(LootBagTextIds.StolenWarning),
                _ => "",
            };
            HudWidgets.SetText(_note, note);
            Show(_note, note.Length > 0);
            Show(_empty, content.Rows.Length == 0);
            RefreshRows(content);
            _takeAll.SetEnabled(_model.CanTake);
        }

        private void RefreshRows(LootBagContent content)
        {
            while (_rows.Count < content.Rows.Length)
            {
                int index = _rows.Count;
                var row = Button("loot_bag_row_" + index, "", () => _actions.TakeRow(_rowIndexes[index]));
                _rows.Add(row);
                _list.Add(row);
            }
            _rowIndexes.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = i < content.Rows.Length;
                Show(_rows[i], used);
                if (!used)
                    continue;
                var row = content.Rows[i];
                _rowIndexes.Add(row.Index);
                var item = _bags.Item(row.ItemNetId);
                string name = item == null ? "#" + Number(row.ItemNetId) : Config.Text.Get(item.NameId);
                _rows[i].text = Config.Text.Get(row.Stolen ? LootBagTextIds.RowStolen : LootBagTextIds.Row, new TextArg("name", name),
                    new TextArg("count", Number(row.Count)));
                _rows[i].SetEnabled(_model.CanTake);
            }
        }

        private Label Text(string text, string className)
        {
            var label = HudWidgets.Label(text, className);
            label.enableRichText = false;
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
