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
    /// <c>ReasonCodeStrings</c>; the server decides every input and number. The window has the fixed size of its row: the list
    /// is a scroll view that takes the height left, and the header, the inputs, Start and the session panel never shrink, so a
    /// longer list scrolls instead of running under the session controls (the vendor window's pattern; wave 1 QA F2).
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

        public ForgeWindowPresenter(HudConfig config, HudView view, ForgeConfig forge, ForgeWindowViewModel model, IForgeWindowActions actions)
            : base(config, view, model)
        {
            _forge = forge;
            _model = model;
            _actions = actions;
        }

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

        public string QualityText => _quality.text;

        public string ActionsText => _actionsText.text;

        /// <summary>The part of the list the player sees, the inputs box and the session panel, for layout tests.</summary>
        public VisualElement ListViewport => _list.contentViewport;

        public ScrollView List => _list;

        public VisualElement InputBox => _inputBox;

        public VisualElement SessionPanel => _session;

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
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
                return;
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
                var row = Fixed(Compact(Button("forge_row_" + index, "", () => _actions.Choose(index))));
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
