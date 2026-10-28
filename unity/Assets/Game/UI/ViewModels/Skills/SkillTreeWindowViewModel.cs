#nullable enable
using System.Collections.Generic;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Skills;

namespace Game.UI.ViewModels.Skills
{
    /// <summary>How a node looks in the window (spec 3.5): from the committed state, the level and the points.</summary>
    public enum SkillNodeState
    {
        /// <summary>Not learnable now: a level, a prerequisite or a point is missing (the details say which).</summary>
        Locked,
        /// <summary>Learnable or evolvable now.</summary>
        Available,
        /// <summary>Learned, and one more level can be bought now.</summary>
        Upgradable,
        /// <summary>Learned (at its max level, or no point for the next).</summary>
        Learned,
        /// <summary>No behaviour yet (no <c>SkillDef</c> row): shown, never learnable.</summary>
        Unavailable,
    }

    /// <summary>One node of the tree on screen: its row, its learned level and its state.</summary>
    public sealed class SkillNodeView
    {
        internal SkillNodeView(Shared.Data.Tables.SkillTree node)
        {
            Node = node;
        }

        public Shared.Data.Tables.SkillTree Node { get; }

        public string Id => Node.Id;

        public int Level { get; internal set; }

        public SkillNodeState State { get; internal set; }

        /// <summary>Why it is locked (the first missing condition); <see cref="SkillTreeReason.None"/> otherwise.</summary>
        public SkillTreeReason Missing { get; internal set; }

        /// <summary>The hotbar slot it is on, 0 for none.</summary>
        public int Slot { get; internal set; }

        /// <summary>A learned active or buff: it can be dragged onto the hotbar.</summary>
        public bool Draggable => Level > 0 && Node.IsHotbarSkill();
    }

    /// <summary>What a button of the window asks the server for (sent by the binder).</summary>
    public readonly struct SkillIntent
    {
        public SkillIntent(SkillTreeOp op, string target, byte slot)
        {
            Op = op;
            Target = target;
            Slot = slot;
        }

        public SkillTreeOp Op { get; }

        public string Target { get; }

        public byte Slot { get; }
    }

    /// <summary>
    /// The skill tree window v1 (spec 3.5, R-3.5-01/04, 9.13 pattern; WP-M2-5): one tab per tree of the class line (root
    /// first), the active tab's nodes on the design grid with their states, the selected node, the points left, the classes
    /// the character can advance into, and the requests its buttons make. Pure: it reads the committed state the server
    /// sent (<see cref="SetSkills"/>) and the progression (<see cref="SetProgress"/>) and checks with the shared
    /// <see cref="SkillTreeRules"/>; the server decides. A request waits for its answer (<see cref="Pending"/>).
    /// </summary>
    public sealed class SkillTreeWindowViewModel : ViewModel
    {
        private readonly SkillRules? _rules;
        private readonly List<SkillNodeView> _nodes = new List<SkillNodeView>();
        private readonly List<ClassDef> _tabs = new List<ClassDef>();
        private readonly List<ClassDef> _advances = new List<ClassDef>();

        public SkillTreeWindowViewModel(SkillRules? rules)
        {
            _rules = rules;
        }

        public bool Open { get; private set; }

        public SkillState? Skills { get; private set; }

        public int Level { get; private set; } = 1;

        public int Points { get; private set; }

        /// <summary>The trees of the class line, root first.</summary>
        public IReadOnlyList<ClassDef> Tabs => _tabs;

        /// <summary>The class whose tree the grid shows.</summary>
        public string ActiveTab { get; private set; } = "";

        /// <summary>The active tree's nodes, in table order.</summary>
        public IReadOnlyList<SkillNodeView> Nodes => _nodes;

        /// <summary>The classes the character may advance into now (R-3.5-01: its children at their unlock level).</summary>
        public IReadOnlyList<ClassDef> Advances => _advances;

        public string Selected { get; private set; } = "";

        /// <summary>The request id the window waits for; 0 for none (its buttons are off meanwhile).</summary>
        public uint Pending { get; private set; }

        public SkillNodeView? SelectedNode
        {
            get
            {
                foreach (var node in _nodes)
                {
                    if (node.Id == Selected)
                        return node;
                }
                return null;
            }
        }

        public void SetOpen(bool open)
        {
            if (open == Open)
                return;
            Open = open;
            NotifyChanged();
        }

        public void Toggle() => SetOpen(!Open);

        public void SetSkills(SkillState skills)
        {
            // A class advance opens the new tree (R-3.5-04: a new tree becomes available).
            if (Skills == null || Skills.ClassId != skills.ClassId)
                ActiveTab = "";
            Skills = skills;
            Rebuild();
        }

        public void SetProgress(int level, int points)
        {
            if (level == Level && points == Points)
                return;
            Level = level;
            Points = points;
            Rebuild();
        }

        public void SelectTab(string classId)
        {
            if (classId == ActiveTab)
                return;
            ActiveTab = classId;
            Selected = "";
            Rebuild();
        }

        public void Select(string nodeId)
        {
            if (nodeId == Selected)
                return;
            Selected = nodeId;
            NotifyChanged();
        }

        /// <summary>The selected node's main button: learn, evolve or upgrade, when it can; null otherwise.</summary>
        public SkillIntent? Primary()
        {
            var node = SelectedNode;
            if (node == null || Pending != 0)
                return null;
            switch (node.State)
            {
                case SkillNodeState.Available:
                    return new SkillIntent(node.Node.EvolvesFrom.Length == 0 ? SkillTreeOp.Learn : SkillTreeOp.Evolve, node.Id, 0);
                case SkillNodeState.Upgradable:
                    return new SkillIntent(SkillTreeOp.Upgrade, node.Id, 0);
                default:
                    return null;
            }
        }

        /// <summary>Takes the selected node off its hotbar slot; null when it is on none.</summary>
        public SkillIntent? Unbar()
        {
            var node = SelectedNode;
            return node == null || node.Slot == 0 || Pending != 0 ? (SkillIntent?)null : new SkillIntent(SkillTreeOp.ClearSlot, "", (byte)node.Slot);
        }

        /// <summary>A learned node dropped on a hotbar slot (1-based); null when it cannot go there.</summary>
        public SkillIntent? Drop(string nodeId, int slot)
        {
            if (_rules == null || Skills == null || Pending != 0
                || SkillTreeRules.CheckAssign(_rules, Skills, slot, nodeId) != SkillTreeReason.None)
                return null;
            return new SkillIntent(SkillTreeOp.AssignSlot, nodeId, (byte)slot);
        }

        public SkillIntent? Respec() =>
            Skills != null && Skills.LearnedCount > 0 && Pending == 0 ? new SkillIntent(SkillTreeOp.Respec, "", 0) : (SkillIntent?)null;

        public SkillIntent? Advance(string classId)
        {
            if (Pending != 0)
                return null;
            foreach (var cls in _advances)
            {
                if (cls.Id == classId)
                    return new SkillIntent(SkillTreeOp.Advance, classId, 0);
            }
            return null;
        }

        /// <summary>A request went out (the binder sent it): the buttons wait for its answer.</summary>
        public void SetPending(uint requestId)
        {
            Pending = requestId;
            NotifyChanged();
        }

        /// <summary>The server answered <paramref name="result"/>: the wait ends (the state that shows it follows).</summary>
        public void OnAnswer(SkillTreeResult result)
        {
            if (result.RequestId != Pending)
                return;
            Pending = 0;
            NotifyChanged();
        }

        /// <summary>The connection ended: nothing is known any more.</summary>
        public void Clear()
        {
            Skills = null;
            Pending = 0;
            Selected = "";
            ActiveTab = "";
            Rebuild();
        }

        private void Rebuild()
        {
            _nodes.Clear();
            _tabs.Clear();
            _advances.Clear();
            if (_rules != null && Skills != null)
            {
                foreach (var cls in _rules.Line(Skills.ClassId))
                    _tabs.Add(cls);
                if (ActiveTab.Length == 0 || !_rules.OnLine(Skills.ClassId, ActiveTab))
                    ActiveTab = Skills.ClassId;
                foreach (var node in _rules.Tree(ActiveTab))
                    _nodes.Add(View(node));
                foreach (var cls in _rules.Children(Skills.ClassId))
                {
                    if (SkillTreeRules.CheckAdvance(_rules, Skills, cls.Id, Level, blocked: false) == SkillTreeReason.None)
                        _advances.Add(cls);
                }
            }
            NotifyChanged();
        }

        private SkillNodeView View(Shared.Data.Tables.SkillTree node)
        {
            var skills = Skills!;
            var view = new SkillNodeView(node) { Level = skills.LevelOf(node.Id), Slot = skills.SlotOf(node.Id) };
            if (_rules!.Skill(node.Id) == null)
            {
                view.State = SkillNodeState.Unavailable;
                view.Missing = SkillTreeReason.SkillUnavailable;
                return view;
            }
            if (view.Level > 0)
            {
                var upgrade = SkillTreeRules.CheckUpgrade(_rules, skills, node.Id, Points, out _);
                view.State = upgrade == SkillTreeReason.None ? SkillNodeState.Upgradable : SkillNodeState.Learned;
                view.Missing = upgrade == SkillTreeReason.MaxLevelReached ? SkillTreeReason.None : upgrade;
                return view;
            }
            // Combat is the server's to judge (it refuses with its reason); the window shows what the tree allows.
            var reason = node.EvolvesFrom.Length == 0
                ? SkillTreeRules.CheckLearn(_rules, skills, node.Id, Level, Points, out _)
                : SkillTreeRules.CheckEvolve(_rules, skills, node.Id, Level, Points, blocked: false, out _);
            view.State = reason == SkillTreeReason.None ? SkillNodeState.Available : SkillNodeState.Locked;
            view.Missing = reason;
            return view;
        }
    }
}
