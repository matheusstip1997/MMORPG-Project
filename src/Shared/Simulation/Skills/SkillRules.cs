using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Simulation.Skills
{
    /// <summary>
    /// The skill tree as runtime data (spec 3.5, WP-M2-5): nodes, resolved skills, class lines, their weapon types and
    /// default hotbar layouts, built once from the validated tables. Read-only; shared by the server, client prediction,
    /// the skill tree window and the bots, so every side checks and builds loadouts alike.
    /// </summary>
    public sealed class SkillRules
    {
        private readonly Dictionary<string, SkillTree> _nodes = new Dictionary<string, SkillTree>(StringComparer.Ordinal);
        private readonly Dictionary<string, ResolvedSkill> _skills = new Dictionary<string, ResolvedSkill>(StringComparer.Ordinal);
        private readonly Dictionary<string, ClassDef> _classes = new Dictionary<string, ClassDef>(StringComparer.Ordinal);
        private readonly Dictionary<string, ClassDef[]> _lines = new Dictionary<string, ClassDef[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ClassDef>> _children = new Dictionary<string, List<ClassDef>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<SkillTree>> _trees = new Dictionary<string, List<SkillTree>>(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> _classWeapons = new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly Dictionary<string, ActionBindings> _bindings = new Dictionary<string, ActionBindings>(StringComparer.Ordinal);
        private readonly List<SkillTree> _orderedNodes = new List<SkillTree>();

        private SkillRules(SkillTreeSettings settings)
        {
            Settings = settings;
        }

        public SkillTreeSettings Settings { get; }

        /// <summary>Every node in table order (class by class, then by design row and column).</summary>
        public IReadOnlyList<SkillTree> Nodes => _orderedNodes;

        /// <summary>Builds the rules from a validated config set; null when the skill tables are not loaded.</summary>
        public static SkillRules? From(ConfigSet set)
        {
            var settings = SkillTreeSettings.Find(set);
            if (settings == null)
                return null;
            var rules = new SkillRules(settings);
            foreach (var cls in NetIdsRows<ClassDef>(set))
                rules._classes[cls.Id] = cls;
            foreach (var cls in rules._classes.Values)
            {
                var line = ClassLines.LineOf(set, cls.Id).ToArray();
                rules._lines[cls.Id] = line;
                if (cls.Parent.Length != 0)
                {
                    if (!rules._children.TryGetValue(cls.Parent, out var list))
                        rules._children[cls.Parent] = list = new List<ClassDef>();
                    list.Add(cls);
                }
                var bindings = ClassLines.BindingsFor(set, cls.Id);
                if (bindings != null)
                    rules._bindings[cls.Id] = bindings;
            }
            foreach (var gear in NetIdsRows<ClassGearDef>(set))
                rules._classWeapons[gear.Id] = WeaponMasks.Of(gear.WeaponTypes);
            var rows = SkillDefResolver.RowsOf(set);
            foreach (var node in NetIdsRows<SkillTree>(set))
            {
                rules._nodes[node.Id] = node;
                rules._orderedNodes.Add(node);
                if (!rules._trees.TryGetValue(node.ClassId, out var tree))
                    rules._trees[node.ClassId] = tree = new List<SkillTree>();
                tree.Add(node);
                if (rows.Skills.ContainsKey(node.Id) && SkillDefResolver.TryResolve(rows.Skills, rows.Modifiers, node.Id, out var skill, out _))
                    rules._skills[node.Id] = skill;
            }
            return rules;
        }

        public SkillTree? Node(string nodeId) => _nodes.TryGetValue(nodeId, out var node) ? node : null;

        /// <summary>The node's resolved behaviour; null when it has no <see cref="SkillDef"/> row yet (it cannot be learned).</summary>
        public ResolvedSkill? Skill(string nodeId) => _skills.TryGetValue(nodeId, out var skill) ? skill : null;

        public ClassDef? Class(string classId) => _classes.TryGetValue(classId, out var cls) ? cls : null;

        /// <summary>The class line, root first (R-3.5-01); empty for an unknown class.</summary>
        public IReadOnlyList<ClassDef> Line(string classId) =>
            _lines.TryGetValue(classId, out var line) ? line : Array.Empty<ClassDef>();

        /// <summary>The classes a character of this class can advance into (R-3.5-01).</summary>
        public IReadOnlyList<ClassDef> Children(string classId) =>
            _children.TryGetValue(classId, out var list) ? list : (IReadOnlyList<ClassDef>)Array.Empty<ClassDef>();

        /// <summary>The nodes of one class's tree (an empty tree has nothing to learn).</summary>
        public IReadOnlyList<SkillTree> Tree(string classId) =>
            _trees.TryGetValue(classId, out var tree) ? tree : (IReadOnlyList<SkillTree>)Array.Empty<SkillTree>();

        /// <summary>The attack, dodge and parry of the class line and its default hotbar layout (spec 9.6).</summary>
        public ActionBindings? Bindings(string classId) => _bindings.TryGetValue(classId, out var row) ? row : null;

        /// <summary>True when <paramref name="classId"/> is on the line of <paramref name="lineOf"/> (itself included).</summary>
        public bool OnLine(string lineOf, string classId)
        {
            foreach (var cls in Line(lineOf))
            {
                if (cls.Id == classId)
                    return true;
            }
            return false;
        }

        /// <summary>The tier of a node's class (0 for a root tree); -1 for an unknown class.</summary>
        public int TierOf(SkillTree node) => Class(node.ClassId)?.Tier ?? -1;

        /// <summary>The weapon types the class rows of a line list (<see cref="ClassGearDef"/>), as a mask.</summary>
        public uint ClassWeapons(string classId) => _classWeapons.TryGetValue(classId, out var mask) ? mask : 0;

        /// <summary>
        /// The M1 kit of a class line as nodes (the skills of its <see cref="ActionBindings"/> slots; spec 3.5 backlog E):
        /// what existing characters were given when skills became learned (realm migration V0008).
        /// </summary>
        public List<string> KitNodes(string classId)
        {
            var kit = new List<string>();
            var bindings = Bindings(classId);
            if (bindings == null)
                return kit;
            foreach (var node in _orderedNodes)
            {
                var skill = Skill(node.Id);
                if (skill != null && skill.ActionId.Length != 0 && OnLine(classId, node.ClassId)
                    && Array.IndexOf(bindings.SkillSlots, skill.ActionId) >= 0)
                    kit.Add(node.Id);
            }
            return kit;
        }

        private static List<T> NetIdsRows<T>(ConfigSet set) where T : class
        {
            var rows = new List<T>();
            if (set.TryGet(typeof(T), out var table))
            {
                foreach (var row in table.Rows)
                    rows.Add((T)row);
            }
            return rows;
        }
    }
}
