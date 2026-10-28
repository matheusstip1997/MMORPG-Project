using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Simulation.Skills
{
    /// <summary>A <see cref="SkillDef"/> with its inheritance resolved (R-3.5-05): every field holds a value.</summary>
    public sealed class ResolvedSkill
    {
        public ResolvedSkill(string id, string[] chain, string actionId, int damagePercentPerLevel, ArmorClass armorClass,
            SkillModifierDef[] modifiers)
        {
            Id = id;
            Chain = chain;
            ActionId = actionId;
            DamagePercentPerLevel = damagePercentPerLevel;
            ArmorClass = armorClass;
            Modifiers = modifiers;
        }

        public string Id { get; }

        /// <summary>The rows the fields came from, this one first, then its parents.</summary>
        public string[] Chain { get; }

        /// <summary>The hotbar action; empty for a passive or a weapon unlock.</summary>
        public string ActionId { get; }

        public int DamagePercentPerLevel { get; }

        /// <summary>0 when the skill sets no armor tier.</summary>
        public ArmorClass ArmorClass { get; }

        public SkillModifierDef[] Modifiers { get; }

        /// <summary>The damage and heal multiplier of a skill at <paramref name="level"/>, in percent (100 at level 1).</summary>
        public int LevelPercent(int level) => 100 + Math.Max(0, level - 1) * DamagePercentPerLevel;
    }

    /// <summary>
    /// Resolves <see cref="SkillDef"/> inheritance (spec 3.5 Implementation, R-3.5-05): a field a row leaves unset comes
    /// from its parent, and so on up the chain; a row without <see cref="SkillModifierDef"/> rows takes the nearest
    /// parent's. Used by the validator, the server, the client and <c>DataValidator --skill</c>.
    /// </summary>
    public static class SkillDefResolver
    {
        /// <summary>Longer chains are refused (an evolution chain has at most one node per class tier).</summary>
        public const int MaxChain = ClassDef.MaxTier + 1;

        public readonly struct Rows
        {
            public Rows(IReadOnlyDictionary<string, SkillDef> skills, IReadOnlyList<SkillModifierDef> modifiers)
            {
                Skills = skills;
                Modifiers = modifiers;
            }

            public IReadOnlyDictionary<string, SkillDef> Skills { get; }

            public IReadOnlyList<SkillModifierDef> Modifiers { get; }
        }

        public static Rows RowsOf(ConfigSet set)
        {
            var skills = new Dictionary<string, SkillDef>(StringComparer.Ordinal);
            foreach (var row in NetIdsRows<SkillDef>(set))
                skills[row.Id] = row;
            return new Rows(skills, NetIdsRows<SkillModifierDef>(set));
        }

        public static bool TryResolve(IReadOnlyDictionary<string, SkillDef> skills, IReadOnlyList<SkillModifierDef> modifiers,
            string id, out ResolvedSkill resolved, out string error)
        {
            resolved = null!;
            var chain = new List<SkillDef>();
            string next = id;
            while (next.Length != 0)
            {
                if (!skills.TryGetValue(next, out var row))
                {
                    error = chain.Count == 0 ? $"no SkillDef row '{id}'" : $"'{chain[chain.Count - 1].Id}' inherits a missing SkillDef '{next}'";
                    return false;
                }
                foreach (var seen in chain)
                {
                    if (seen.Id == row.Id)
                    {
                        error = $"'{id}' inherits itself through its parents";
                        return false;
                    }
                }
                if (chain.Count == MaxChain)
                {
                    error = $"'{id}' has more than {MaxChain} rows on its inheritance chain";
                    return false;
                }
                chain.Add(row);
                next = row.Parent;
            }

            string actionId = string.Empty;
            int perLevel = SkillDef.Inherit;
            ArmorClass armor = 0;
            SkillModifierDef[]? mods = null;
            var ids = new string[chain.Count];
            for (int i = 0; i < chain.Count; i++)
            {
                var row = chain[i];
                ids[i] = row.Id;
                if (actionId.Length == 0)
                    actionId = row.ActionId;
                if (perLevel == SkillDef.Inherit)
                    perLevel = row.DamagePercentPerLevel;
                if (armor == 0)
                    armor = row.ArmorClass;
                if (mods == null)
                {
                    var own = ModifiersOf(modifiers, row.Id);
                    if (own.Length != 0)
                        mods = own;
                }
            }
            resolved = new ResolvedSkill(id, ids, actionId, perLevel == SkillDef.Inherit ? 0 : perLevel, armor,
                mods ?? Array.Empty<SkillModifierDef>());
            error = string.Empty;
            return true;
        }

        private static SkillModifierDef[] ModifiersOf(IReadOnlyList<SkillModifierDef> modifiers, string skillId)
        {
            var own = new List<SkillModifierDef>();
            for (int i = 0; i < modifiers.Count; i++)
            {
                if (modifiers[i].SkillId == skillId)
                    own.Add(modifiers[i]);
            }
            return own.ToArray();
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
