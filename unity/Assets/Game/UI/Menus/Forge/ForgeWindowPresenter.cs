#nullable enable
using System.Collections.Generic;
using System.Globalization;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Forge;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Forge
{
    /// <summary>
    /// Spec 9.13 <c>forge_window</c> (spec 5.3; WP-M3-6): the forge window v1, placed by its <c>HudLayout</c> row and opened by a
    /// station's yes. The station's molds and recipes (each with its job level), the chosen one's inputs (the bag stack each
    /// takes, the next candidate on request) and its start button; during a session the forge action buttons with the count,
    /// the quality range the server sends (never a rolled number), and finish and cancel. Text from <c>UiStrings</c> and
    /// <c>ReasonCodeStrings</c>; the server decides every input and number.
    /// </summary>
    public sealed class ForgeWindowPresenter : HudPresenter
    {
        private readonly ForgeConfig _forge;
        private readonly ForgeWindowViewModel _model;
        private readonly IForgeWindowActions _actions;
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<(Label Text, Button Next)> _inputs = new List<(Label, Button)>();
        private readonly List<Button> _actionButtons = new List<Button>();
        private VisualElement _window = null!;
        private VisualElement _list = null!;
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
        private Button? _tabForge;
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

        public Button? EnchantTab => _tabEnchant;

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        public int RowCount => _rows.Count;

        public Button Row(int index) => _rows[index];

        public string InputText(int index) => _inputs[index].Text.text;

        public Button StartButton => _start;

        public Button FinishButton => _finish;

        public Button CancelButton => _cancel;

        public Button ActionButton(int index) => _actionButtons[index];

        public bool SessionShown => _session.style.display == DisplayStyle.Flex;

        /// <summary>The session panel, where the HUD root mounts the forge timing bar (WP-M3-10, <see cref="ForgeBarPresenter"/>).</summary>
        public VisualElement SessionPanel => _session;

        public string QualityText => _quality.text;

        public string ActionsText => _actionsText.text;

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
            _window.Add(header);
            if (_enchant != null)
            {
                // WP-M3-8: the forge and enchant tabs of the station window.
                var tabs = Row("forge-tabs");
                _tabForge = Button("forge_tab_forge", Config.Text.Get(EnchantTextIds.TabForge), () => _enchant.Actions.ShowEnchantTab(false));
                _tabEnchant = Button("forge_tab_enchant", Config.Text.Get(EnchantTextIds.TabEnchant), () => _enchant.Actions.ShowEnchantTab(true));
                tabs.Add(_tabForge);
                tabs.Add(_tabEnchant);
                _window.Add(tabs);
            }

            _list = HudWidgets.Box("forge-list");
            _list.pickingMode = PickingMode.Position;
            _window.Add(_list);
            _empty = Text(Config.Text.Get(ForgeTextIds.Empty), "forge-empty");
            _window.Add(_empty);

            _inputBox = HudWidgets.Box("forge-inputs");
            _inputBox.pickingMode = PickingMode.Position;
            _inputBox.style.marginTop = 6;
            _window.Add(_inputBox);
            _start = Button("forge_start", Config.Text.Get(ForgeTextIds.Start), () => _actions.StartForge());
            _window.Add(_start);

            _session = HudWidgets.Box("forge-session");
            _session.pickingMode = PickingMode.Position;
            _session.style.marginTop = 6;
            _actionsText = Text("", "forge-actions");
            _session.Add(_actionsText);
            _actionRow = Row("forge-action-row");
            _session.Add(_actionRow);
            _quality = Text("", "forge-quality");
            _session.Add(_quality);
            var buttons = Row("forge-session__buttons");
            _finish = Button("forge_finish", Config.Text.Get(ForgeTextIds.Finish), () => _actions.FinishForge());
            _cancel = Button("forge_cancel", Config.Text.Get(ForgeTextIds.Cancel), () => _actions.CancelForge());
            buttons.Add(_finish);
            buttons.Add(_cancel);
            _session.Add(buttons);
            _window.Add(_session);

            _waiting = Text(Config.Text.Get(ForgeTextIds.Waiting), "forge-waiting");
            _window.Add(_waiting);
            if (_enchant != null)
                _window.Add(_enchant.Build());
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
                return;
            if (_enchant != null)
            {
                bool enchantTab = _enchant.Model.Active;
                HudWidgets.Border(_tabForge!, enchantTab ? Theme.Border : Theme.Accent);
                HudWidgets.Border(_tabEnchant!, enchantTab ? Theme.Accent : Theme.Border);
                _enchant.Refresh(enchantTab);
                Show(_list, !enchantTab);
                Show(_inputBox, !enchantTab);
                if (enchantTab)
                {
                    Show(_empty, false);
                    Show(_start, false);
                    Show(_session, false);
                    Show(_waiting, false);
                    return;
                }
            }
            RefreshRows();
            RefreshInputs();
            bool session = _model.InSession;
            Show(_start, !session && _model.Definition != null);
            _start.SetEnabled(_model.CanStart);
            Show(_session, session);
            Show(_waiting, _model.Finishing || _model.PendingStart != 0);
            if (session)
                RefreshSession();
        }

        private void RefreshRows()
        {
            var definitions = _model.Definitions;
            while (_rows.Count < definitions.Count)
            {
                int index = _rows.Count;
                var row = Button("forge_row_" + index, "", () => _actions.Choose(index));
                row.style.unityTextAlign = UnityEngine.TextAnchor.MiddleLeft;
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
            var definition = _model.Definition;
            int count = definition?.Inputs.Count ?? 0;
            while (_inputs.Count < count)
            {
                int index = _inputs.Count;
                var line = Row("forge-input");
                var text = Text("", "forge-input__text");
                var next = Button("forge_next_" + index, Config.Text.Get(ForgeTextIds.Next), () => _actions.NextInput(index));
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
                var button = Button("forge_action_" + index, "", () => _actions.UseAction(index));
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
            var button = new Button(onClick) { name = name, text = text };
            button.style.color = Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(button, Theme.Border);
            return button;
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
