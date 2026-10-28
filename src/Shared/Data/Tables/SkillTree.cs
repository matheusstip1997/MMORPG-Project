using System;
using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>R-3.5-03: what a skill-tree node is. A buff is used from the hotbar and counts as an active skill.</summary>
    public enum SkillNodeKind : byte
    {
        Active = 1,
        Buff = 2,
        Passive = 3,
        /// <summary>R-3.5-07: one level that lets the class line use more weapon types (<see cref="SkillTree.Weapons"/>).</summary>
        WeaponUnlock = 4,
    }

    /// <summary>
    /// One node of a class's skill tree (spec 3.5 Implementation, R-3.5-04; catalog 8.5). **Generated** from
    /// <c>docs/design/skill-trees.json</c> by <c>src/Tools/SkillTreeGen</c>: do not edit the data file by hand. A node's
    /// behaviour is its <see cref="SkillDef"/> row (hand-authored); a node without one cannot be learned yet. Numbers
    /// follow <see cref="SkillTreeSettings"/> (user decision 2026-10-27, placeholders): max level 5 (weapon unlocks 1), one
    /// point per level, minimum level the class's unlock level plus a step per design row.
    /// </summary>
    [Table("SkillTree", 1)]
    public sealed class SkillTree : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Node ids travel in reliable messages as UTF-8 strings (<c>SkillTreeMessages</c>); ASCII, at most this long.</summary>
        public const int MaxIdBytes = 48;

        public const int MaxLevelCap = 100;

        /// <summary>The node id, <c>class.skillName</c> (the design file's id), stored in <c>character_skills.node_id</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>The id at the root of the node's <see cref="EvolvesFrom"/> chain (its own id when it evolves from nothing).</summary>
        [Ref(typeof(SkillTree))] public string Family { get; set; } = string.Empty;

        /// <summary>The class whose tree holds the node.</summary>
        [Ref(typeof(ClassDef))] public string ClassId { get; set; } = string.Empty;

        /// <summary>R-3.5-04: the character level needed to learn the node.</summary>
        [Range(1, 10_000)] public int MinCharLevel { get; set; }

        /// <summary>R-3.5-04: nodes of the same tree to learn first, each at <see cref="RequiresLevels"/> of the same index.</summary>
        [Ref(typeof(SkillTree))] public string[] Requires { get; set; } = Array.Empty<string>();

        public int[] RequiresLevels { get; set; } = Array.Empty<int>();

        [Range(1, MaxLevelCap)] public int MaxLevel { get; set; }

        /// <summary>Skill points per level learned (R-3.5-04).</summary>
        [Range(1, 1_000)] public int PointCost { get; set; }

        public SkillNodeKind Kind { get; set; }

        /// <summary>R-3.5-04: the node of an earlier tier of the same class line this one replaces; empty for none.</summary>
        [Ref(typeof(SkillTree), Optional = true)] public string EvolvesFrom { get; set; } = string.Empty;

        /// <summary>
        /// R-3.5-07: for a skill, the weapon types it needs (any of them, in either slot; empty means any); for a weapon
        /// unlock, the types it lets the class line use.
        /// </summary>
        public WeaponType[] Weapons { get; set; } = Array.Empty<WeaponType>();

        /// <summary>The elements the skill uses, one per effect (spec 3.5, R-3.4-04).</summary>
        public Element[] Elements { get; set; } = Array.Empty<Element>();

        /// <summary>Grid position in the design file and the skill tree window (row 0 at the top).</summary>
        [Range(0, 100)] public int Row { get; set; }

        [Range(0, 100)] public int Col { get; set; }

        /// <summary>An active or a buff: used from the hotbar (R-3.5-03).</summary>
        public bool IsHotbarSkill() => Kind == SkillNodeKind.Active || Kind == SkillNodeKind.Buff;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            else if (!IsAsciiId(Id))
                errors.Add(nameof(Id), $"must be ASCII and at most {MaxIdBytes} bytes");
            if (!Enum.IsDefined(typeof(SkillNodeKind), Kind))
                errors.Add(nameof(Kind), "unknown node kind");
            if (Requires.Length != RequiresLevels.Length)
                errors.Add(nameof(RequiresLevels), $"has {RequiresLevels.Length} levels for {Requires.Length} required nodes");
            for (int i = 0; i < RequiresLevels.Length; i++)
            {
                if (RequiresLevels[i] < 1)
                    errors.Add(nameof(RequiresLevels), $"level {RequiresLevels[i]} for '{(i < Requires.Length ? Requires[i] : "?")}' is below 1");
            }
            for (int i = 0; i < Requires.Length; i++)
            {
                if (Requires[i] == Id)
                    errors.Add(nameof(Requires), "a node cannot require itself");
                else if (Array.IndexOf(Requires, Requires[i]) != i)
                    errors.Add(nameof(Requires), $"lists '{Requires[i]}' twice");
            }
            if (EvolvesFrom == Id)
                errors.Add(nameof(EvolvesFrom), "a node cannot evolve from itself");
            for (int i = 0; i < Weapons.Length; i++)
            {
                if (Weapons[i] == WeaponType.None || !Enum.IsDefined(typeof(WeaponType), Weapons[i]))
                    errors.Add(nameof(Weapons), $"{(int)Weapons[i]} is not a weapon type");
                else if (Array.IndexOf(Weapons, Weapons[i]) != i)
                    errors.Add(nameof(Weapons), $"lists {Weapons[i]} twice");
            }
            foreach (var element in Elements)
            {
                if (element == Element.None || !Enum.IsDefined(typeof(Element), element))
                    errors.Add(nameof(Elements), $"{(int)element} is not an element");
            }
            if (Kind == SkillNodeKind.WeaponUnlock && Weapons.Length == 0)
                errors.Add(nameof(Weapons), "a weapon unlock names the weapon types it unlocks (R-3.5-07)");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var settings = SkillTreeSettings.Find(set);
            if (settings != null && Kind == SkillNodeKind.WeaponUnlock && MaxLevel != settings.WeaponUnlockMaxLevel)
                errors.Add(nameof(MaxLevel), $"a weapon unlock has {settings.WeaponUnlockMaxLevel} level (SkillTreeSettings), not {MaxLevel}");

            var cls = ClassDef.Find(set, ClassId);
            if (cls != null && MinCharLevel < cls.UnlockLevel)
                errors.Add(nameof(MinCharLevel), $"{MinCharLevel} is below the unlock level {cls.UnlockLevel} of '{ClassId}' (R-3.5-04)");

            // R-3.5-04: a prerequisite is a link inside one tree, at a level the required node has.
            for (int i = 0; i < Requires.Length && i < RequiresLevels.Length; i++)
            {
                var required = Find(set, Requires[i]);
                if (required == null)
                    continue;
                if (required.ClassId != ClassId)
                    errors.Add(nameof(Requires), $"'{required.Id}' is in the tree of '{required.ClassId}', not '{ClassId}' (R-3.5-04)");
                if (RequiresLevels[i] > required.MaxLevel)
                    errors.Add(nameof(RequiresLevels), $"'{required.Id}' at level {RequiresLevels[i]} is above its max level {required.MaxLevel}");
            }
            if (RequiresCycle(set, this))
                errors.Add(nameof(Requires), $"'{Id}' requires itself through its prerequisites");

            // R-3.5-04: a skill evolves only from the skill it names, of an earlier tier of the same class line.
            if (EvolvesFrom.Length != 0)
            {
                var from = Find(set, EvolvesFrom);
                if (from != null && !ClassLines.IsStrictAncestor(set, from.ClassId, ClassId))
                    errors.Add(nameof(EvolvesFrom), $"'{EvolvesFrom}' (tree of '{from.ClassId}') is not on an earlier tier of the line of '{ClassId}' (R-3.5-04)");
                if (from != null && from.Kind == SkillNodeKind.WeaponUnlock)
                    errors.Add(nameof(EvolvesFrom), $"'{EvolvesFrom}' is a weapon unlock, which does not evolve");
            }
            string family = FamilyOf(set, this);
            if (family.Length != 0 && family != Family)
                errors.Add(nameof(Family), $"is '{Family}', the root of the evolution chain is '{family}'");
        }

        public static SkillTree? Find(ConfigSet set, string id) => NetIds.Find<SkillTree>(set, id, row => row.Id);

        public static List<SkillTree> Rows(ConfigSet set) => NetIds.Rows<SkillTree>(set);

        public static bool IsAsciiId(string id)
        {
            if (id.Length == 0 || id.Length > MaxIdBytes)
                return false;
            foreach (char c in id)
            {
                if (c < 0x21 || c > 0x7E)
                    return false;
            }
            return true;
        }

        /// <summary>The root of the node's evolution chain; empty when the chain is broken or loops (reported elsewhere).</summary>
        private static string FamilyOf(ConfigSet set, SkillTree node)
        {
            var current = node;
            for (int guard = 0; guard < 16; guard++)
            {
                if (current.EvolvesFrom.Length == 0)
                    return current.Id;
                var next = Find(set, current.EvolvesFrom);
                if (next == null)
                    return string.Empty;
                current = next;
            }
            return string.Empty;
        }

        private static bool RequiresCycle(ConfigSet set, SkillTree start)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<SkillTree>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                foreach (var id in node.Requires)
                {
                    if (id == start.Id)
                        return true;
                    var next = Find(set, id);
                    if (next != null && seen.Add(id))
                        stack.Push(next);
                }
            }
            return false;
        }
    }
}
