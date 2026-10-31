#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Jobs;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Jobs;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Jobs
{
    /// <summary>
    /// Spec 9.13 <c>jobs_window</c> (spec 5.1; WP-M3-1, WP-M3-7): the Jobs window, placed by its <c>HudLayout</c> row. A tab row:
    /// the jobs list and one tree tab per held job.
    /// <para>
    /// The jobs list: the main and the secondary slot, each with its job, job level, XP bar and job points (or "Not chosen
    /// yet"). At a job master it adds the pick section: one row per job with its level requirement and a pick button per slot,
    /// disabled with the rules' reason when the pick is not allowed; a pick first shows the confirmation step (the choice is
    /// permanent).
    /// </para>
    /// <para>
    /// A tree tab (decision 8): the rows with their job level gates, the links between prerequisites and the nodes they open
    /// (lines), each node with its level out of its max and its next level's cost; the selected node's text, its bonus now and
    /// at the next level, its requirement and Learn; the unspent points and Respec, which asks for confirmation first; and the
    /// job's active bonuses with their caps. Text from <c>UiStrings</c> and <c>ReasonCodeStrings</c>; the server decides
    /// every pick, learn and respec.
    /// </para>
    /// </summary>
    public sealed class JobsWindowPresenter : HudPresenter
    {
        private const int BarHeightPx = 6;
        private const float LinkWidthPx = 2f;

        /// <summary>A grid row is at least this many body lines high (a node shows its name and its level and cost).</summary>
        private const int NodeLines = 3;

        private readonly JobsConfig _jobs;
        private readonly JobsWindowViewModel _model;
        private readonly IJobsWindowActions _actions;
        private readonly Dictionary<JobSlot, SlotView> _slots = new Dictionary<JobSlot, SlotView>();
        private readonly List<PickRow> _rows = new List<PickRow>();
        private readonly Dictionary<string, Button> _nodeButtons = new Dictionary<string, Button>();
        private readonly List<Label> _rowGates = new List<Label>();
        private readonly List<(Vector2 From, Vector2 To, bool Met)> _linkLines = new List<(Vector2, Vector2, bool)>();
        private VisualElement _window = null!;
        private VisualElement _tabs = null!;
        private VisualElement _overview = null!;
        private VisualElement _pick = null!;
        private Label _hint = null!;
        private VisualElement _confirm = null!;
        private Label _confirmText = null!;
        private Label _waiting = null!;
        private VisualElement _tree = null!;
        private VisualElement _grid = null!;
        private VisualElement _links = null!;
        private VisualElement _details = null!;
        private Label _nodeName = null!;
        private Label _nodeInfo = null!;
        private Button _learn = null!;
        private Label _treePoints = null!;
        private Button _respec = null!;
        private VisualElement _respecConfirm = null!;
        private Label _respecTitle = null!;
        private Label _summary = null!;
        private JobTreeView? _view;
        private bool _pointerInside;

        public JobsWindowPresenter(HudConfig config, HudView view, JobsConfig jobs, JobsWindowViewModel model, IJobsWindowActions actions)
            : base(config, view, model)
        {
            _jobs = jobs;
            _model = model;
            _actions = actions;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        /// <summary>The text of a slot's line ("Main job: Blacksmith ...").</summary>
        public string SlotText(JobSlot slot) => _slots[slot].Text.text;

        public bool PickShown => _pick.style.display == DisplayStyle.Flex && _overview.style.display == DisplayStyle.Flex;

        public bool ConfirmShown => _confirm.style.display == DisplayStyle.Flex;

        public string ConfirmText => _confirmText.text;

        public int RowCount => _rows.Count;

        public Button PickButton(int row, JobSlot slot) => slot == JobSlot.Secondary ? _rows[row].Secondary : _rows[row].Main;

        /// <summary>The reason line of pick row <paramref name="row"/> (empty when every slot can be picked or none is open).</summary>
        public string RowReason(int row) => _rows[row].Reason.text;

        public Button ConfirmButton => _confirm.Q<Button>("jobs_confirm");

        public Button CancelButton => _confirm.Q<Button>("jobs_cancel");

        // ---- the tree tab (WP-M3-7; tests and QA checks)

        /// <summary>The tab button of the jobs list (0) or of a held job's tree; null when there is none.</summary>
        public Button? TabButton(byte jobNetId) => _tabs.Q<Button>("jobs_tab_" + jobNetId.ToString(CultureInfo.InvariantCulture));

        public bool TreeShown => _tree.style.display == DisplayStyle.Flex;

        /// <summary>The button of a node in the shown tree, or null.</summary>
        public Button? NodeButton(string nodeId) => _nodeButtons.TryGetValue(nodeId, out var button) ? button : null;

        /// <summary>The text of a node's button ("Prospector\n1/5   Cost 1").</summary>
        public string NodeText(string nodeId) => NodeButton(nodeId)?.text ?? string.Empty;

        /// <summary>The gate label of row <paramref name="row"/> ("Job level 5").</summary>
        public string RowGateText(int row) => row < _rowGates.Count ? _rowGates[row].text : string.Empty;

        /// <summary>The links drawn (one line per prerequisite).</summary>
        public int LinkCount => _view?.Links().Count ?? 0;

        public string DetailsText => _nodeName.text + "\n" + _nodeInfo.text;

        public Button LearnButton => _learn;

        public string TreePointsText => _treePoints.text;

        public Button RespecButton => _respec;

        public bool RespecConfirmShown => _respecConfirm.style.display == DisplayStyle.Flex;

        public string RespecConfirmTitle => _respecTitle.text;

        public Button RespecConfirmButton => _respecConfirm.Q<Button>("jobtree_respec_confirm");

        public Button RespecCancelButton => _respecConfirm.Q<Button>("jobtree_respec_cancel");

        public string SummaryText => _summary.text;

        protected override void Build()
        {
            _window = View.Place(HudElementIds.JobsWindow, HudWidgets.Panel("jobs-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.style.paddingBottom = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            var header = HudWidgets.Box("jobs-header");
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.pickingMode = PickingMode.Position;
            var title = HudWidgets.Label(Config.Text.Get(JobsTextIds.Title), "jobs-title");
            title.style.color = Theme.Accent;
            header.Add(title);
            var close = Button("jobs_close", Config.Text.Get(JobsTextIds.Close), () => _actions.CloseJobs());
            close.tooltip = Config.Text.Get(JobsTextIds.CloseLabel);
            header.Add(close);
            _window.Add(header);

            _tabs = Row("jobs-tabs");
            _window.Add(_tabs);

            _overview = HudWidgets.Box("jobs-overview");
            _overview.pickingMode = PickingMode.Position;
            _window.Add(_overview);
            BuildOverview();

            _tree = HudWidgets.Box("jobs-tree");
            _tree.pickingMode = PickingMode.Position;
            _tree.style.flexGrow = 1;
            _window.Add(_tree);
            BuildTree();
        }

        private void BuildOverview()
        {
            foreach (var slot in new[] { JobSlot.Main, JobSlot.Secondary })
            {
                var view = new SlotView(slot);
                view.Text.style.color = Theme.Text;
                view.Text.style.whiteSpace = WhiteSpace.Normal;
                view.Bar.style.height = BarHeightPx;
                view.Bar.style.backgroundColor = Theme.Panel;
                HudWidgets.Border(view.Bar, Theme.Border);
                view.Fill.style.height = Length.Percent(100);
                view.Fill.style.backgroundColor = Theme.Accent;
                view.Root.Add(view.Text);
                view.Bar.Add(view.Fill);
                view.Root.Add(view.Bar);
                view.Root.style.marginTop = 4;
                _slots[slot] = view;
                _overview.Add(view.Root);
            }

            _hint = HudWidgets.Label(Config.Text.Get(JobsTextIds.PickHint), "jobs-hint");
            _hint.style.color = Theme.Text;
            _hint.style.marginTop = 6;
            _hint.style.whiteSpace = WhiteSpace.Normal;
            _overview.Add(_hint);

            _pick = HudWidgets.Box("jobs-pick");
            _pick.pickingMode = PickingMode.Position;
            _pick.style.marginTop = 6;
            var caption = HudWidgets.Label(Config.Text.Get(JobsTextIds.PickTitle), "jobs-caption");
            caption.style.color = Theme.Accent;
            _pick.Add(caption);
            _overview.Add(_pick);

            _confirm = HudWidgets.Box("jobs-confirm");
            _confirm.pickingMode = PickingMode.Position;
            _confirm.style.marginTop = 6;
            var confirmTitle = HudWidgets.Label(Config.Text.Get(JobsTextIds.ConfirmTitle), "jobs-caption");
            confirmTitle.style.color = Theme.Accent;
            _confirm.Add(confirmTitle);
            _confirmText = HudWidgets.Label("", "jobs-confirm__text");
            _confirmText.style.color = Theme.Text;
            _confirmText.style.whiteSpace = WhiteSpace.Normal;
            _confirm.Add(_confirmText);
            var buttons = Row("jobs-confirm__buttons");
            buttons.Add(Button("jobs_confirm", Config.Text.Get(JobsTextIds.Confirm), () => _actions.ConfirmPick()));
            buttons.Add(Button("jobs_cancel", Config.Text.Get(JobsTextIds.Cancel), () => _actions.CancelPick()));
            _confirm.Add(buttons);
            _overview.Add(_confirm);

            _waiting = HudWidgets.Label(Config.Text.Get(JobsTextIds.Waiting), "jobs-waiting");
            _waiting.style.color = Theme.Text;
            _overview.Add(_waiting);
        }

        private void BuildTree()
        {
            _grid = HudWidgets.Box("jobs-tree__grid");
            _grid.pickingMode = PickingMode.Position;
            _grid.style.position = Position.Relative;
            _grid.style.flexGrow = 1;
            _grid.style.marginTop = 4;
            _grid.RegisterCallback<GeometryChangedEvent>(_ => PlaceNodes());
            // The links under the nodes (decision 8): one line from each prerequisite to the node it opens.
            _links = new VisualElement { name = "jobtree_links", pickingMode = PickingMode.Ignore };
            _links.style.position = Position.Absolute;
            _links.style.left = 0;
            _links.style.top = 0;
            _links.style.right = 0;
            _links.style.bottom = 0;
            _links.generateVisualContent += DrawLinks;
            _grid.Add(_links);
            _tree.Add(_grid);

            // Below the grid: the selected node's details (or the respec confirmation) beside the active bonuses, so the
            // grid keeps its size whatever shows there.
            var bottom = Row("jobs-tree__bottom");
            bottom.style.marginTop = 4;
            bottom.style.flexShrink = 0;
            _tree.Add(bottom);
            var left = HudWidgets.Box("jobs-tree__left");
            left.pickingMode = PickingMode.Position;
            left.style.flexGrow = 1;
            left.style.flexBasis = 0;
            left.style.marginRight = 6;
            bottom.Add(left);
            var right = HudWidgets.Box("jobs-tree__right");
            right.pickingMode = PickingMode.Position;
            right.style.flexGrow = 1;
            right.style.flexBasis = 0;
            bottom.Add(right);

            _details = HudWidgets.Box("jobs-tree__details");
            _details.pickingMode = PickingMode.Position;
            _nodeName = HudWidgets.Label("", "jobs-tree__name");
            _nodeName.style.color = Theme.Accent;
            _details.Add(_nodeName);
            _nodeInfo = HudWidgets.Label("", "jobs-tree__info");
            _nodeInfo.style.color = Theme.Text;
            _nodeInfo.style.whiteSpace = WhiteSpace.Normal;
            _details.Add(_nodeInfo);
            var buttons = Row("jobs-tree__buttons");
            buttons.style.justifyContent = Justify.SpaceBetween;
            _learn = Button("jobtree_learn", Config.Text.Get(JobsTextIds.Learn), () => _actions.Learn());
            buttons.Add(_learn);
            _respec = Button("jobtree_respec", Config.Text.Get(JobsTextIds.Respec), () => _actions.Respec());
            buttons.Add(_respec);
            _details.Add(buttons);
            left.Add(_details);

            _respecConfirm = HudWidgets.Box("jobs-tree__confirm");
            _respecConfirm.pickingMode = PickingMode.Position;
            _respecTitle = HudWidgets.Label("", "jobs-caption");
            _respecTitle.style.color = Theme.Accent;
            _respecConfirm.Add(_respecTitle);
            var respecText = HudWidgets.Label(Config.Text.Get(JobsTextIds.RespecText), "jobs-confirm__text");
            respecText.style.color = Theme.Text;
            respecText.style.whiteSpace = WhiteSpace.Normal;
            _respecConfirm.Add(respecText);
            var confirmButtons = Row("jobs-confirm__buttons");
            confirmButtons.Add(Button("jobtree_respec_confirm", Config.Text.Get(JobsTextIds.Confirm), () => _actions.ConfirmRespec()));
            confirmButtons.Add(Button("jobtree_respec_cancel", Config.Text.Get(JobsTextIds.Cancel), () => _actions.CancelRespec()));
            _respecConfirm.Add(confirmButtons);
            left.Add(_respecConfirm);

            _treePoints = HudWidgets.Label("", "jobs-tree__points");
            _treePoints.style.color = Theme.Accent;
            right.Add(_treePoints);
            var summaryTitle = HudWidgets.Label(Config.Text.Get(JobsTextIds.SummaryTitle), "jobs-caption");
            summaryTitle.style.color = Theme.Accent;
            summaryTitle.style.marginTop = 4;
            right.Add(summaryTitle);
            _summary = HudWidgets.Label("", "jobs-tree__summary");
            _summary.style.color = Theme.Text;
            _summary.style.whiteSpace = WhiteSpace.Normal;
            right.Add(_summary);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
                return;
            RefreshTabs();
            _view = _model.Tree;
            Show(_overview, _view == null);
            Show(_tree, _view != null);
            if (_view == null)
                RefreshOverview();
            else
                RefreshTree(_view);
        }

        private void RefreshTabs()
        {
            _tabs.Clear();
            var overview = Button("jobs_tab_0", Config.Text.Get(JobsTextIds.TabOverview), () => _actions.SelectTab(0));
            HudWidgets.Border(overview, _model.Tab == 0 ? Theme.Accent : Theme.Border);
            _tabs.Add(overview);
            if (_jobs.Rules?.Tree == null)
                return;
            foreach (var held in _model.HeldJobs)
            {
                byte netId = held.JobNetId;
                var tab = Button("jobs_tab_" + netId.ToString(CultureInfo.InvariantCulture),
                    Config.Text.Get(JobsTextIds.TabTree, new TextArg("name", JobName(netId))), () => _actions.SelectTab(netId));
                HudWidgets.Border(tab, _model.Tab == netId ? Theme.Accent : Theme.Border);
                _tabs.Add(tab);
            }
        }

        private void RefreshOverview()
        {
            RefreshSlot(JobSlot.Main);
            RefreshSlot(JobSlot.Secondary);
            bool open = _model.InSlot(JobSlot.Main) == null || _model.InSlot(JobSlot.Secondary) == null;
            Show(_hint, !_model.AtJobMaster && open && _model.State != null);
            Show(_pick, _model.AtJobMaster && _model.Confirming == null);
            Show(_confirm, _model.Confirming != null);
            Show(_waiting, _model.Pending != 0);
            if (_model.AtJobMaster)
                RefreshRows();
            if (_model.Confirming is { } confirming)
            {
                string name = JobName(confirming.JobNetId);
                HudWidgets.SetText(_confirmText, Config.Text.Get(JobsTextIds.ConfirmText, new TextArg("name", name),
                    new TextArg("slot", Config.Text.Get(JobsTextIds.SlotName(confirming.Slot)))));
            }
        }

        private void RefreshTree(JobTreeView view)
        {
            _grid.style.minHeight = view.RowCount * NodeLines * Theme.Tokens.FontSizeBody;
            foreach (var button in _nodeButtons.Values)
                button.RemoveFromHierarchy();
            _nodeButtons.Clear();
            foreach (var label in _rowGates)
                label.RemoveFromHierarchy();
            _rowGates.Clear();
            for (int row = 0; row < view.RowCount; row++)
            {
                var gate = HudWidgets.Label(Config.Text.Get(JobsTextIds.RowGate, new TextArg("level", Number(view.RowGate(row)))), "jobs-tree__gate");
                gate.name = "jobtree_gate_" + row.ToString(CultureInfo.InvariantCulture);
                gate.style.position = Position.Absolute;
                gate.style.color = view.RowOpen(row) ? Theme.Text : Theme.Locked;
                gate.pickingMode = PickingMode.Ignore;
                _rowGates.Add(gate);
                _grid.Add(gate);
            }
            foreach (var node in view.Nodes)
            {
                var button = NodeElement(view, node);
                _nodeButtons[node.Id] = button;
                _grid.Add(button);
            }
            PlaceNodes();

            RefreshDetails(view);
            HudWidgets.SetText(_treePoints, Config.Text.Get(JobsTextIds.TreePoints, new TextArg("points", Number(view.Points))));
            _learn.SetEnabled(_model.CanLearn);
            HudWidgets.Border(_learn, _model.CanLearn ? Theme.Accent : Theme.Border);
            _respec.SetEnabled(_model.CanRespec);
            Show(_respecConfirm, _model.ConfirmingRespec);
            Show(_details, !_model.ConfirmingRespec); // the confirmation takes the details' place
            HudWidgets.SetText(_respecTitle, Config.Text.Get(JobsTextIds.RespecTitle, new TextArg("name", JobName(view.Job.NetId))));
            HudWidgets.SetText(_summary, SummaryOf(view));
        }

        private Button NodeElement(JobTreeView view, JobTree node)
        {
            string id = node.Id;
            var state = view.StateOf(node);
            var button = new Button(() => _actions.SelectNode(id)) { name = "jobtree_node_" + id, focusable = false };
            button.AddToClassList("jobs-tree__node");
            button.AddToClassList("jobs-tree__node--" + state.ToString().ToLowerInvariant());
            button.style.position = Position.Absolute;
            button.style.fontSize = Theme.Tokens.FontSizeBody; // spec 9.8: never below the body size
            button.style.whiteSpace = WhiteSpace.Normal;
            button.style.color = state == JobNodeState.RowLocked || state == JobNodeState.Locked ? Theme.Locked : Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            var border = id == _model.SelectedNode ? Theme.Accent
                : view.Check(id) == JobTreeCheck.Ok ? Theme.Stamina
                : state == JobNodeState.Learned || state == JobNodeState.Maxed ? Theme.Border : Theme.Locked;
            HudWidgets.Border(button, border);
            string text = Config.Text.Get(node.NameId) + "\n"
                          + Config.Text.Get(JobsTextIds.NodeLevel, new TextArg("level", Number(view.LevelOf(id))), new TextArg("max", Number(node.MaxLevel)));
            if (state != JobNodeState.Maxed)
                text += "   " + Config.Text.Get(JobsTextIds.Cost, new TextArg("cost", Number(view.CostOf(node))));
            button.text = text;
            return button;
        }

        /// <summary>
        /// The row gates in a left column, then each node at its row and at the rank of its column among the columns the tree
        /// uses, the cells sized from the grid's current size; the links between the cells' centres.
        /// </summary>
        private void PlaceNodes()
        {
            _linkLines.Clear();
            var view = _view;
            var size = _grid.contentRect;
            if (view == null || view.RowCount == 0 || view.Columns.Count == 0 || float.IsNaN(size.width) || size.width <= 0f || size.height <= 0f)
            {
                _links.MarkDirtyRepaint();
                return;
            }
            var rank = new Dictionary<int, int>();
            foreach (int col in view.Columns)
                rank[col] = rank.Count;
            float cellWidth = size.width / (view.Columns.Count + 1); // the first column holds the row gates
            float cellHeight = size.height / view.RowCount;
            for (int row = 0; row < _rowGates.Count; row++)
            {
                _rowGates[row].style.left = 0;
                _rowGates[row].style.top = row * cellHeight;
                _rowGates[row].style.width = cellWidth;
            }
            var centres = new Dictionary<string, Vector2>();
            foreach (var node in view.Nodes)
            {
                if (!_nodeButtons.TryGetValue(node.Id, out var button))
                    continue;
                float left = (rank[node.Col] + 1) * cellWidth;
                float top = node.Row * cellHeight;
                button.style.left = left;
                button.style.top = top;
                button.style.width = cellWidth;
                button.style.height = cellHeight;
                centres[node.Id] = new Vector2(left + cellWidth / 2f, top + cellHeight / 2f);
            }
            foreach (var link in view.Links())
            {
                if (centres.TryGetValue(link.From.Id, out var from) && centres.TryGetValue(link.To.Id, out var to))
                    _linkLines.Add((from, to, view.LevelOf(link.From.Id) >= link.Level));
            }
            _links.MarkDirtyRepaint();
        }

        private void DrawLinks(MeshGenerationContext context)
        {
            var painter = context.painter2D;
            painter.lineWidth = LinkWidthPx;
            foreach (var (from, to, met) in _linkLines)
            {
                painter.strokeColor = met ? Theme.Accent : Theme.Locked;
                painter.BeginPath();
                painter.MoveTo(from);
                painter.LineTo(to);
                painter.Stroke();
            }
        }

        private void RefreshDetails(JobTreeView view)
        {
            var tree = _jobs.Rules?.Tree;
            if (_model.SelectedNode == null || tree?.Node(_model.SelectedNode) is not { } node)
            {
                HudWidgets.SetText(_nodeName, string.Empty);
                HudWidgets.SetText(_nodeInfo, Config.Text.Get(JobsTextIds.SelectHint));
                return;
            }
            int level = view.LevelOf(node.Id);
            HudWidgets.SetText(_nodeName, Config.Text.Get(node.NameId) + "   "
                + Config.Text.Get(JobsTextIds.NodeLevel, new TextArg("level", Number(level)), new TextArg("max", Number(node.MaxLevel))));
            var info = new StringBuilder(Config.Text.Get(node.TextId));
            info.Append('\n').Append(level > 0
                ? Config.Text.Get(JobsTextIds.Now, new TextArg("bonus", BonusText(node.BonusKind, JobTreeMath.ValueAt(node, level), node.MoldTiers)))
                : Config.Text.Get(JobsTextIds.NothingYet));
            info.Append("   ").Append(JobTreeMath.NextValue(node, level) is { } next
                ? Config.Text.Get(JobsTextIds.Next, new TextArg("bonus", BonusText(node.BonusKind, next, node.MoldTiers)))
                    + "   " + Config.Text.Get(JobsTextIds.Cost, new TextArg("cost", Number(view.CostOf(node))))
                : Config.Text.Get(JobsTextIds.Maxed));
            if (!view.RowOpen(node.Row))
                info.Append('\n').Append(Config.Text.Get(JobsTextIds.RowLocked, new TextArg("level", Number(view.RowGate(node.Row)))));
            for (int i = 0; i < node.Requires.Length && i < node.RequiresLevels.Length; i++)
            {
                string required = tree!.Node(node.Requires[i]) is { } r ? Config.Text.Get(r.NameId) : node.Requires[i];
                info.Append('\n').Append(Config.Text.Get(JobsTextIds.Requires, new TextArg("name", required),
                    new TextArg("level", Number(node.RequiresLevels[i]))));
            }
            if (level < node.MaxLevel && view.ReasonCode(node.Id) is { } code)
                info.Append('\n').Append(Config.Reasons.Text(code));
            HudWidgets.SetText(_nodeInfo, info.ToString());
        }

        /// <summary>The active bonuses, one line each (decision 8): "Harvest yield +8 % (harvest; cap 25)".</summary>
        private string SummaryOf(JobTreeView view)
        {
            var lines = view.Summary();
            if (lines.Count == 0)
                return Config.Text.Get(JobsTextIds.SummaryNone);
            var text = new StringBuilder();
            foreach (var line in lines)
            {
                if (text.Length > 0)
                    text.Append('\n');
                if (line.Kind == JobBonusKind.UnlockMoldTiers)
                {
                    text.Append(Config.Text.Get(JobsTextIds.SummaryUnlock, new TextArg("bonus", TiersText(line.Kind, line.Tiers))));
                    continue;
                }
                var activities = new StringBuilder();
                foreach (var activity in line.Activities)
                {
                    if (activities.Length > 0)
                        activities.Append(", ");
                    activities.Append(Config.Text.Get(JobsTextIds.Activity(activity)));
                }
                text.Append(Config.Text.Get(JobsTextIds.SummaryLine, new TextArg("bonus", BonusText(line.Kind, line.Value, null)),
                    new TextArg("activities", activities.ToString()), new TextArg("cap", Number(line.Cap))));
            }
            return text.ToString();
        }

        private string BonusText(JobBonusKind kind, int value, int[]? tiers)
        {
            if (kind == JobBonusKind.UnlockMoldTiers)
            {
                uint bits = 0;
                foreach (int tier in tiers ?? System.Array.Empty<int>())
                    bits |= 1u << tier;
                return TiersText(kind, bits);
            }
            return Config.Text.Get(JobsTextIds.Bonus(kind), new TextArg("value", Number(value)));
        }

        private string TiersText(JobBonusKind kind, uint tiers)
        {
            var list = new StringBuilder();
            for (int tier = 1; tier <= JobTree.MaxMoldTier; tier++)
            {
                if (!JobTreeMath.HasTier(tiers, tier))
                    continue;
                if (list.Length > 0)
                    list.Append(", ");
                list.Append(Number(tier));
            }
            return Config.Text.Get(JobsTextIds.Bonus(kind), new TextArg("tiers", list.ToString()));
        }

        private void RefreshSlot(JobSlot slot)
        {
            var view = _slots[slot];
            string slotName = Config.Text.Get(JobsTextIds.SlotName(slot));
            var held = _model.InSlot(slot);
            Show(view.Bar, held != null);
            if (held == null)
            {
                HudWidgets.SetText(view.Text, slotName + ": " + Config.Text.Get(JobsTextIds.Empty));
                return;
            }
            string level = Config.Text.Get(JobsTextIds.Level, new TextArg("level", Number(held.Level)), new TextArg("max", Number(_model.MaxLevel)));
            string xp = held.XpToNext == 0
                ? Config.Text.Get(JobsTextIds.XpMax)
                : Config.Text.Get(JobsTextIds.Xp, new TextArg("xp", Number(held.Xp)), new TextArg("next", Number(held.XpToNext)));
            string points = Config.Text.Get(JobsTextIds.Points, new TextArg("points", Number(held.Points)));
            HudWidgets.SetText(view.Text, slotName + ": " + JobName(held.JobNetId) + "   " + level + "   " + xp + "   " + points);
            float filled = held.XpToNext == 0 ? 1f : Mathf.Clamp01((float)held.Xp / held.XpToNext);
            view.Fill.style.width = Length.Percent(filled * 100f);
        }

        private void RefreshRows()
        {
            var jobs = _model.Jobs;
            while (_rows.Count < jobs.Count)
            {
                int index = _rows.Count;
                var row = new PickRow(index);
                row.Name.style.color = Theme.Text;
                row.Reason.style.color = Theme.Text;
                row.Reason.style.whiteSpace = WhiteSpace.Normal;
                row.Main = Button("jobs_pick_main_" + index, Config.Text.Get(JobsTextIds.PickMain), () => _actions.Pick(index, JobSlot.Main));
                row.Secondary = Button("jobs_pick_secondary_" + index, Config.Text.Get(JobsTextIds.PickSecondary),
                    () => _actions.Pick(index, JobSlot.Secondary));
                var buttons = Row("jobs-row__buttons");
                buttons.Add(row.Main);
                buttons.Add(row.Secondary);
                row.Root.Add(row.Name);
                row.Root.Add(buttons);
                row.Root.Add(row.Reason);
                row.Root.style.marginTop = 4;
                _rows.Add(row);
                _pick.Add(row.Root);
            }
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                bool used = i < jobs.Count;
                Show(row.Root, used);
                if (!used)
                    continue;
                var job = jobs[i];
                HudWidgets.SetText(row.Name, Config.Text.Get(job.NameId) + "   "
                    + Config.Text.Get(JobsTextIds.MinLevel, new TextArg("level", Number(job.MinLevel))));
                bool main = _model.CanPick(job, JobSlot.Main);
                bool secondary = _model.CanPick(job, JobSlot.Secondary);
                row.Main.SetEnabled(main);
                row.Secondary.SetEnabled(secondary);
                HudWidgets.Border(row.Main, main ? Theme.Accent : Theme.Border);
                HudWidgets.Border(row.Secondary, secondary ? Theme.Accent : Theme.Border);
                HudWidgets.SetText(row.Reason, ReasonText(job));
            }
        }

        /// <summary>Why the next open slot cannot take <paramref name="job"/>, from <c>ReasonCodeStrings</c>; empty when it can.</summary>
        private string ReasonText(JobDef job)
        {
            var slot = _model.InSlot(JobSlot.Main) == null ? JobSlot.Main : JobSlot.Secondary;
            var check = _model.Check(job, slot);
            string? code = check switch
            {
                JobPickCheck.Ok => null,
                JobPickCheck.LevelTooLow => nameof(JobReason.JobLevelTooLow),
                JobPickCheck.MainFirst => nameof(JobReason.JobMainFirst),
                JobPickCheck.SameAsMain => nameof(JobReason.JobSameAsMain),
                JobPickCheck.NotSecondary => nameof(JobReason.JobNotSecondary),
                _ => _model.State == null ? null : nameof(JobReason.JobAlreadyChosen),
            };
            return code == null ? string.Empty : Config.Reasons.Text(code);
        }

        /// <summary>A job's display name (<c>JobDef.nameId</c>); its net id for one the tables lack.</summary>
        public string JobName(int jobNetId) => _jobs.Job(jobNetId) is { } job ? Config.Text.Get(job.NameId) : Number(jobNetId);

        private Button Button(string name, string text, System.Action onClick)
        {
            var button = new Button(onClick) { name = name, text = text };
            button.style.color = Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(button, Theme.Border);
            return button;
        }

        private static VisualElement Row(string className)
        {
            var row = HudWidgets.Box(className);
            row.style.flexDirection = FlexDirection.Row;
            row.pickingMode = PickingMode.Position;
            return row;
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        private sealed class SlotView
        {
            public SlotView(JobSlot slot)
            {
                Root = HudWidgets.Box("jobs-slot");
                Root.name = "jobs_slot_" + (slot == JobSlot.Secondary ? "secondary" : "main");
                Text = HudWidgets.Label("", "jobs-slot__text");
                Bar = HudWidgets.Box("jobs-slot__bar");
                Fill = HudWidgets.Box("jobs-slot__fill");
            }

            public VisualElement Root { get; }
            public Label Text { get; }
            public VisualElement Bar { get; }
            public VisualElement Fill { get; }
        }

        private sealed class PickRow
        {
            public PickRow(int index)
            {
                Root = HudWidgets.Box("jobs-row");
                Root.name = "jobs_row_" + index;
                Root.pickingMode = PickingMode.Position;
                Name = HudWidgets.Label("", "jobs-row__name");
                Reason = HudWidgets.Label("", "jobs-row__reason");
            }

            public VisualElement Root { get; }
            public Label Name { get; }
            public Label Reason { get; }
            public Button Main { get; set; } = null!;
            public Button Secondary { get; set; } = null!;
        }
    }
}
