#nullable enable
using System.Collections.Generic;
using System.Globalization;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Skills;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Skills;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Skills
{
    /// <summary>What the skill tree window's controls ask for (the binder sends the requests).</summary>
    public interface ISkillTreeWindowActions
    {
        void SelectTab(string classId);
        void Select(string nodeId);
        void Primary();
        void Unbar();
        void Respec();
        void Advance(string classId);
        /// <summary>A learned skill was dragged from the window and let go at <paramref name="panelPosition"/> (a hotbar slot or not).</summary>
        void Drop(string nodeId, Vector2 panelPosition);
        void Close();
    }

    /// <summary>
    /// The skill tree window v1 (spec 3.5, 9.13 pattern; WP-M2-5), placed by its <c>HudLayout</c> row
    /// <c>skill_tree_window</c>: a tab per tree of the class line, the active tree's nodes on the design file's grid (row,
    /// column) with their level and state, the selected node's details and its Learn, Upgrade or Evolve button, the points
    /// left, Reset skills, the classes to advance into, and learned actives dragged onto a hotbar slot. Built in code with UI
    /// Toolkit like the other windows; text from <c>UiStrings</c>; the server decides every request.
    /// </summary>
    public sealed class SkillTreeWindowPresenter : HudPresenter
    {
        private readonly SkillTreeWindowViewModel _model;
        private readonly SkillRules? _rules;
        private readonly ISkillTreeWindowActions _actions;
        private readonly float _dragThresholdPx;
        private readonly Dictionary<string, Button> _nodeButtons = new Dictionary<string, Button>();
        private readonly int _columns;
        private readonly int _rows;
        private VisualElement _window = null!;
        private VisualElement _tabs = null!;
        private VisualElement _grid = null!;
        private Label _empty = null!;
        private Label _name = null!;
        private Label _info = null!;
        private Button _primary = null!;
        private Button _unbar = null!;
        private Label _points = null!;
        private Button _respec = null!;
        private VisualElement _advance = null!;
        private Label _ghost = null!;
        private bool _pointerInside;
        private string _pressNode = "";
        private Vector2 _pressAt;
        private bool _dragging;

        public SkillTreeWindowPresenter(HudConfig config, HudView view, SkillTreeWindowViewModel model, SkillRules? rules,
            float dragThresholdPx, ISkillTreeWindowActions actions)
            : base(config, view, model)
        {
            _model = model;
            _rules = rules;
            _dragThresholdPx = dragThresholdPx;
            _actions = actions;
            // The grid spans the widest tree of the design file: every tree keeps its own positions.
            if (rules != null)
            {
                foreach (var node in rules.Nodes)
                {
                    _columns = Mathf.Max(_columns, node.Col + 1);
                    _rows = Mathf.Max(_rows, node.Row + 1);
                }
            }
        }

        public bool CapturesPointer => _model.Open && (_pointerInside || _dragging);

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        /// <summary>The button of a node in the active tree (tests and QA checks), or null.</summary>
        public Button? NodeButton(string nodeId) => _nodeButtons.TryGetValue(nodeId, out var button) ? button : null;

        public string PointsText => _points.text;

        public string DetailsText => _name.text + "\n" + _info.text;

        public Button PrimaryButton => _primary;

        public int AdvanceButtonCount => _advance.childCount - 1;

        protected override void Build()
        {
            _window = View.Place(HudElementIds.SkillTreeWindow, HudWidgets.Panel("skill-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);
            _window.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _window.RegisterCallback<PointerUpEvent>(OnPointerUp);
            _window.RegisterCallback<PointerCaptureOutEvent>(_ => EndPress());

            var header = Row("skill-header");
            header.style.justifyContent = Justify.SpaceBetween;
            var title = HudWidgets.Label(Config.Text.Get(SkillTreeTextIds.Title), "skill-title");
            title.style.color = Theme.Accent;
            header.Add(title);
            _points = HudWidgets.Label("", "skill-points");
            _points.style.color = Theme.Text;
            header.Add(_points);
            var close = Button("skill_close", Config.Text.Get(SkillTreeTextIds.Close), () => _actions.Close());
            close.tooltip = Config.Text.Get(SkillTreeTextIds.CloseLabel);
            header.Add(close);
            _window.Add(header);

            _tabs = Row("skill-tabs");
            _window.Add(_tabs);
            _grid = HudWidgets.Box("skill-grid");
            _grid.pickingMode = PickingMode.Position;
            _grid.RegisterCallback<GeometryChangedEvent>(_ => PlaceNodes());
            _empty = HudWidgets.Label(Config.Text.Get(SkillTreeTextIds.EmptyTree), "skill-empty");
            _empty.style.color = Theme.Text;
            _grid.Add(_empty);
            _window.Add(_grid);

            var details = HudWidgets.Box("skill-details");
            details.pickingMode = PickingMode.Position;
            _name = HudWidgets.Label("", "skill-name");
            _name.style.color = Theme.Accent;
            details.Add(_name);
            _info = HudWidgets.Label("", "skill-info");
            _info.style.color = Theme.Text;
            details.Add(_info);
            var buttons = Row("skill-buttons");
            _primary = Button("skill_primary", "", () => _actions.Primary());
            buttons.Add(_primary);
            _unbar = Button("skill_unbar", Config.Text.Get(SkillTreeTextIds.Unbar), () => _actions.Unbar());
            buttons.Add(_unbar);
            details.Add(buttons);
            _window.Add(details);

            var footer = Row("skill-footer");
            footer.style.justifyContent = Justify.SpaceBetween;
            _respec = Button("skill_respec", Config.Text.Get(SkillTreeTextIds.Respec), () => _actions.Respec());
            footer.Add(_respec);
            var hint = HudWidgets.Label(Config.Text.Get(SkillTreeTextIds.DragHint), "skill-hint");
            hint.style.color = Theme.Text;
            footer.Add(hint);
            _window.Add(footer);

            _advance = Row("skill-advance");
            var advanceTitle = HudWidgets.Label(Config.Text.Get(SkillTreeTextIds.AdvanceTitle), "skill-advance-title");
            advanceTitle.style.color = Theme.Accent;
            _advance.Add(advanceTitle);
            _window.Add(_advance);

            // The dragged skill follows the pointer over the whole HUD (it is dropped on a hotbar slot).
            _ghost = HudWidgets.Label("", "skill-ghost");
            _ghost.style.position = Position.Absolute;
            _ghost.style.color = Theme.Accent;
            _ghost.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(_ghost, Theme.Accent);
            Show(_ghost, false);
            View.Root.Add(_ghost);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
                return;
            HudWidgets.SetText(_points, Config.Text.Get(SkillTreeTextIds.Points, new TextArg("points", Number(_model.Points))));

            _tabs.Clear();
            foreach (var cls in _model.Tabs)
            {
                string id = cls.Id;
                var tab = Button("skill_tab_" + id, Config.Text.Get(UiStringCoverage.ClassName(id)), () => _actions.SelectTab(id));
                HudWidgets.Border(tab, id == _model.ActiveTab ? Theme.Accent : Theme.Border);
                _tabs.Add(tab);
            }

            foreach (var button in _nodeButtons.Values)
                button.RemoveFromHierarchy();
            _nodeButtons.Clear();
            foreach (var node in _model.Nodes)
            {
                var button = NodeElement(node);
                _nodeButtons[node.Id] = button;
                _grid.Add(button);
            }
            Show(_empty, _model.Nodes.Count == 0);
            PlaceNodes();

            RefreshDetails();
            _respec.SetEnabled(_model.Respec() != null);

            while (_advance.childCount > 1)
                _advance.RemoveAt(1);
            foreach (var cls in _model.Advances)
            {
                string id = cls.Id;
                _advance.Add(Button("skill_advance_" + id,
                    Config.Text.Get(SkillTreeTextIds.Advance, new TextArg("class", Config.Text.Get(UiStringCoverage.ClassName(id)))),
                    () => _actions.Advance(id)));
            }
            Show(_advance, _model.Advances.Count > 0);
        }

        private Button NodeElement(SkillNodeView node)
        {
            string id = node.Id;
            var button = new Button { name = "skill_node_" + id, focusable = false };
            button.AddToClassList("skill-node");
            button.AddToClassList("skill-node--" + node.State.ToString().ToLowerInvariant());
            button.style.position = Position.Absolute;
            button.style.fontSize = Theme.Tokens.FontSizeBody; // spec 9.8: never below the body size
            button.style.color = node.State == SkillNodeState.Unavailable ? Theme.Locked : Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            var border = id == _model.Selected ? Theme.Accent
                : node.State == SkillNodeState.Available || node.State == SkillNodeState.Upgradable ? Theme.Stamina
                : node.Level > 0 ? Theme.Border : Theme.Locked;
            HudWidgets.Border(button, border);
            button.text = Config.Text.Get(UiStringCoverage.SkillNodeName(id)) + "\n"
                          + Config.Text.Get(SkillTreeTextIds.NodeLevel, new TextArg("level", Number(node.Level)), new TextArg("max", Number(node.Node.MaxLevel)));
            button.RegisterCallback<PointerDownEvent>(e => OnNodeDown(id, e), TrickleDown.TrickleDown);
            return button;
        }

        /// <summary>Each node at its design row and column, the cells sized from the grid's current size.</summary>
        private void PlaceNodes()
        {
            var size = _grid.contentRect;
            if (_columns == 0 || _rows == 0 || float.IsNaN(size.width) || size.width <= 0f || size.height <= 0f)
                return;
            float cellWidth = size.width / _columns;
            float cellHeight = size.height / _rows;
            foreach (var node in _model.Nodes)
            {
                if (!_nodeButtons.TryGetValue(node.Id, out var button))
                    continue;
                button.style.left = node.Node.Col * cellWidth;
                button.style.top = node.Node.Row * cellHeight;
                button.style.width = cellWidth;
                button.style.height = cellHeight;
            }
        }

        private void RefreshDetails()
        {
            var node = _model.SelectedNode;
            Show(_primary, false);
            Show(_unbar, false);
            if (node == null)
            {
                HudWidgets.SetText(_name, "");
                HudWidgets.SetText(_info, "");
                return;
            }
            var def = node.Node;
            HudWidgets.SetText(_name, Config.Text.Get(UiStringCoverage.SkillNodeName(def.Id)));
            var lines = new List<string>
            {
                Config.Text.Get(SkillTreeTextIds.Kind(def.Kind)) + " - "
                + Config.Text.Get(SkillTreeTextIds.Level, new TextArg("level", Number(node.Level)), new TextArg("max", Number(def.MaxLevel))),
                Config.Text.Get(SkillTreeTextIds.MinLevel, new TextArg("level", Number(def.MinCharLevel))),
            };
            for (int i = 0; i < def.Requires.Length && i < def.RequiresLevels.Length; i++)
                lines.Add(Config.Text.Get(SkillTreeTextIds.Requires, new TextArg("name", Config.Text.Get(UiStringCoverage.SkillNodeName(def.Requires[i]))),
                    new TextArg("level", Number(def.RequiresLevels[i]))));
            if (def.EvolvesFrom.Length != 0)
                lines.Add(Config.Text.Get(SkillTreeTextIds.EvolvesFrom, new TextArg("name", Config.Text.Get(UiStringCoverage.SkillNodeName(def.EvolvesFrom)))));
            if (def.Weapons.Length != 0)
            {
                var names = new List<string>();
                foreach (var weapon in def.Weapons)
                    names.Add(Config.Text.Get(SkillTreeTextIds.Weapon(weapon)));
                lines.Add(Config.Text.Get(def.Kind == SkillNodeKind.WeaponUnlock ? SkillTreeTextIds.Unlocks : SkillTreeTextIds.Weapons,
                    new TextArg("weapons", string.Join(", ", names))));
            }
            if (node.Slot != 0)
                lines.Add(Config.Text.Get(SkillTreeTextIds.OnSlot, new TextArg("key", Config.Text.Get(HudTextIds.KeySlot(node.Slot)))));
            if (node.State == SkillNodeState.Unavailable)
                lines.Add(Config.Text.Get(SkillTreeTextIds.Unavailable));
            else if (node.Missing != SkillTreeReason.None)
                lines.Add(Config.Reasons.Text(node.Missing.ToString()));
            HudWidgets.SetText(_info, string.Join("\n", lines));

            if (_model.Primary() is { } intent)
            {
                _primary.text = Config.Text.Get(intent.Op == SkillTreeOp.Upgrade ? SkillTreeTextIds.Upgrade
                    : intent.Op == SkillTreeOp.Evolve ? SkillTreeTextIds.Evolve : SkillTreeTextIds.Learn);
                Show(_primary, true);
            }
            Show(_unbar, _model.Unbar() != null);
        }

        private void OnNodeDown(string nodeId, PointerDownEvent e)
        {
            if (e.button != 0)
                return;
            _actions.Select(nodeId);
            var node = _model.SelectedNode;
            if (node == null || !node.Draggable)
                return;
            _pressNode = nodeId;
            _pressAt = new Vector2(e.position.x, e.position.y);
            _dragging = false;
            _window.CapturePointer(e.pointerId);
            e.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            if (_pressNode.Length == 0)
                return;
            var at = new Vector2(e.position.x, e.position.y);
            if (!_dragging && (at - _pressAt).sqrMagnitude >= _dragThresholdPx * _dragThresholdPx)
            {
                _dragging = true;
                _ghost.text = Config.Text.Get(UiStringCoverage.SkillNodeName(_pressNode));
                Show(_ghost, true);
            }
            if (_dragging)
            {
                var local = View.Root.WorldToLocal(at);
                _ghost.style.left = local.x;
                _ghost.style.top = local.y;
            }
        }

        private void OnPointerUp(PointerUpEvent e)
        {
            if (_pressNode.Length == 0 || e.button != 0)
                return;
            string node = _pressNode;
            bool dragged = _dragging;
            if (_window.HasPointerCapture(e.pointerId))
                _window.ReleasePointer(e.pointerId);
            EndPress();
            if (dragged)
                _actions.Drop(node, new Vector2(e.position.x, e.position.y));
        }

        private void EndPress()
        {
            _pressNode = "";
            if (_dragging)
            {
                _dragging = false;
                Show(_ghost, false);
            }
        }

        private Button Button(string name, string text, System.Action onClick)
        {
            var button = new Button(onClick) { name = name, text = text, focusable = false };
            button.style.fontSize = Theme.Tokens.FontSizeBody;
            button.style.color = Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(button, Theme.Border);
            return button;
        }

        private static VisualElement Row(string className)
        {
            var row = HudWidgets.Box(className);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.pickingMode = PickingMode.Position;
            return row;
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
