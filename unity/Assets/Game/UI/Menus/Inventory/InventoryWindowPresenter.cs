#nullable enable
using System;
using System.Linq;
using Game.Gameplay.Entities.Visuals;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Inventory;
using Shared.Data.Tables;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Inventory
{
    /// <summary>What the player does in the inventory window; the binder turns it into requests (WP16).</summary>
    public interface IInventoryWindowActions
    {
        /// <summary>Click-to-move: select, deselect, or move the selection onto <paramref name="slot"/>.</summary>
        void Click(int slot);

        /// <summary>A stack dragged from <paramref name="from"/> and released on <paramref name="to"/>.</summary>
        void Drop(int from, int to);

        /// <summary>
        /// A stack dragged out of the window and released at <paramref name="panelPosition"/>: onto the quick-item slot it binds
        /// a consumable there (WP-M3-3); elsewhere it is refused with a toast (no dropping to the ground yet).
        /// </summary>
        void DropOutside(int from, Vector2 panelPosition);

        /// <summary>Right-click or double-click on a stack: use it (spec 3.4 consumable use, 9.13; WP-M3-3).</summary>
        void Use(int slot);

        /// <summary>Shows another bag container (<c>ContainerDef.code</c>) in the grid (WP-M2-1).</summary>
        void SelectTab(byte container);

        /// <summary>Shift+click: a split of half the stack in <paramref name="slot"/>; the next click places it (WP-M2-11).</summary>
        void BeginSplit(int slot);

        /// <summary>The split bar's minus and plus.</summary>
        void AdjustSplit(int delta);

        /// <summary>The split bar's cancel: the selection and the split are dropped.</summary>
        void CancelSplit();

        void Close();
    }

    /// <summary>
    /// Spec 9.13 <c>inventory_window</c> (backlog G client part, WP16; containers and equipment WP-M2-1): a tab row with one
    /// button per bag container (<c>inv.container.&lt;id&gt;</c>), the active one as a grid of <c>InventoryWindowSettings.columns</c>
    /// columns, and the equipment strip below it (one slot per <c>EquipSlotDef</c> row, its name <c>inv.equipSlot.&lt;id&gt;</c> on an
    /// empty slot, a dual weapon's second slot dimmed), placed by its <c>HudLayout</c> row. Clicks and drags work between the
    /// grid and the strip (equip, unequip) and, with click-to-move, across tabs. The minimum of WP-M2-1: the full tabs, the
    /// character sheet and item stats are WP-M2-11. Each slot shows the item's icon
    /// (<c>ItemIconRegistry</c>: an Addressables texture, or the placeholder shape and color), the stack count, a preview
    /// style while a move waits for the server, and the accent border when selected. Hovering a stack shows its tooltip
    /// (name, rarity, category, count, stolen; all from <c>UiStrings</c>). A press and release on a slot is a click
    /// (click-to-move); a press that moves past <c>dragThresholdPx</c> drags the stack under the pointer onto another slot.
    /// Redraws on the view model's <c>Changed</c> only; the server decides every move.
    /// </summary>
    public sealed class InventoryWindowPresenter : HudPresenter
    {
        private readonly InventoryConfig _inventory;
        private readonly InventoryWindowViewModel _model;
        private readonly IInventoryWindowActions _actions;
        private readonly ITextureSource? _icons;
        private VisualElement _window = null!;
        private VisualElement _grid = null!;
        private VisualElement _tabs = null!;
        private VisualElement _equipStrip = null!;
        private SlotElements[] _equip = Array.Empty<SlotElements>();
        private Button[] _tabButtons = Array.Empty<Button>();
        private Label _tooltipLevel = null!;
        private VisualElement _tooltip = null!;
        private Label _tooltipName = null!;
        private Label _tooltipRarity = null!;
        private Label _tooltipCategory = null!;
        private Label _tooltipCount = null!;
        private Label _tooltipStolen = null!;
        private VisualElement _tooltipStats = null!;
        private Label _tooltipWeapon = null!;
        private Label _tooltipSlots = null!;
        private Label _tooltipBound = null!;
        private Label _tooltipQuality = null!;
        private Label _tooltipCrafted = null!;
        private Label _tooltipTainted = null!;
        private Label _tooltipReserved = null!;
        private Label _tooltipUse = null!;
        private Label _title = null!;
        private VisualElement _splitBar = null!;
        private Label _splitText = null!;
        private ItemIconElement _ghost = null!;
        private SlotElements[] _slots = Array.Empty<SlotElements>();
        private int _pressSlot = InventoryWindowViewModel.None;
        private bool _pressShift;
        private Vector2 _pressAt;
        private bool _dragging;
        private bool _pointerInside;

        public InventoryWindowPresenter(HudConfig config, HudView view, InventoryConfig inventory, InventoryWindowViewModel model,
            IInventoryWindowActions actions, ITextureSource? icons) : base(config, view, model)
        {
            _inventory = inventory;
            _model = model;
            _actions = actions;
            _icons = icons;
            if (icons != null)
                icons.Finished += _ => Refresh();
        }

        private sealed class SlotElements
        {
            public VisualElement Root = null!;
            public ItemIconElement Icon = null!;
            public Label Count = null!;
            public Label? Name;
        }

        /// <summary>True while the window is open and the pointer is on it or drags from it: gameplay ignores the mouse.</summary>
        public bool CapturesPointer => _model.Open && (_pointerInside || _pressSlot != InventoryWindowViewModel.None);

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        public int SlotElementCount => _slots.Length;

        public VisualElement SlotElement(int slot) => _slots[slot].Root;

        /// <summary>The count shown on a slot ("" for a single item or an empty slot), for tests and QA checks.</summary>
        public string CountText(int slot) => _slots[slot].Count.text;

        public ItemIconElement Icon(int slot) => _slots[slot].Icon;

        /// <summary>The equipment strip's slots (<c>EquipSlotDef</c> codes), for tests and QA checks.</summary>
        public int EquipElementCount => _equip.Length;

        public VisualElement EquipElement(int slot) => _equip[slot].Root;

        public ItemIconElement EquipIcon(int slot) => _equip[slot].Icon;

        /// <summary>The tab button of a container code, or null.</summary>
        public Button? TabButton(byte code) => _tabButtons.FirstOrDefault(b => (byte)b.userData == code);

        /// <summary>The tooltip's lines, one per line, or "" when it is hidden.</summary>
        public string TooltipText => _tooltip.style.display != DisplayStyle.Flex
            ? ""
            : string.Join("\n", new[] { _tooltipName, _tooltipRarity, _tooltipCategory, _tooltipCount, _tooltipLevel, _tooltipStolen }
                .Concat(_tooltipStats.Children().OfType<Label>()).Concat(new[] { _tooltipWeapon, _tooltipSlots, _tooltipQuality, _tooltipCrafted, _tooltipTainted, _tooltipBound, _tooltipReserved, _tooltipUse })
                .Where(label => label.style.display != DisplayStyle.None && label.text.Length > 0).Select(label => label.text));

        /// <summary>The split bar's text while a split is set up (WP-M2-11); empty otherwise.</summary>
        public string SplitText => _splitBar.style.display == DisplayStyle.Flex ? _splitText.text : "";

        /// <summary>The split bar's buttons (tests and the QA smoke click them).</summary>
        public Button SplitLess => _splitBar.Q<Button>("inv_split_less");

        public Button SplitMore => _splitBar.Q<Button>("inv_split_more");

        protected override void Build()
        {
            var settings = _inventory.Settings;
            _window = View.Place(HudElementIds.InventoryWindow, HudWidgets.Panel("inv-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);
            _window.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _window.RegisterCallback<PointerUpEvent>(OnPointerUp);
            _window.RegisterCallback<PointerCaptureOutEvent>(_ => EndPress());

            var header = HudWidgets.Box("inv-header");
            _title = HudWidgets.Label(Config.Text.Get(InventoryTextIds.Title), "inv-title");
            header.Add(_title);
            header.Add(BuildSplitBar());
            var close = new Button(() => _actions.Close()) { name = "inv_close", text = Config.Text.Get(InventoryTextIds.Close) };
            close.tooltip = Config.Text.Get(InventoryTextIds.CloseLabel); // spec 9.8: a text label for an icon-only control
            close.AddToClassList("inv-close");
            close.style.color = Theme.Text;
            close.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(close, Theme.Border);
            header.Add(close);
            _window.Add(header);

            // One line as wide as the grid (Hud.uss: no wrap, the tabs share the width), spaced like the slots below it.
            float width = settings.Columns * settings.SlotSizePx + (settings.Columns - 1) * settings.SlotGapPx;
            _tabs = HudWidgets.Box("inv-tabs");
            _tabs.style.width = width;
            _tabs.style.marginBottom = settings.SlotGapPx;
            _tabButtons = new Button[_model.Tabs.Count];
            for (int i = 0; i < _model.Tabs.Count; i++)
            {
                byte code = _model.Tabs[i];
                var tab = new Button(() => _actions.SelectTab(code))
                {
                    name = "inv_tab_" + code, text = Config.Text.Get(_inventory.ContainerTextId(code)), userData = code,
                };
                tab.AddToClassList("inv-tab");
                tab.style.color = Theme.Text;
                tab.style.fontSize = Theme.Tokens.FontSizeBody;
                tab.style.marginRight = i == _model.Tabs.Count - 1 ? 0 : settings.SlotGapPx;
                _tabButtons[i] = tab;
                _tabs.Add(tab);
            }
            _window.Add(_tabs);

            _grid = HudWidgets.Box("inv-grid");
            _grid.name = "inv_grid";
            _grid.style.width = width;
            _window.Add(_grid);
            BuildSlots(_model.SlotCount);

            _equipStrip = HudWidgets.Box("inv-equip");
            _equipStrip.name = "inv_equip";
            _equipStrip.style.width = width;
            _equipStrip.style.marginTop = settings.SlotGapPx * 2;
            _window.Add(_equipStrip);
            _equip = new SlotElements[_model.EquipSlotCount];
            for (int i = 0; i < _equip.Length; i++)
            {
                int code = i;
                _equip[i] = NewSlot("inv_equip_" + i, () => _model.SlotCount + code, i);
                var name = HudWidgets.Label(Config.Text.Get(_inventory.EquipSlotTextId(i)), "inv-slot__name");
                name.pickingMode = PickingMode.Ignore;
                name.style.fontSize = Theme.Tokens.FontSizeBody; // spec 9.8: at least 12 px
                name.style.color = Theme.Locked;
                _equip[i].Root.Add(name);
                _equip[i].Name = name;
                RowFor(_equipStrip, i).Add(_equip[i].Root);
            }

            _tooltip = HudWidgets.Panel("inv-tooltip", Theme);
            _tooltip.name = "inv_tooltip";
            _tooltipName = TooltipLine("inv-tooltip__name");
            _tooltipName.style.color = Theme.Accent;
            _tooltipRarity = TooltipLine("inv-tooltip__line");
            _tooltipCategory = TooltipLine("inv-tooltip__line");
            _tooltipCount = TooltipLine("inv-tooltip__line");
            _tooltipLevel = TooltipLine("inv-tooltip__line");
            _tooltipStolen = TooltipLine("inv-tooltip__line");
            _tooltipStolen.style.color = Theme.Hp;
            // WP-M2-11: the item's stats (one line per ItemStatBonusDef row at its rarity), weapon type, slots, bound.
            _tooltipStats = HudWidgets.Box("inv-tooltip__stats");
            _tooltip.Add(_tooltipStats);
            _tooltipWeapon = TooltipLine("inv-tooltip__line");
            _tooltipSlots = TooltipLine("inv-tooltip__line");
            // WP-M3-2: a crafted item's quality and crafter, a tainted item, a reserved stack.
            _tooltipQuality = TooltipLine("inv-tooltip__line");
            _tooltipCrafted = TooltipLine("inv-tooltip__line");
            _tooltipTainted = TooltipLine("inv-tooltip__line");
            _tooltipTainted.style.color = Theme.Hp;
            _tooltipBound = TooltipLine("inv-tooltip__line");
            _tooltipBound.style.color = Theme.Accent;
            _tooltipReserved = TooltipLine("inv-tooltip__line");
            _tooltipReserved.style.color = Theme.Accent;
            _tooltipUse = TooltipLine("inv-tooltip__line"); // WP-M3-3: how to use a consumable
            Show(_tooltip, false);
            _window.Add(_tooltip);

            _ghost = new ItemIconElement { name = "inv_drag_ghost", pickingMode = PickingMode.Ignore };
            _ghost.AddToClassList("inv-ghost");
            _ghost.style.width = settings.SlotSizePx;
            _ghost.style.height = settings.SlotSizePx;
            Show(_ghost, false);
            View.Root.Add(_ghost);
        }

        /// <summary>
        /// WP-M2-11 stack splitting: in the header line while a split is set up (Shift+click on a stack), in the title's place
        /// so the window keeps its height: "Split 2 of 5", minus, plus and cancel. The next click on a free slot or a stack of
        /// the same item places the split.
        /// </summary>
        private VisualElement BuildSplitBar()
        {
            _splitBar = HudWidgets.Box("inv-split");
            _splitBar.name = "inv_split";
            _splitBar.pickingMode = PickingMode.Position;
            _splitText = HudWidgets.Label("", "inv-split__text");
            _splitText.style.color = Theme.Accent;
            _splitBar.Add(_splitText);
            _splitBar.Add(SplitButton("inv_split_less", InventoryTextIds.SplitLess, () => _actions.AdjustSplit(-1)));
            _splitBar.Add(SplitButton("inv_split_more", InventoryTextIds.SplitMore, () => _actions.AdjustSplit(1)));
            _splitBar.Add(SplitButton("inv_split_cancel", InventoryTextIds.SplitCancel, () => _actions.CancelSplit()));
            Show(_splitBar, false);
            return _splitBar;
        }

        private Button SplitButton(string name, string textId, Action onClick)
        {
            var button = new Button(onClick) { name = name, text = Config.Text.Get(textId) };
            button.AddToClassList("inv-split__button");
            button.style.color = Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(button, Theme.Border);
            return button;
        }

        private Label TooltipLine(string className)
        {
            var label = HudWidgets.Label("", className);
            _tooltip.Add(label);
            return label;
        }

        private void BuildSlots(int count)
        {
            _grid.Clear();
            _slots = new SlotElements[count];
            for (int i = 0; i < count; i++)
            {
                int slot = i;
                _slots[i] = NewSlot("inv_slot_" + i, () => slot, i);
                RowFor(_grid, i).Add(_slots[i].Root);
            }
        }

        /// <summary>
        /// The row of slot <paramref name="index"/> in a grid: <c>InventoryWindowSettings.columns</c> slots per row element, so
        /// the columns never depend on the slots fitting the width exactly. A wrapping box of exactly that width wrapped the
        /// last column at a fractional panel scale (a 1469 x 855 game view: 5 columns, the strip drawn below the window).
        /// </summary>
        private VisualElement RowFor(VisualElement grid, int index)
        {
            if (index % _inventory.Settings.Columns == 0)
                grid.Add(HudWidgets.Box("inv-row"));
            return grid[grid.childCount - 1];
        }

        /// <summary>One slot element; <paramref name="flat"/> gives the view model's flat slot (grid, then equipment strip).</summary>
        private SlotElements NewSlot(string name, Func<int> flat, int column)
        {
            var settings = _inventory.Settings;
            var root = HudWidgets.Box("inv-slot");
            root.name = name;
            root.pickingMode = PickingMode.Position;
            root.style.width = settings.SlotSizePx;
            root.style.height = settings.SlotSizePx;
            root.style.marginRight = (column + 1) % settings.Columns == 0 ? 0 : settings.SlotGapPx;
            root.style.marginBottom = settings.SlotGapPx;
            root.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
            HudWidgets.Border(root, Theme.Border);
            var icon = new ItemIconElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("inv-slot__icon");
            root.Add(icon);
            var countLabel = HudWidgets.Label("", "inv-slot__count");
            countLabel.style.fontSize = Theme.Tokens.FontSizeBody;
            countLabel.style.color = Theme.Text;
            root.Add(countLabel);
            root.RegisterCallback<PointerDownEvent>(e => OnSlotDown(flat(), e));
            root.RegisterCallback<PointerEnterEvent>(_ => _model.SetHovered(flat()));
            root.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (_model.Hovered == flat())
                    _model.SetHovered(InventoryWindowViewModel.None);
            });
            return new SlotElements { Root = root, Icon = icon, Count = countLabel };
        }

        /// <summary>The element of a flat slot.</summary>
        private SlotElements ElementOf(int flat) => flat < _model.SlotCount ? _slots[flat] : _equip[flat - _model.SlotCount];

        private void DrawSlot(SlotElements elements, int flat)
        {
            var view = _model.Slot(flat);
            SetIcon(elements.Icon, view);
            HudWidgets.SetText(elements.Count, view.Count > 1
                ? Config.Text.Get(InventoryTextIds.Count, new TextArg("count", NumberText.Of(view.Count)))
                : "");
            elements.Root.EnableInClassList("inv-slot--preview", view.Preview);
            elements.Root.EnableInClassList("inv-slot--covered", view.Covered);
            bool reserved = _model.IsReserved(flat); // WP-M3-2: a craft holds it; dimmed like a preview
            elements.Root.EnableInClassList("inv-slot--reserved", reserved);
            elements.Root.style.opacity = view.Preview || view.Covered || reserved ? 0.6f : 1f;
            if (elements.Name != null)
                elements.Name.style.display = view.IsEmpty ? DisplayStyle.Flex : DisplayStyle.None;
            bool selected = _model.Selected == flat;
            elements.Root.EnableInClassList("inv-slot--selected", selected);
            HudWidgets.Border(elements.Root, selected ? Theme.Accent : Theme.Border);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
            {
                Show(_tooltip, false);
                Show(_ghost, false);
                return;
            }
            if (_slots.Length != _model.SlotCount)
                BuildSlots(_model.SlotCount);
            foreach (var tab in _tabButtons)
            {
                bool active = (byte)tab.userData == _model.ActiveContainer;
                tab.EnableInClassList("inv-tab--active", active);
                tab.style.backgroundColor = active ? Theme.Accent : Theme.Panel;
                HudWidgets.Border(tab, active ? Theme.Accent : Theme.Border);
            }
            for (int i = 0; i < _slots.Length; i++)
                DrawSlot(_slots[i], i);
            for (int i = 0; i < _equip.Length; i++)
                DrawSlot(_equip[i], _model.SlotCount + i);
            bool splitting = _model.SplitCount > 0;
            Show(_splitBar, splitting);
            Show(_title, !splitting);
            HudWidgets.SetText(_splitText, splitting
                ? Config.Text.Get(InventoryTextIds.Split, new TextArg("count", NumberText.Of(_model.SplitCount)),
                    new TextArg("stack", NumberText.Of(_model.SplitStack)))
                : "");
            RefreshTooltip();
        }

        private void SetIcon(ItemIconElement icon, in InventorySlotView view)
        {
            if (view.IsEmpty)
            {
                icon.SetEmpty();
                return;
            }
            var info = _inventory.Item(view.ItemNetId);
            var row = info?.Icon;
            Texture2D? texture = null;
            if (row != null && row.IconAddress.Length > 0 && _icons != null
                && _icons.Get(row.IconAddress, out var loaded) == PrefabState.Ready)
                texture = loaded;
            icon.Set(row?.PlaceholderShape ?? IconShape.Square,
                row != null ? HudTheme.ColorOf(row.PlaceholderColor) : Theme.Locked, texture);
        }

        private void RefreshTooltip()
        {
            int hovered = _model.Hovered;
            if (hovered < 0 || hovered >= _slots.Length + _equip.Length || _model.Slot(hovered).IsEmpty || _dragging)
            {
                Show(_tooltip, false);
                return;
            }
            var view = _model.Slot(hovered);
            var info = _inventory.Item(view.ItemNetId);
            var text = Config.Text;
            HudWidgets.SetText(_tooltipName, info != null ? text.Get(info.Def.NameId) : "#" + view.ItemNetId);
            string rarityId = _inventory.RarityTextId(view.Rarity);
            HudWidgets.SetText(_tooltipRarity, text.Get(InventoryTextIds.TooltipRarity,
                new TextArg("rarity", rarityId.Length > 0 ? text.Get(rarityId) : NumberText.Of(view.Rarity))));
            HudWidgets.SetText(_tooltipCategory, info != null
                ? text.Get(InventoryTextIds.TooltipCategory, new TextArg("category", text.Get(UiStringCoverage.ItemCategoryId(info.Def.Category))))
                : "");
            HudWidgets.SetText(_tooltipCount, text.Get(InventoryTextIds.TooltipCount,
                new TextArg("count", NumberText.Of(view.Count)), new TextArg("max", NumberText.Of(_inventory.MaxStack(view.ItemNetId)))));
            int minLevel = info?.Def.MinLevel ?? 0;
            HudWidgets.SetText(_tooltipLevel, minLevel > 1
                ? text.Get(InventoryTextIds.TooltipMinLevel, new TextArg("level", NumberText.Of(minLevel)))
                : "");
            Show(_tooltipLevel, minLevel > 1);
            HudWidgets.SetText(_tooltipStolen, view.Stolen ? text.Get(InventoryTextIds.TooltipStolen) : "");
            Show(_tooltipStolen, view.Stolen);
            RefreshTooltipStats(view, info);
            // Left of the window (it sits on the right edge), level with the slot.
            var slot = ElementOf(hovered).Root;
            _tooltip.style.top = slot.ChangeCoordinatesTo(_window, Vector2.zero).y; // the slot sits in a row of its grid
            Show(_tooltip, true);
        }

        /// <summary>
        /// WP-M2-11 item stats: one line per stat bonus at the item's rarity ("Atk +12", "Def +5%", names <c>stat.&lt;id&gt;</c>),
        /// the weapon type, the equipment slots and "Bound" for a non-tradable item. Display only: the server applies the stats.
        /// </summary>
        private void RefreshTooltipStats(in InventorySlotView view, ItemInfo? info)
        {
            var text = Config.Text;
            // WP-M3-2: a crafted instance shows its stats snapshot instead of the item's list (spec 4.1 M3 item model).
            var entry = _model.Entry(_model.Hovered);
            var bonuses = entry != null && entry.CraftedStats.Length > 0
                ? _inventory.CraftedBonuses(entry.CraftedStats)
                : _inventory.Bonuses(view.ItemNetId, view.Rarity);
            int lines = 0;
            foreach (var bonus in bonuses)
            {
                string stat = text.Get(UiStringCoverage.StatId(bonus.StatId));
                if (bonus.Flat != 0)
                    StatLine(lines++, text.Get(InventoryTextIds.TooltipStat, new TextArg("stat", stat), new TextArg("value", Signed(bonus.Flat))));
                if (bonus.Percent != 0)
                    StatLine(lines++, text.Get(InventoryTextIds.TooltipStatPercent, new TextArg("stat", stat),
                        new TextArg("value", Signed(bonus.Percent))));
            }
            for (int i = lines; i < _tooltipStats.childCount; i++)
                Show(_tooltipStats[i], false);
            var weapon = info?.Def.WeaponType ?? WeaponType.None;
            HudWidgets.SetText(_tooltipWeapon, weapon != WeaponType.None
                ? text.Get(InventoryTextIds.TooltipWeapon, new TextArg("weapon", text.Get(SkillTreeTextIds.Weapon(weapon))))
                : "");
            Show(_tooltipWeapon, weapon != WeaponType.None);
            string slots = string.Join(", ", _inventory.EquipSlotTextIds(view.ItemNetId).Select(id => text.Get(id)));
            HudWidgets.SetText(_tooltipSlots, slots.Length > 0 ? text.Get(InventoryTextIds.TooltipSlots, new TextArg("slots", slots)) : "");
            Show(_tooltipSlots, slots.Length > 0);
            bool bound = _model.IsBound(_model.Hovered);
            HudWidgets.SetText(_tooltipBound, bound ? text.Get(InventoryTextIds.TooltipBound) : "");
            Show(_tooltipBound, bound);
            // WP-M3-2: quality and "Crafted" (the crafter's name waits for an EntityIdentity through DisplayIdentity, rule 8),
            // tainted (R-4.4-03) and reserved by a craft.
            int quality = entry?.Quality ?? 0;
            HudWidgets.SetText(_tooltipQuality, quality > 0
                ? text.Get(InventoryTextIds.TooltipQuality, new TextArg("quality", NumberText.Of(quality)))
                : "");
            Show(_tooltipQuality, quality > 0);
            bool crafted = entry != null && entry.CrafterId != 0;
            HudWidgets.SetText(_tooltipCrafted, crafted ? text.Get(InventoryTextIds.TooltipCrafted) : "");
            Show(_tooltipCrafted, crafted);
            bool tainted = entry?.Tainted == true;
            HudWidgets.SetText(_tooltipTainted, tainted ? text.Get(InventoryTextIds.TooltipTainted) : "");
            Show(_tooltipTainted, tainted);
            bool reserved = entry?.Reserved == true;
            HudWidgets.SetText(_tooltipReserved, reserved ? text.Get(InventoryTextIds.TooltipReserved) : "");
            Show(_tooltipReserved, reserved);
            bool usable = info?.Def.Category == ItemCategory.Consumable;
            HudWidgets.SetText(_tooltipUse, usable ? text.Get(InventoryTextIds.TooltipUse) : "");
            Show(_tooltipUse, usable);
        }

        private void StatLine(int index, string line)
        {
            while (_tooltipStats.childCount <= index)
                _tooltipStats.Add(HudWidgets.Label("", "inv-tooltip__line"));
            var label = (Label)_tooltipStats[index];
            HudWidgets.SetText(label, line);
            Show(label, true);
        }

        /// <summary>A bonus with its sign ("+12", "-3"); integer, culture-independent.</summary>
        private static string Signed(int value) => value >= 0 ? "+" + NumberText.Of(value) : "-" + NumberText.Of(-value);

        private void OnSlotDown(int slot, PointerDownEvent e)
        {
            // WP-M3-3: right-click, or a double-click, uses the stack (a consumable; the server refuses anything else).
            if (e.button == 1 || (e.button == 0 && e.clickCount == 2 && !e.shiftKey))
            {
                EndPress();
                _actions.Use(slot);
                e.StopPropagation();
                return;
            }
            if (e.button != 0)
                return;
            _pressSlot = slot;
            _pressShift = e.shiftKey; // Shift+click sets up a split (WP-M2-11)
            _pressAt = new Vector2(e.position.x, e.position.y);
            _dragging = false;
            _window.CapturePointer(e.pointerId);
            e.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            if (_pressSlot == InventoryWindowViewModel.None)
                return;
            var at = new Vector2(e.position.x, e.position.y);
            float threshold = _inventory.Settings.DragThresholdPx;
            if (!_dragging && (at - _pressAt).sqrMagnitude >= threshold * threshold && !_model.Slot(_pressSlot).IsEmpty)
            {
                _dragging = true;
                SetIcon(_ghost, _model.Slot(_pressSlot));
                Show(_ghost, true);
                Show(_tooltip, false);
            }
            if (_dragging)
            {
                var local = View.Root.WorldToLocal(at);
                float half = _inventory.Settings.SlotSizePx / 2f;
                _ghost.style.left = local.x - half;
                _ghost.style.top = local.y - half;
            }
        }

        private void OnPointerUp(PointerUpEvent e)
        {
            if (_pressSlot == InventoryWindowViewModel.None || e.button != 0)
                return;
            int from = _pressSlot;
            bool dragged = _dragging;
            bool shift = _pressShift;
            var at = new Vector2(e.position.x, e.position.y);
            int target = SlotAt(at);
            bool inside = _window.worldBound.Contains(at);
            if (_window.HasPointerCapture(e.pointerId))
                _window.ReleasePointer(e.pointerId); // ends the press (PointerCaptureOut)
            EndPress();
            if (dragged)
            {
                if (target >= 0)
                    _actions.Drop(from, target);
                else if (!inside)
                    _actions.DropOutside(from, at);
            }
            else if (target == from && shift && _model.SplitCount == 0)
            {
                _actions.BeginSplit(from);
            }
            else if (target == from)
            {
                _actions.Click(from);
            }
        }

        private void EndPress()
        {
            _pressSlot = InventoryWindowViewModel.None;
            if (_dragging)
            {
                _dragging = false;
                Show(_ghost, false);
                RefreshTooltip();
            }
        }

        private int SlotAt(Vector2 panelPosition)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Root.worldBound.Contains(panelPosition))
                    return i;
            }
            for (int i = 0; i < _equip.Length; i++)
            {
                if (_equip[i].Root.worldBound.Contains(panelPosition))
                    return _model.SlotCount + i;
            }
            return InventoryWindowViewModel.None;
        }
    }

    /// <summary>
    /// An item icon: the <c>ItemIconRegistry</c> texture when one is loaded, otherwise the placeholder shape in its color,
    /// drawn with vector paths (no art needed), with a dark outline so it reads on any slot.
    /// </summary>
    public sealed class ItemIconElement : VisualElement
    {
        public ItemIconElement()
        {
            generateVisualContent += Draw;
        }

        public IconShape Shape { get; private set; }

        public Color Color { get; private set; }

        public Texture2D? Texture { get; private set; }

        /// <summary>Nothing is drawn (an empty slot).</summary>
        public bool IsEmpty { get; private set; } = true;

        public void Set(IconShape shape, Color color, Texture2D? texture)
        {
            if (!IsEmpty && shape == Shape && color == Color && texture == Texture)
                return;
            IsEmpty = false;
            Shape = shape;
            Color = color;
            Texture = texture;
            style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
            MarkDirtyRepaint();
        }

        public void SetEmpty()
        {
            if (IsEmpty)
                return;
            IsEmpty = true;
            Texture = null;
            style.backgroundImage = new StyleBackground(StyleKeyword.None);
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (IsEmpty || Texture != null || rect.width <= 0f || rect.height <= 0f)
                return;
            var paint = context.painter2D;
            float w = rect.width, h = rect.height;
            float r = Mathf.Min(w, h) * 0.38f;
            var c = new Vector2(w * 0.5f, h * 0.5f);
            paint.fillColor = Color;
            paint.strokeColor = new Color(0f, 0f, 0f, 0.75f);
            paint.lineWidth = Mathf.Max(1f, r * 0.12f);
            paint.BeginPath();
            switch (Shape)
            {
                case IconShape.Circle:
                    paint.Arc(c, r, 0f, 360f);
                    break;
                case IconShape.Square:
                    Polygon(paint, c, r * 1.15f, 4, 45f);
                    break;
                case IconShape.Diamond:
                    Polygon(paint, c, r * 1.1f, 4, 0f);
                    break;
                case IconShape.Triangle:
                    Polygon(paint, c + new Vector2(0f, r * 0.15f), r * 1.15f, 3, 0f);
                    break;
                default:
                    Polygon(paint, c, r, 6, 30f);
                    break;
            }
            paint.ClosePath();
            paint.Fill();
            paint.Stroke();
        }

        /// <summary>A regular polygon around <paramref name="center"/>; the first corner at <paramref name="startDeg"/> from straight up.</summary>
        private static void Polygon(Painter2D paint, Vector2 center, float radius, int corners, float startDeg)
        {
            for (int i = 0; i < corners; i++)
            {
                float angle = (startDeg + i * 360f / corners) * Mathf.Deg2Rad;
                var point = center + new Vector2(Mathf.Sin(angle) * radius, -Mathf.Cos(angle) * radius);
                if (i == 0)
                    paint.MoveTo(point);
                else
                    paint.LineTo(point);
            }
        }
    }
}
