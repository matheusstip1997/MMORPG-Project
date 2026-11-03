#nullable enable
using System.Collections.Generic;
using System.Globalization;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Forge;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Forge
{
    /// <summary>
    /// Spec 9.13 <c>forge_window</c> (spec 5.3; WP-M3-6, Forge v2 WP-M3-14): the station window, placed by its <c>HudLayout</c>
    /// row and opened by a station's yes, with three tabs. <b>Forge</b>: a mold slot (a mold from the Safe bag's equipment
    /// area), one slot per material slot of that mold (a Normal bag stack each, dragged from the inventory window onto the slot
    /// or chosen in the slot's picker on a click; X empties it), the stat ranges the chosen materials can reach (never a rolled
    /// number) and Start. <b>Recipes</b>: the station's refine, convert and mold-making recipes with their job level in a scroll
    /// view that takes the height left, the chosen one's inputs (the next candidate on request) and Start (the WP-M3-6 list).
    /// <b>Enchant</b>: WP-M3-8's panel. During a session the forge action buttons with the count, the quality range the server
    /// sends, the stat ranges it allows, and Finish and Cancel. Text from <c>UiStrings</c> and <c>ReasonCodeStrings</c>; the
    /// server decides every input and number. The window has the fixed size of its row: the tab's content scrolls, the header,
    /// the tabs, Start and the session panel never shrink (the vendor window's pattern; wave 1 QA F2).
    /// </summary>
    public sealed class ForgeWindowPresenter : HudPresenter
    {
        private readonly ForgeConfig _forge;
        private readonly ForgeWindowViewModel _model;
        private readonly IForgeWindowActions _actions;
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<(Label Text, Button Next)> _inputs = new List<(Label, Button)>();
        private readonly List<(Button Slot, Button Clear)> _slots = new List<(Button, Button)>();
        private readonly List<Button> _pickerEntries = new List<Button>();
        private readonly List<Label> _statLines = new List<Label>();
        private readonly List<Button> _actionButtons = new List<Button>();
        private VisualElement _window = null!;
        private ScrollView _forgePanel = null!;
        private Button _moldSlot = null!;
        private VisualElement _slotBox = null!;
        private VisualElement _picker = null!;
        private Label _pickerTitle = null!;
        private Label _pickerEmpty = null!;
        private VisualElement _stats = null!;
        private ScrollView _list = null!;
        private Label _empty = null!;
        private VisualElement _inputBox = null!;
        private Button _start = null!;
        private VisualElement _session = null!;
        private VisualElement _actionRow = null!;
        private Label _actionsText = null!;
        private Label _quality = null!;
        private Button _finish = null!;
        private Button _cancel = null!;
        private Label _waiting = null!;
        private bool _pointerInside;
        private readonly EnchantPanel? _enchant;
        private Button _tabForge = null!;
        private Button _tabRecipes = null!;
        private Button? _tabEnchant;

        public ForgeWindowPresenter(HudConfig config, HudView view, ForgeConfig forge, ForgeWindowViewModel model, IForgeWindowActions actions,
            EnchantPanel? enchant = null)
            : base(config, view, enchant != null ? new ViewModels.ViewModel[] { model, enchant.Model } : new ViewModels.ViewModel[] { model })
        {
            _forge = forge;
            _model = model;
            _actions = actions;
            _enchant = enchant;
        }

        /// <summary>The enchant tab drawn inside this window (WP-M3-8); null when the window was built without it.</summary>
        public EnchantPanel? Enchant => _enchant;

        public Button? ForgeTab => _tabForge;

        public Button RecipesTab => _tabRecipes;

        public Button? EnchantTab => _tabEnchant;

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        /// <summary>The Recipes tab's rows (one per recipe of the station).</summary>
        public int RowCount => _rows.Count;

        public Button Row(int index) => _rows[index];

        /// <summary>The Recipes tab's input line <paramref name="index"/>.</summary>
        public string InputText(int index) => _inputs[index].Text.text;

        /// <summary>The Forge tab's mold slot (a drop target and its picker's button).</summary>
        public Button MoldSlot => _moldSlot;

        /// <summary>The Forge tab's material slot <paramref name="index"/> (a drop target and its picker's button).</summary>
        public Button SlotButton(int index) => _slots[index].Slot;

        public Button ClearButton(int index) => _slots[index].Clear;

        public bool PickerShown => _picker.style.display == DisplayStyle.Flex;

        public int PickerCount => _pickerEntries.FindAll(b => b.style.display == DisplayStyle.Flex).Count;

        public Button PickerEntry(int index) => _pickerEntries[index];

        public Button PickerCancel => _picker.Q<Button>("forge_pick_cancel");

        /// <summary>The stat preview lines shown, one per line, or "".</summary>
        public string StatsText => string.Join("\n", _statLines.FindAll(l => l.style.display == DisplayStyle.Flex).ConvertAll(l => l.text));

        public Button StartButton => _start;

        public Button FinishButton => _finish;

        public Button CancelButton => _cancel;

        public Button ActionButton(int index) => _actionButtons[index];

        public bool SessionShown => _session.style.display == DisplayStyle.Flex;

        /// <summary>The session panel, where the HUD root mounts the forge timing bar (WP-M3-10, <see cref="ForgeBarPresenter"/>).</summary>
        public VisualElement SessionPanel => _session;

        public string QualityText => _quality.text;

        public string ActionsText => _actionsText.text;

        /// <summary>The part of the recipe list the player sees, for layout tests.</summary>
        public VisualElement ListViewport => _list.contentViewport;

        public ScrollView List => _list;

        /// <summary>The part of the Forge tab the player sees, for layout tests.</summary>
        public VisualElement ForgeViewport => _forgePanel.contentViewport;

        public VisualElement InputBox => _inputBox;

        /// <summary>
        /// A stack dragged out of the inventory window and released at <paramref name="panelPosition"/> (WP-M3-14): on the mold
        /// slot or a material slot of the open Forge tab it is that slot's (the stack is selected, never moved). False when the
        /// position is on no forge slot, so the inventory window handles it.
        /// </summary>
        public bool DropAt(Vector2 panelPosition, byte container, InventorySlotEntry entry)
        {
            if (!_model.Open || _model.Tab != Game.UI.ViewModels.Forge.ForgeTab.Forge || (_enchant?.Model.Active ?? false) || !IsShown)
                return false;
            if (_moldSlot.worldBound.Contains(panelPosition))
            {
                _actions.DropOnMold(container, entry);
                return true;
            }
            int count = _model.Mold?.Inputs.Count ?? 0;
            for (int i = 0; i < count && i < _slots.Count; i++)
            {
                if (_slots[i].Slot.worldBound.Contains(panelPosition))
                {
                    _actions.DropOnInput(i, container, entry);
                    return true;
                }
            }
            return false;
        }

        protected override void Build()
        {
            _window = View.Place(HudElementIds.ForgeWindow, HudWidgets.Panel("forge-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.style.paddingBottom = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            var header = Row("forge-header");
            header.style.justifyContent = Justify.SpaceBetween;
            var title = HudWidgets.Label(Config.Text.Get(ForgeTextIds.Title), "forge-title");
            title.style.color = Theme.Accent;
            header.Add(title);
            var close = Button("forge_close", Config.Text.Get(ForgeTextIds.Close), () => _actions.CloseForge());
            close.tooltip = Config.Text.Get(ForgeTextIds.CloseLabel);
            header.Add(close);
            header.style.flexShrink = 0;
            _window.Add(header);

            // The tabs: Forge and Recipes (WP-M3-14), Enchant (WP-M3-8).
            var tabs = Fixed(Row("forge-tabs"));
            _tabForge = Compact(Button("forge_tab_forge", Config.Text.Get(EnchantTextIds.TabForge), () => ShowTab(Game.UI.ViewModels.Forge.ForgeTab.Forge)));
            _tabRecipes = Compact(Button("forge_tab_recipes", Config.Text.Get(ForgeTextIds.TabRecipes), () => ShowTab(Game.UI.ViewModels.Forge.ForgeTab.Recipes)));
            tabs.Add(_tabForge);
            tabs.Add(_tabRecipes);
            if (_enchant != null)
            {
                _tabEnchant = Compact(Button("forge_tab_enchant", Config.Text.Get(EnchantTextIds.TabEnchant), () => _enchant.Actions.ShowEnchantTab(true)));
                tabs.Add(_tabEnchant);
            }
            _window.Add(tabs);

            BuildForgePanel();

            _list = new ScrollView(ScrollViewMode.Vertical) { name = "forge_list" };
            _list.AddToClassList("forge-list");
            _list.style.flexGrow = 1;
            _list.style.flexShrink = 1;
            _list.pickingMode = PickingMode.Position;
            _window.Add(_list);
            _empty = Fixed(Text(Config.Text.Get(ForgeTextIds.Empty), "forge-empty"));
            _window.Add(_empty);

            _inputBox = Fixed(HudWidgets.Box("forge-inputs"));
            _inputBox.pickingMode = PickingMode.Position;
            _inputBox.style.marginTop = 6;
            _window.Add(_inputBox);
            _start = Fixed(Compact(Button("forge_start", Config.Text.Get(ForgeTextIds.Start), () => _actions.StartForge())));
            _window.Add(_start);

            _session = Fixed(HudWidgets.Box("forge-session"));
            _session.pickingMode = PickingMode.Position;
            _session.style.marginTop = 6;
            _actionsText = Text("", "forge-actions");
            _session.Add(_actionsText);
            _actionRow = Row("forge-action-row");
            _session.Add(_actionRow);
            _quality = Text("", "forge-quality");
            _session.Add(_quality);
            var buttons = Row("forge-session__buttons");
            _finish = Compact(Button("forge_finish", Config.Text.Get(ForgeTextIds.Finish), () => _actions.FinishForge()));
            _cancel = Compact(Button("forge_cancel", Config.Text.Get(ForgeTextIds.Cancel), () => _actions.CancelForge()));
            buttons.Add(_finish);
            buttons.Add(_cancel);
            _session.Add(buttons);
            _window.Add(_session);

            _waiting = Fixed(Text(Config.Text.Get(ForgeTextIds.Waiting), "forge-waiting"));
            _window.Add(_waiting);
            if (_enchant != null)
                _window.Add(_enchant.Build());
        }

        /// <summary>The Forge tab: the mold slot, the material slots, their picker and the stat preview, in one scroll view.</summary>
        private void BuildForgePanel()
        {
            _forgePanel = new ScrollView(ScrollViewMode.Vertical) { name = "forge_panel" };
            _forgePanel.style.flexGrow = 1;
            _forgePanel.style.flexShrink = 1;
            _forgePanel.pickingMode = PickingMode.Position;
            _window.Add(_forgePanel);

            _moldSlot = Compact(Button("forge_mold_slot", "", () => _actions.Pick(ForgeWindowViewModel.PickMold)));
            _moldSlot.style.unityTextAlign = TextAnchor.MiddleLeft;
            _moldSlot.style.whiteSpace = WhiteSpace.Normal;
            _forgePanel.Add(_moldSlot);
            _slotBox = HudWidgets.Box("forge-slots");
            _slotBox.pickingMode = PickingMode.Position;
            _forgePanel.Add(_slotBox);

            _picker = HudWidgets.Box("forge-picker");
            _picker.pickingMode = PickingMode.Position;
            _picker.style.marginTop = 4;
            HudWidgets.Border(_picker, Theme.Accent);
            _pickerTitle = Text("", "forge-picker__title");
            _pickerTitle.style.color = Theme.Accent;
            _picker.Add(_pickerTitle);
            _pickerEmpty = Text("", "forge-picker__empty");
            _picker.Add(_pickerEmpty);
            var cancel = Compact(Button("forge_pick_cancel", Config.Text.Get(ForgeTextIds.PickCancel), () => _actions.ClosePicker()));
            _picker.Add(cancel);
            _forgePanel.Add(_picker);

            _stats = HudWidgets.Box("forge-stats");
            _stats.pickingMode = PickingMode.Ignore;
            _stats.style.marginTop = 4;
            var statsTitle = Text(Config.Text.Get(ForgeTextIds.StatsTitle), "forge-stats__title");
            statsTitle.style.color = Theme.Accent;
            _stats.Add(statsTitle);
            _forgePanel.Add(_stats);
        }

        private void ShowTab(Game.UI.ViewModels.Forge.ForgeTab tab)
        {
            _enchant?.Actions.ShowEnchantTab(false);
            _actions.ShowTab(tab);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
                return;
            bool enchantTab = _enchant?.Model.Active ?? false;
            bool forgeTab = !enchantTab && _model.Tab == Game.UI.ViewModels.Forge.ForgeTab.Forge;
            bool recipesTab = !enchantTab && _model.Tab == Game.UI.ViewModels.Forge.ForgeTab.Recipes;
            HudWidgets.Border(_tabForge, forgeTab ? Theme.Accent : Theme.Border);
            HudWidgets.Border(_tabRecipes, recipesTab ? Theme.Accent : Theme.Border);
            if (_tabEnchant != null)
                HudWidgets.Border(_tabEnchant, enchantTab ? Theme.Accent : Theme.Border);
            _enchant?.Refresh(enchantTab);
            Show(_forgePanel, forgeTab);
            Show(_list, recipesTab);
            Show(_inputBox, recipesTab);
            if (enchantTab)
            {
                Show(_empty, false);
                Show(_start, false);
                Show(_session, false);
                Show(_waiting, false);
                return;
            }
            if (forgeTab)
            {
                Show(_empty, false);
                RefreshForge();
            }
            else
            {
                RefreshRows();
                RefreshInputs();
            }
            bool session = _model.InSession;
            Show(_start, !session && _model.Definition != null);
            _start.SetEnabled(_model.CanStart);
            Show(_session, session);
            Show(_waiting, _model.Finishing || _model.PendingStart != 0);
            if (session)
                RefreshSession();
        }

        private void RefreshForge()
        {
            bool session = _model.InSession;
            var mold = _model.Mold;
            _moldSlot.text = mold != null
                ? Config.Text.Get(ForgeTextIds.MoldSlot, new TextArg("item", ItemName(mold.MoldItemId)))
                : Config.Text.Get(ForgeTextIds.MoldEmpty);
            HudWidgets.Border(_moldSlot, _model.PickerTarget == ForgeWindowViewModel.PickMold ? Theme.Accent : Theme.Border);
            _moldSlot.SetEnabled(!session);

            int count = mold?.Inputs.Count ?? 0;
            while (_slots.Count < count)
            {
                int index = _slots.Count;
                var line = Row("forge-slot");
                line.style.alignItems = Align.Center;
                var slot = Compact(Button("forge_slot_" + index, "", () => _actions.Pick(index)));
                slot.style.unityTextAlign = TextAnchor.MiddleLeft;
                slot.style.whiteSpace = WhiteSpace.Normal;
                slot.style.flexGrow = 1;
                slot.style.flexShrink = 1;
                var clear = Compact(Button("forge_clear_" + index, Config.Text.Get(ForgeTextIds.Clear), () => _actions.ClearInput(index)));
                clear.tooltip = Config.Text.Get(ForgeTextIds.ClearLabel);
                clear.style.flexShrink = 0;
                line.Add(slot);
                line.Add(clear);
                _slotBox.Add(line);
                _slots.Add((slot, clear));
            }
            bool picking = _model.PickerTarget != ForgeWindowViewModel.PickNone && !session;
            for (int i = 0; i < _slots.Count; i++)
            {
                bool used = i < count;
                Show(_slots[i].Slot.parent, used);
                if (!used)
                    continue;
                var spec = mold!.Inputs[i];
                string name = Config.Text.Get(spec.NameId);
                var chosen = _model.Chosen(i);
                _slots[i].Slot.text = chosen != null
                    ? Config.Text.Get(ForgeTextIds.Input, new TextArg("slot", name), new TextArg("item", ItemName(chosen.ItemNetId)),
                        new TextArg("count", Number(spec.Count)))
                    : Config.Text.Get(ForgeTextIds.SlotEmpty, new TextArg("slot", name));
                HudWidgets.Border(_slots[i].Slot, _model.PickerTarget == i ? Theme.Accent : Theme.Border);
                _slots[i].Slot.SetEnabled(!session);
                _slots[i].Clear.SetEnabled(!session && chosen != null);
            }
            RefreshPicker(picking, mold);
            RefreshStats(!picking);
        }

        private void RefreshPicker(bool shown, ForgeDefinition? mold)
        {
            Show(_picker, shown);
            if (!shown)
                return;
            var texts = new List<string>();
            int target = _model.PickerTarget;
            if (target == ForgeWindowViewModel.PickMold)
            {
                HudWidgets.SetText(_pickerTitle, Config.Text.Get(ForgeTextIds.PickMold));
                foreach (int index in _model.MoldCandidates())
                    texts.Add(OutputName(_model.Molds[index]) + "   " + ItemName(_model.Molds[index].MoldItemId));
                HudWidgets.SetText(_pickerEmpty, Config.Text.Get(ForgeTextIds.PickMoldsEmpty));
            }
            else
            {
                string slotName = mold != null && target >= 0 && target < mold.Inputs.Count ? Config.Text.Get(mold.Inputs[target].NameId) : "";
                HudWidgets.SetText(_pickerTitle, Config.Text.Get(ForgeTextIds.PickTitle, new TextArg("slot", slotName)));
                foreach (var entry in _model.Candidates(target))
                    texts.Add(Config.Text.Get(ForgeTextIds.PickEntry, new TextArg("item", ItemName(entry.ItemNetId)),
                        new TextArg("count", Number(entry.Count))));
                HudWidgets.SetText(_pickerEmpty, Config.Text.Get(ForgeTextIds.PickEmpty));
            }
            while (_pickerEntries.Count < texts.Count)
            {
                int index = _pickerEntries.Count;
                var button = Compact(Button("forge_pick_" + index, "", () => _actions.PickEntry(index)));
                button.style.unityTextAlign = TextAnchor.MiddleLeft;
                _picker.Insert(1 + index, button); // under the title, above the empty text and Cancel
                _pickerEntries.Add(button);
            }
            for (int i = 0; i < _pickerEntries.Count; i++)
            {
                bool used = i < texts.Count;
                Show(_pickerEntries[i], used);
                if (used)
                    _pickerEntries[i].text = texts[i];
            }
            Show(_pickerEmpty, texts.Count == 0);
        }

        /// <summary>The stat ranges of the chosen materials (spec 5.3 M3 decision 13): what the item can still end with.</summary>
        private void RefreshStats(bool allowed)
        {
            var ranges = allowed ? _model.StatPreview() : new List<CraftedStatRange>();
            var lines = new List<string>();
            foreach (var range in ranges)
            {
                var stat = new TextArg("stat", Config.Text.Get(UiStringCoverage.StatId(range.StatId)));
                if (range.FlatLow != 0 || range.FlatHigh != 0)
                    lines.Add(Config.Text.Get(ForgeTextIds.StatRange, stat, new TextArg("low", Number(range.FlatLow)),
                        new TextArg("high", Number(range.FlatHigh))));
                if (range.PercentLow != 0 || range.PercentHigh != 0)
                    lines.Add(Config.Text.Get(ForgeTextIds.StatRangePercent, stat, new TextArg("low", Number(range.PercentLow)),
                        new TextArg("high", Number(range.PercentHigh))));
            }
            while (_statLines.Count < lines.Count)
            {
                var label = Text("", "forge-stats__line");
                _stats.Add(label);
                _statLines.Add(label);
            }
            for (int i = 0; i < _statLines.Count; i++)
            {
                bool used = i < lines.Count;
                Show(_statLines[i], used);
                if (used)
                    HudWidgets.SetText(_statLines[i], lines[i]);
            }
            Show(_stats, lines.Count > 0);
        }

        private void RefreshRows()
        {
            var definitions = _model.Recipes;
            while (_rows.Count < definitions.Count)
            {
                int index = _rows.Count;
                var row = Fixed(Compact(Button("forge_row_" + index, "", () => _actions.Choose(index))));
                row.style.unityTextAlign = TextAnchor.MiddleLeft;
                _rows.Add(row);
                _list.Add(row);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = i < definitions.Count;
                Show(_rows[i], used);
                if (!used)
                    continue;
                var definition = definitions[i];
                _rows[i].text = OutputName(definition) + "   "
                    + Config.Text.Get(ForgeTextIds.MinLevel, new TextArg("level", Number(definition.MinJobLevel)));
                HudWidgets.Border(_rows[i], i == _model.Selected ? Theme.Accent : Theme.Border);
                _rows[i].SetEnabled(!_model.InSession);
            }
            Show(_empty, definitions.Count == 0);
        }

        private void RefreshInputs()
        {
            var definition = _model.Recipe;
            int count = definition?.Inputs.Count ?? 0;
            while (_inputs.Count < count)
            {
                int index = _inputs.Count;
                var line = Row("forge-input");
                var text = Text("", "forge-input__text");
                var next = Compact(Button("forge_next_" + index, Config.Text.Get(ForgeTextIds.Next), () => _actions.NextInput(index)));
                text.style.flexGrow = 1;
                text.style.flexShrink = 1;
                next.style.flexShrink = 0;
                line.style.alignItems = Align.Center;
                line.Add(text);
                line.Add(next);
                _inputBox.Add(line);
                _inputs.Add((text, next));
            }
            for (int i = 0; i < _inputs.Count; i++)
            {
                bool used = i < count;
                Show(_inputs[i].Text.parent, used);
                if (!used)
                    continue;
                var spec = definition!.Inputs[i];
                string slot = spec.NameId.Length > 0 ? Config.Text.Get(spec.NameId) : Config.Text.Get(ForgeTextIds.RecipeSlot);
                string countText = Number(spec.Count);
                string text = _model.Chosen(i) is { } chosen
                    ? Config.Text.Get(ForgeTextIds.Input, new TextArg("slot", slot), new TextArg("item", ItemName(chosen.ItemNetId)),
                        new TextArg("count", countText))
                    : Config.Text.Get(ForgeTextIds.InputMissing, new TextArg("slot", slot), new TextArg("count", countText));
                HudWidgets.SetText(_inputs[i].Text, text);
                _inputs[i].Next.SetEnabled(!_model.InSession && _model.CandidateCount(i) > 1);
            }
        }

        private void RefreshSession()
        {
            var session = _model.Session!;
            var definition = _model.Definition;
            int actions = definition?.Actions.Count ?? 0;
            while (_actionButtons.Count < actions)
            {
                int index = _actionButtons.Count;
                var button = Compact(Button("forge_action_" + index, "", () => _actions.UseAction(index)));
                _actionButtons.Add(button);
                _actionRow.Add(button);
            }
            for (int i = 0; i < _actionButtons.Count; i++)
            {
                bool used = i < actions;
                Show(_actionButtons[i], used);
                if (!used)
                    continue;
                _actionButtons[i].text = Config.Text.Get(definition!.Actions[i].NameId);
                _actionButtons[i].SetEnabled(_model.CanAct);
            }
            Show(_actionsText, actions > 0);
            HudWidgets.SetText(_actionsText, Config.Text.Get(ForgeTextIds.Actions, new TextArg("used", Number(_model.ActionsUsed)),
                new TextArg("max", Number(_model.MaxActions))));
            bool quality = definition != null && definition.RollsQuality;
            Show(_quality, quality);
            if (quality)
                HudWidgets.SetText(_quality, Config.Text.Get(ForgeTextIds.Quality, new TextArg("min", Number(session.QualityMin)),
                    new TextArg("max", Number(session.QualityMax)), new TextArg("low", Rarity(session.RarityMin)),
                    new TextArg("high", Rarity(session.RarityMax))));
            _finish.SetEnabled(!_model.Finishing);
            _cancel.SetEnabled(!_model.Finishing);
        }

        /// <summary>What a definition makes, by its item's name.</summary>
        public string OutputName(ForgeDefinition definition) =>
            definition.Output != null ? Config.Text.Get(definition.Output.NameId) : definition.Id;

        private string ItemName(int netId) => _forge.Rules?.Item(netId) is { } item ? Config.Text.Get(item.NameId) : Number(netId);

        private string ItemName(string itemId) => _forge.Rules?.Item(itemId) is { } item ? Config.Text.Get(item.NameId) : itemId;

        private string Rarity(int tier)
        {
            string id = _forge.RarityNameId(tier);
            return id.Length > 0 ? Config.Text.Get(id) : Number(tier);
        }

        private VisualElement Row(string className)
        {
            var row = HudWidgets.Box(className);
            row.style.flexDirection = FlexDirection.Row;
            row.pickingMode = PickingMode.Position;
            return row;
        }

        private Label Text(string text, string className)
        {
            var label = HudWidgets.Label(text, className);
            label.style.color = Theme.Text;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private Button Button(string name, string text, System.Action onClick)
        {
            var button = new Button(onClick) { focusable = false, name = name, text = text };
            button.style.color = Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(button, Theme.Border);
            return button;
        }

        /// <summary>A section of the window column that keeps its height (only the list shrinks and scrolls).</summary>
        private static T Fixed<T>(T element) where T : VisualElement
        {
            element.style.flexShrink = 0;
            return element;
        }

        /// <summary>A compact button (the theme button's 10 px padding makes rows 37 px tall), as the vendor window's rows.</summary>
        private static Button Compact(Button button)
        {
            button.style.paddingTop = 4;
            button.style.paddingBottom = 4;
            return button;
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
