#nullable enable
using System;
using System.Collections.Generic;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Contracts;
using Game.UI.ViewModels.Hud;
using Shared.Protocol;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Contracts
{
    /// <summary>
    /// Spec 9.13 <c>contract_window</c>, the C window (spec 5.4, 9.3 C; M4 decisions 10-12; WP-M4-10 part B; placeholder art, WP-M4-11
    /// polishes it), placed by its <c>HudLayout</c> row. The header with the title and close; the tabs My contracts and Propose.
    /// My contracts: one row per own contract (the role, what it makes, the gold, the status with the time left, one line per slot)
    /// with Cancel when the player may cancel (its cost shown for the crafter after work started) and the crafter's portfolio for a
    /// customer. Propose: the action stepped with previous and next, one row per input (who supplies it; the customer's stack chosen
    /// in a picker of the Normal bag, the crafter's "any" or a named material), the gold and the hours, the bounds, the check's
    /// reason, and Propose. Every text from <c>UiStrings</c> and <c>ReasonCodeStrings</c>.
    /// </summary>
    public sealed class ContractWindowPresenter : HudPresenter
    {
        private readonly ContractConfig _contracts;
        private readonly ContractWindowViewModel _model;
        private readonly IContractWindowActions _actions;
        private readonly Func<long> _utcNowMs;
        private readonly List<string> _rowTexts = new List<string>();
        private VisualElement _window = null!;
        private Button _mineTab = null!;
        private Button _proposeTab = null!;
        private Label _empty = null!;
        private ScrollView _list = null!;
        private ScrollView _form = null!;
        private TextField _gold = null!;
        private TextField _deadline = null!;
        private Button _propose = null!;
        private Label _check = null!;
        private VisualElement _slots = null!;
        private Label _action = null!;
        private Label _output = null!;
        private TextField? _editing; // the field with the focus: its text is the player's, never overwritten by a redraw
        private PortfolioPanel _portfolio = null!;
        private Button _portfolioBack = null!;
        private bool _pointerInside;

        public ContractWindowPresenter(HudConfig config, HudView view, ContractConfig contracts, ContractWindowViewModel model,
            IContractWindowActions actions, Func<long> utcNowMs)
            : base(config, view, model)
        {
            _contracts = contracts;
            _model = model;
            _actions = actions;
            _utcNowMs = utcNowMs;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        public Button TabButton(ContractTab tab) => tab == ContractTab.Mine ? _mineTab : _proposeTab;

        /// <summary>The rows of My contracts, top to bottom, as their first line (tests and the QA smoke).</summary>
        public IReadOnlyList<string> RowTexts => _rowTexts;

        public bool EmptyShown => _empty.style.display == DisplayStyle.Flex;

        /// <summary>A button of the window by name (<c>contract_cancel_&lt;id&gt;</c>, <c>contract_choose_&lt;input&gt;</c>, ...), or null.</summary>
        public Button? ButtonNamed(string name) => _window.Q<Button>(name);

        public Button ProposeButton => _propose;

        public TextField GoldField => _gold;

        public TextField DeadlineField => _deadline;

        /// <summary>The form's check shown under the fields (empty when it passes).</summary>
        public string CheckText => _check.style.display == DisplayStyle.Flex ? _check.text : "";

        public string ActionText => _action.text;

        public PortfolioPanel Portfolio => _portfolio;

        protected override void Build()
        {
            _window = View.Place(HudElementIds.ContractWindow, HudWidgets.Panel("contract-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.style.paddingBottom = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            var header = Row("contract-header");
            header.style.justifyContent = Justify.SpaceBetween;
            var title = HudWidgets.Label(Config.Text.Get(ContractTextIds.Title), "contract-title");
            title.style.color = Theme.Accent;
            header.Add(title);
            var close = Button("contract_close", Config.Text.Get(ContractTextIds.Close), () => _actions.CloseWindow());
            close.tooltip = Config.Text.Get(ContractTextIds.CloseLabel);
            header.Add(close);
            _window.Add(header);

            var tabs = Row("contract-tabs");
            _mineTab = Button("contract_tab_mine", Config.Text.Get(ContractTextIds.TabMine), () => _actions.SelectTab(ContractTab.Mine));
            _proposeTab = Button("contract_tab_propose", Config.Text.Get(ContractTextIds.TabPropose), () => _actions.SelectTab(ContractTab.Propose));
            tabs.Add(_mineTab);
            tabs.Add(_proposeTab);
            _window.Add(tabs);

            _empty = Text("", "contract-empty");
            _window.Add(_empty);
            _list = Scroll("contract_list");
            _window.Add(_list);

            _portfolioBack = Button("contract_portfolio_back", Config.Text.Get(ContractTextIds.PortfolioBack), () => _actions.ClosePortfolio());
            _window.Add(_portfolioBack);
            _portfolio = new PortfolioPanel(Config, _contracts, "contract");
            _window.Add(_portfolio.Root);

            _form = Scroll("contract_form");
            var actionRow = Row("contract-form-action");
            actionRow.Add(Button("contract_previous", Config.Text.Get(ContractTextIds.FormPrevious), () => _actions.StepDefinition(-1)));
            _action = Text("", "contract-form-action__text");
            _action.style.flexGrow = 1;
            actionRow.Add(_action);
            actionRow.Add(Button("contract_next", Config.Text.Get(ContractTextIds.FormNext), () => _actions.StepDefinition(1)));
            _form.Add(actionRow);
            _output = Text("", "contract-form-output");
            _form.Add(_output);
            _slots = HudWidgets.Box("contract-form-slots");
            _slots.pickingMode = PickingMode.Position;
            _form.Add(_slots);
            _gold = Field("contract_gold", Config.Text.Get(ContractTextIds.FormGold), text => _actions.SetGold(text));
            _deadline = Field("contract_deadline", Config.Text.Get(ContractTextIds.FormDeadline), text => _actions.SetDeadline(text));
            _form.Add(_gold);
            _form.Add(_deadline);
            _form.Add(Text(Config.Text.Get(ContractTextIds.FormBounds, new TextArg("gold", NumberText.Of(_contracts.MinGold)),
                new TextArg("min", NumberText.Of(_contracts.MinDeadlineHours)), new TextArg("max", NumberText.Of(_contracts.MaxDeadlineHours))),
                "contract-form-bounds"));
            _check = Text("", "contract-form-check");
            _form.Add(_check);
            _propose = Button("contract_propose", Config.Text.Get(ContractTextIds.FormPropose), () => _actions.Propose());
            _form.Add(_propose);
            _window.Add(_form);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
            {
                _pointerInside = false; // a hidden window captures nothing (no leave event comes)
                return;
            }
            bool mine = _model.Tab == ContractTab.Mine;
            HudWidgets.Border(_mineTab, mine ? Theme.Accent : Theme.Border);
            HudWidgets.Border(_proposeTab, mine ? Theme.Border : Theme.Accent);
            bool portfolio = _model.Portfolio.Shown;
            Show(_portfolio.Root, portfolio);
            Show(_portfolioBack, portfolio);
            if (portfolio)
                _portfolio.Render(_model.Portfolio.State, _model.Portfolio.Pending != 0);
            Show(_list, mine && !portfolio);
            Show(_form, !mine && !portfolio);
            Show(_empty, false);
            if (portfolio)
                return;
            if (mine)
                RefreshMine();
            else
                RefreshForm();
        }

        private void RefreshMine()
        {
            _list.Clear();
            _rowTexts.Clear();
            long now = _utcNowMs();
            foreach (var entry in _model.Mine)
            {
                long id = entry.ContractId;
                var box = HudWidgets.Box("contract-row");
                box.pickingMode = PickingMode.Position;
                string text = RowText(entry, now);
                _rowTexts.Add(text);
                var line = Row("contract-row__head");
                var label = Text(text, "contract-row__text");
                label.style.flexGrow = 1;
                label.style.flexShrink = 1;
                label.style.whiteSpace = WhiteSpace.Normal;
                line.Add(label);
                long cost = ContractBinder.CancelCost(entry, _contracts.CancelCostPercent);
                if (cost >= 0)
                {
                    var cancel = Button("contract_cancel_" + id, cost > 0
                        ? Config.Text.Get(ContractTextIds.CancelCost, new TextArg("gold", NumberText.Of(cost)))
                        : Config.Text.Get(ContractTextIds.Cancel), () => _actions.CancelContract(id));
                    cancel.SetEnabled(_model.PendingAction == 0);
                    line.Add(cancel);
                }
                if (entry.Role == ContractRole.Customer && (entry.Status == ContractStatus.Accepted || entry.Status == ContractStatus.InProgress))
                    line.Add(Button("contract_portfolio_" + id, Config.Text.Get(ContractTextIds.CrafterPortfolio), () => _actions.ViewCrafter(id)));
                box.Add(line);
                foreach (string slot in SlotLines(entry.Slots))
                    box.Add(Text(slot, "contract-row__slot"));
                _list.Add(box);
            }
            Show(_empty, _model.Mine.Count == 0);
            if (_model.Mine.Count == 0)
                HudWidgets.SetText(_empty, Config.Text.Get(ContractTextIds.Empty));
        }

        private void RefreshForm()
        {
            var form = _model.Form;
            var definition = form.Definition;
            if (definition == null)
            {
                HudWidgets.SetText(_action, Config.Text.Get(ContractTextIds.FormNothing));
                HudWidgets.SetText(_output, "");
                _slots.Clear();
                _propose.SetEnabled(false);
                return;
            }
            HudWidgets.SetText(_action, Config.Text.Get(ContractTextIds.FormAction,
                new TextArg("action", Config.Text.Get(ContractTextIds.Action(definition)))));
            HudWidgets.SetText(_output, Config.Text.Get(ContractTextIds.FormOutput, new TextArg("output", OutputName(definition))));
            _slots.Clear();
            for (int i = 0; i < form.Slots.Count; i++)
            {
                int input = i;
                var slot = form.Slots[i];
                var spec = definition.Inputs[i];
                string what;
                if (slot.Supplier == ContractSupplier.Customer)
                    what = slot.Stack is { } stack
                        ? Config.Text.Get(ContractTextIds.SlotCustomer, new TextArg("item", ItemName(stack.ItemNetId)), new TextArg("count", NumberText.Of(spec.Count)))
                        : Config.Text.Get(ContractTextIds.FormEmpty);
                else
                    what = Config.Text.Get(ContractTextIds.SlotCrafter, new TextArg("item", slot.CrafterItem == 0
                        ? Config.Text.Get(ContractTextIds.SlotAny) : ItemName(slot.CrafterItem)), new TextArg("count", NumberText.Of(spec.Count)));
                string inputName = spec.NameId.Length > 0 ? Config.Text.Get(spec.NameId) : ItemName(spec.ItemId);
                var row = Row("contract-form-slot");
                var label = Text(Config.Text.Get(ContractTextIds.FormSlot, new TextArg("n", NumberText.Of(i + 1)),
                    new TextArg("input", inputName), new TextArg("supplier", what)), "contract-form-slot__text");
                label.style.flexGrow = 1;
                label.style.flexShrink = 1;
                label.style.whiteSpace = WhiteSpace.Normal;
                row.Add(label);
                row.Add(Button("contract_switch_" + i, Config.Text.Get(ContractTextIds.FormSwitch), () => _actions.SwitchSupplier(input)));
                row.Add(slot.Supplier == ContractSupplier.Customer
                    ? Button("contract_choose_" + i, Config.Text.Get(ContractTextIds.FormChoose), () => _actions.OpenPicker(input))
                    : Button("contract_cycle_" + i, Config.Text.Get(ContractTextIds.FormNext), () => _actions.CycleCrafterItem(input)));
                _slots.Add(row);
                if (form.PickerTarget == i)
                    _slots.Add(Picker(form.Candidates(i)));
            }
            string gold = form.Gold.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_editing != _gold && _gold.value != gold)
                _gold.SetValueWithoutNotify(gold);
            string hours = form.DeadlineHours.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_editing != _deadline && _deadline.value != hours)
                _deadline.SetValueWithoutNotify(hours);
            var reason = form.Check();
            Show(_check, reason != ContractReason.None);
            if (reason != ContractReason.None)
                HudWidgets.SetText(_check, Config.Reasons.Text(reason.ToString()));
            _propose.SetEnabled(reason == ContractReason.None && _model.PendingAction == 0);
        }

        private VisualElement Picker(List<InventorySlotEntry> candidates)
        {
            var box = HudWidgets.Box("contract-picker");
            box.pickingMode = PickingMode.Position;
            HudWidgets.Border(box, Theme.Accent);
            box.Add(Text(Config.Text.Get(ContractTextIds.PickTitle), "contract-picker__title"));
            if (candidates.Count == 0)
                box.Add(Text(Config.Text.Get(ContractTextIds.PickEmpty), "contract-picker__empty"));
            for (int i = 0; i < candidates.Count; i++)
            {
                int row = i;
                box.Add(Button("contract_pick_" + i, Config.Text.Get(ContractTextIds.PickEntry,
                    new TextArg("item", ItemName(candidates[i].ItemNetId)), new TextArg("count", NumberText.Of((int)candidates[i].Count))),
                    () => _actions.PickRow(row)));
            }
            box.Add(Button("contract_pick_cancel", Config.Text.Get(ContractTextIds.PickCancel), () => _actions.OpenPicker(_model.Form.PickerTarget)));
            return box;
        }

        /// <summary>The first line of an own contract's row: the role, what it makes, the gold and the status with the time left.</summary>
        public string RowText(ContractEntry entry, long nowMs) => RowText(Config, _contracts, entry, nowMs);

        /// <summary>An own contract's first line (the C window and the journal's contracts tab).</summary>
        public static string RowText(HudConfig config, ContractConfig contracts, ContractEntry entry, long nowMs)
        {
            string role = config.Text.Get(entry.Role == ContractRole.Crafter ? ContractTextIds.RoleCrafter : ContractTextIds.RoleCustomer);
            var definition = contracts.Definition(entry.Kind, entry.DefNetId);
            string output = definition?.Output != null ? config.Text.Get(definition.Output.NameId) : definition?.Id ?? "";
            return config.Text.Get(ContractTextIds.Row, new TextArg("role", role), new TextArg("output", output),
                new TextArg("gold", NumberText.Of(entry.Gold)), new TextArg("status", StatusText(config, entry, nowMs)));
        }

        public static string StatusText(HudConfig config, ContractEntry entry, long nowMs)
        {
            switch (entry.Status)
            {
                case ContractStatus.Proposed:
                    return config.Text.Get(ContractTextIds.StatusProposed, new TextArg("time", TimeLeft(config, entry.OpenUntilUtcMs - nowMs)));
                case ContractStatus.Accepted:
                    return config.Text.Get(entry.WorkStarted ? ContractTextIds.StatusStarted : ContractTextIds.StatusAccepted,
                        new TextArg("time", TimeLeft(config, entry.DeadlineUtcMs - nowMs)));
                case ContractStatus.InProgress:
                    return config.Text.Get(ContractTextIds.StatusInProgress, new TextArg("time", TimeLeft(config, entry.DeadlineUtcMs - nowMs)));
                default:
                    return config.Text.Get(ContractTextIds.StatusClosed);
            }
        }

        /// <summary>Hours and minutes left (never below 0).</summary>
        public static string TimeLeft(HudConfig config, long ms)
        {
            long minutes = Math.Max(0, ms) / 60_000;
            return config.Text.Get(ContractTextIds.Time, new TextArg("hours", NumberText.Of(minutes / 60)),
                new TextArg("minutes", NumberText.Of(minutes % 60)));
        }

        private IEnumerable<string> SlotLines(ContractSlot[] slots)
        {
            foreach (var slot in slots)
            {
                string item = slot.ItemNetId == 0 ? Config.Text.Get(ContractTextIds.SlotAny) : ItemName(slot.ItemNetId);
                yield return Config.Text.Get(slot.Supplier == ContractSupplier.Crafter ? ContractTextIds.SlotCrafter : ContractTextIds.SlotCustomer,
                    new TextArg("item", item), new TextArg("count", NumberText.Of((int)slot.Count)));
            }
        }

        private string OutputName(Shared.Simulation.Crafting.ForgeDefinition? definition) =>
            definition?.Output != null ? Config.Text.Get(definition.Output.NameId) : definition?.Id ?? "";

        private string ItemName(int netId) =>
            _contracts.Forge?.Item(netId) is { } item ? Config.Text.Get(item.NameId) : NumberText.Of(netId);

        private string ItemName(string itemId) =>
            _contracts.Forge?.Item(itemId) is { } item ? Config.Text.Get(item.NameId) : itemId;

        private TextField Field(string name, string label, Action<string> onChange)
        {
            var field = new TextField { name = name, value = string.Empty, maxLength = 19 };
            field.label = label;
            field.RegisterValueChangedCallback(change => onChange(change.newValue));
            field.RegisterCallback<FocusInEvent>(_ =>
            {
                _editing = field;
                _actions.FocusField(true);
            });
            field.RegisterCallback<FocusOutEvent>(_ =>
            {
                if (_editing == field)
                    _editing = null;
                _actions.FocusField(false);
            });
            return field;
        }

        private ScrollView Scroll(string name)
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical) { name = name };
            scroll.style.flexGrow = 1;
            scroll.style.flexShrink = 1;
            scroll.pickingMode = PickingMode.Position;
            return scroll;
        }

        private static VisualElement Row(string className)
        {
            var row = HudWidgets.Box(className);
            row.style.flexDirection = FlexDirection.Row;
            row.pickingMode = PickingMode.Position;
            return row;
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
    }
}
