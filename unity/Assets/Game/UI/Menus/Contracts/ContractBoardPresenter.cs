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
    /// Spec 9.13 <c>contract_board</c> (spec 5.4; M4 decision 10; WP-M4-10 part B; placeholder art, WP-M4-11 polishes it), placed by
    /// its <c>HudLayout</c> row, open at the contract board NPC. The header with the title and close; the job and action filters;
    /// one row per open proposal (what it makes, the gold, the hours the crafter gets from the acceptance, the time left on the board,
    /// one line per slot), selected by a click; Accept on the selected row; the page buttons; and the portfolio of the target-locked
    /// player (spec 5.4: another player's portfolio from the board). No names: the board shows none (TODO(open) in 5.4).
    /// </summary>
    public sealed class ContractBoardPresenter : HudPresenter
    {
        private readonly ContractConfig _contracts;
        private readonly ContractBoardViewModel _model;
        private readonly IContractBoardActions _actions;
        private readonly Func<long> _utcNowMs;
        private readonly List<string> _rowTexts = new List<string>();
        private readonly List<Button> _rows = new List<Button>();
        private VisualElement _window = null!;
        private Button _job = null!;
        private Button _kind = null!;
        private Label _empty = null!;
        private ScrollView _list = null!;
        private Button _accept = null!;
        private Button _more = null!;
        private Button _first = null!;
        private Button _portfolioButton = null!;
        private Button _portfolioBack = null!;
        private VisualElement _footer = null!;
        private VisualElement _filters = null!;
        private PortfolioPanel _portfolio = null!;
        private bool _pointerInside;

        public ContractBoardPresenter(HudConfig config, HudView view, ContractConfig contracts, ContractBoardViewModel model,
            IContractBoardActions actions, Func<long> utcNowMs)
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

        /// <summary>The proposal rows shown, top to bottom, as their first line (tests and the QA smoke).</summary>
        public IReadOnlyList<string> RowTexts => _rowTexts;

        /// <summary>Row <paramref name="index"/>'s button (a click selects it).</summary>
        public Button Row(int index) => _rows[index];

        public int RowCount => _rows.Count;

        public Button AcceptButton => _accept;

        public Button MoreButton => _more;

        public Button JobButton => _job;

        public Button KindButton => _kind;

        public Button PortfolioButton => _portfolioButton;

        public bool EmptyShown => _empty.style.display == DisplayStyle.Flex;

        public PortfolioPanel Portfolio => _portfolio;

        protected override void Build()
        {
            _window = View.Place(HudElementIds.ContractBoard, HudWidgets.Panel("contract-board", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.style.paddingBottom = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            // WP-M4-11 (spec 9.13): the shared window header, its close labelled for screen readers.
            _window.Add(WindowChrome.Header(Config, Config.Text.Get(ContractTextIds.BoardTitle), "contract_board_close", ContractTextIds.Close,
                ContractTextIds.CloseLabel, () => _actions.CloseBoard(), out _, out _));

            _filters = Line("contract-board-filters");
            _job = Button("contract_board_job", "", () => _actions.StepJob());
            _kind = Button("contract_board_kind", "", () => _actions.StepKind());
            _filters.Add(_job);
            _filters.Add(_kind);
            _window.Add(_filters);

            _empty = Text("", "contract-board-empty");
            _window.Add(_empty);
            _list = new ScrollView(ScrollViewMode.Vertical) { name = "contract_board_list" };
            _list.style.flexGrow = 1;
            _list.style.flexShrink = 1;
            _list.pickingMode = PickingMode.Position;
            _window.Add(_list);

            _portfolioBack = Button("contract_board_portfolio_back", Config.Text.Get(ContractTextIds.PortfolioBack), () => _actions.ClosePortfolio());
            _window.Add(_portfolioBack);
            _portfolio = new PortfolioPanel(Config, _contracts, "contract_board");
            _window.Add(_portfolio.Root);

            _footer = Line("contract-board-footer");
            _accept = Button("contract_board_accept", Config.Text.Get(ContractTextIds.BoardAccept), () => _actions.Accept());
            _first = Button("contract_board_first", Config.Text.Get(ContractTextIds.BoardFirst), () => _actions.FirstPage());
            _more = Button("contract_board_more", Config.Text.Get(ContractTextIds.BoardMore), () => _actions.NextPage());
            _portfolioButton = Button("contract_board_portfolio", Config.Text.Get(ContractTextIds.BoardPortfolio), () => _actions.ViewTarget());
            _footer.Add(_accept);
            _footer.Add(_first);
            _footer.Add(_more);
            _footer.Add(_portfolioButton);
            _window.Add(_footer);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
            {
                _pointerInside = false;
                return;
            }
            bool portfolio = _model.Portfolio.Shown;
            Show(_portfolio.Root, portfolio);
            Show(_portfolioBack, portfolio);
            Show(_list, !portfolio);
            Show(_filters, !portfolio);
            Show(_footer, !portfolio);
            Show(_empty, false);
            if (portfolio)
            {
                _portfolio.Render(_model.Portfolio.State, _model.Portfolio.Pending != 0);
                return;
            }
            string job = _model.JobFilter == 0 ? Config.Text.Get(ContractTextIds.BoardJobAny)
                : _contracts.Job(_model.JobFilter) is { } def ? Config.Text.Get(def.NameId) : NumberText.Of((int)_model.JobFilter);
            _job.text = Config.Text.Get(ContractTextIds.BoardJob, new TextArg("job", job));
            string kind = Config.Text.Get(_model.KindFilter == ForgeKind.Mold ? ContractTextIds.BoardKindMold
                : _model.KindFilter == ForgeKind.Recipe ? ContractTextIds.BoardKindRecipe
                : _model.KindFilter == ForgeKind.Enchant ? ContractTextIds.BoardKindEnchant : ContractTextIds.ActionAny);
            _kind.text = Config.Text.Get(ContractTextIds.BoardKind, new TextArg("action", kind));

            _list.Clear();
            _rows.Clear();
            _rowTexts.Clear();
            long now = _utcNowMs();
            for (int i = 0; i < _model.Rows.Count; i++)
            {
                int index = i;
                var entry = _model.Rows[i];
                string text = RowText(entry, now);
                _rowTexts.Add(text);
                var row = Button("contract_board_row_" + entry.ContractId, text, () => _actions.SelectRow(index));
                row.style.whiteSpace = WhiteSpace.Normal;
                row.style.unityTextAlign = UnityEngine.TextAnchor.MiddleLeft;
                HudWidgets.Border(row, i == _model.Selected ? Theme.Accent : Theme.Border);
                _rows.Add(row);
                _list.Add(row);
                foreach (var slot in entry.Slots)
                {
                    string item = slot.ItemNetId == 0 ? Config.Text.Get(ContractTextIds.SlotAny) : ItemName(slot.ItemNetId);
                    _list.Add(Text(Config.Text.Get(slot.Supplier == ContractSupplier.Crafter ? ContractTextIds.SlotCrafter : ContractTextIds.SlotCustomer,
                        new TextArg("item", item), new TextArg("count", NumberText.Of((int)slot.Count))), "contract-board-row__slot"));
                }
            }
            bool empty = _model.HasPage && _model.Rows.Count == 0;
            Show(_empty, empty);
            if (empty)
                HudWidgets.SetText(_empty, Config.Text.Get(ContractTextIds.BoardEmpty));
            _accept.SetEnabled(_model.SelectedRow != null && _model.PendingAccept == 0);
            _more.SetEnabled(_model.HasMore && _model.PendingBoard == 0);
            _first.SetEnabled(_model.Page > 0 && _model.PendingBoard == 0);
        }

        /// <summary>A proposal's first line: what it makes, the gold, the hours from the acceptance, the time left on the board.</summary>
        public string RowText(ContractEntry entry, long nowMs)
        {
            string output = _contracts.OutputName(Config.Text, entry); // an enchant contract: "Enchant {item} +{level}" (WP-M4-10 part C)
            return Config.Text.Get(ContractTextIds.BoardRow, new TextArg("output", output), new TextArg("gold", NumberText.Of(entry.Gold)),
                new TextArg("hours", NumberText.Of((int)entry.DeadlineHours)),
                new TextArg("time", ContractWindowPresenter.TimeLeft(Config, entry.OpenUntilUtcMs - nowMs)));
        }

        private string ItemName(int netId) =>
            _contracts.Forge?.Item(netId) is { } item ? Config.Text.Get(item.NameId) : NumberText.Of(netId);

        private static VisualElement Line(string className)
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
            label.style.whiteSpace = WhiteSpace.Normal;
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
