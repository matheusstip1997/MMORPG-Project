using System;
using Shared.Simulation.Skills;

namespace Shared.Data.Tables
{
    /// <summary>
    /// What a skill-tree node does (spec 3.5 Implementation, R-3.5-05; catalog 8.5): hand-authored, one row per
    /// <see cref="SkillTree"/> node that can be learned. A row inherits its <see cref="Parent"/> (a node on its evolution
    /// chain) and overrides only the fields it sets; <c>SkillDefResolver</c> resolves it and
    /// <c>dotnet run --project src/Tools/DataValidator -- --skill &lt;id&gt;</c> prints the result. An active or buff names
    /// its <see cref="ActionId"/>; a passive has <see cref="SkillModifierDef"/> rows or an <see cref="ArmorClass"/>
    /// (R-3.5-08); a weapon unlock has neither (its weapons are on the node). A row with no modifier rows inherits its
    /// parent's. <c>SkillTreeGen</c> appends a stub row (id and parent) for each new node.
    /// </summary>
    [Table("SkillDef", 1)]
    public sealed class SkillDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary><see cref="DamagePercentPerLevel"/> not set: inherited.</summary>
        public const int Inherit = -1;

        [TableId, Ref(typeof(SkillTree))] public string Id { get; set; } = string.Empty;

        /// <summary>The skill this one inherits from, on the node's evolution chain; empty for none.</summary>
        [Optional, Ref(typeof(SkillDef), Optional = true)] public string Parent { get; set; } = string.Empty;

        /// <summary>The action an active or buff starts from the hotbar (kind skill); empty inherits.</summary>
        [Optional, Ref(typeof(ActionDef), Optional = true)] public string ActionId { get; set; } = string.Empty;

        /// <summary>
        /// User decision 2026-10-27 (WP-M2-5 plan): each level above 1 adds this many percent to the action's damage and
        /// heal percents (placeholder 10). <see cref="Inherit"/> inherits; unset on the whole chain is 0.
        /// </summary>
        [Optional, Range(Inherit, 1_000)] public int DamagePercentPerLevel { get; set; } = Inherit;

        /// <summary>R-3.5-08: an armor passive's tier (the last one on the class line wins); not set (0) inherits.</summary>
        [Optional] public ArmorClass ArmorClass { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (Parent == Id)
                errors.Add(nameof(Parent), "a skill cannot inherit from itself");
            if (ArmorClass != 0 && !Enum.IsDefined(typeof(ArmorClass), ArmorClass))
                errors.Add(nameof(ArmorClass), "unknown armor class");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var node = SkillTree.Find(set, Id);
            if (node == null)
                return;
            // R-3.5-05: an evolution inherits a skill of its own evolution chain.
            if (Parent.Length != 0 && !OnEvolutionChain(set, node, Parent))
                errors.Add(nameof(Parent), $"'{Parent}' is not on the evolution chain of '{Id}' (R-3.5-05)");

            var rows = SkillDefResolver.RowsOf(set);
            if (!SkillDefResolver.TryResolve(rows.Skills, rows.Modifiers, Id, out var skill, out string error))
            {
                errors.Add(nameof(Parent), error);
                return;
            }
            switch (node.Kind)
            {
                case SkillNodeKind.Active:
                case SkillNodeKind.Buff:
                    var action = skill.ActionId.Length != 0 ? ActionDef.Find(set, skill.ActionId) : null;
                    if (skill.ActionId.Length == 0)
                        errors.Add(nameof(ActionId), $"'{Id}' is an {node.Kind} node and needs an action");
                    else if (action != null && action.Kind != ActionKind.Skill)
                        errors.Add(nameof(ActionId), $"'{skill.ActionId}' is a {action.Kind} action, expected Skill");
                    if (skill.ArmorClass != 0)
                        errors.Add(nameof(ArmorClass), "only a passive sets an armor tier (R-3.5-08)");
                    break;
                case SkillNodeKind.Passive:
                    if (skill.ActionId.Length != 0)
                        errors.Add(nameof(ActionId), "a passive has no action");
                    if (skill.Modifiers.Length == 0 && skill.ArmorClass == 0)
                        errors.Add(nameof(Id), $"passive '{Id}' has no SkillModifierDef rows and no armor tier");
                    break;
                case SkillNodeKind.WeaponUnlock:
                    if (skill.ActionId.Length != 0 || skill.Modifiers.Length != 0 || skill.ArmorClass != 0)
                        errors.Add(nameof(Id), "a weapon unlock has no action, modifiers or armor tier (R-3.5-07)");
                    break;
            }
        }

        public static SkillDef? Find(ConfigSet set, string id) => NetIds.Find<SkillDef>(set, id, row => row.Id);

        private static bool OnEvolutionChain(ConfigSet set, SkillTree node, string id)
        {
            var current = node;
            for (int guard = 0; guard < 16 && current.EvolvesFrom.Length != 0; guard++)
            {
                if (current.EvolvesFrom == id)
                    return true;
                var next = SkillTree.Find(set, current.EvolvesFrom);
                if (next == null)
                    return false;
                current = next;
            }
            return false;
        }
    }

    /// <summary>When a passive's modifier applies (the fixed trigger list of spec 3.5 Implementation, WP-M2-5).</summary>
    public enum SkillPassiveTrigger : byte
    {
        Always = 0,
        /// <summary>While one of <see cref="SkillModifierDef.Weapons"/> is equipped (Knight's Oath with a shield).</summary>
        WithWeapon = 1,
        /// <summary>While none of them is equipped (Sword Combat without a shield).</summary>
        WithoutWeapon = 2,
    }

    /// <summary>
    /// A passive skill's stat modifier (spec 3.5 Implementation, R-3.5-03; catalog 8.5): one row per stat, like
    /// <see cref="StatusModifierDef"/>, scaled by the skill's level (user decision 2026-10-27). The server adds them with
    /// source type skill (the skill tree) while <see cref="Trigger"/> holds.
    /// </summary>
    [Table("SkillModifierDef", 1)]
    public sealed class SkillModifierDef : IValidatableRow, ISetValidatableRow
    {
        public const int MaxPerSkill = 4;

        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(SkillDef))] public string SkillId { get; set; } = string.Empty;

        [Ref(typeof(StatDef))] public string Stat { get; set; } = string.Empty;

        /// <summary>Added to the stat's base per skill level (spec 3.4 formula).</summary>
        [Range(-1_000_000, 1_000_000)] public int FlatPerLevel { get; set; }

        /// <summary>Whole percents of the stat per skill level; percents of all sources add up (spec 3.4).</summary>
        [Range(-100, 1_000)] public int PercentPerLevel { get; set; }

        [Optional] public SkillPassiveTrigger Trigger { get; set; }

        /// <summary>The weapon types the trigger looks at (any of them, either slot); empty for <c>always</c>.</summary>
        [Optional] public WeaponType[] Weapons { get; set; } = Array.Empty<WeaponType>();

        /// <summary>True when the modifier applies with this equipped set.</summary>
        public bool Applies(uint equippedWeaponMask)
        {
            if (Trigger == SkillPassiveTrigger.Always)
                return true;
            bool any = (equippedWeaponMask & WeaponMasks.Of(Weapons)) != 0;
            return Trigger == SkillPassiveTrigger.WithWeapon ? any : !any;
        }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (FlatPerLevel == 0 && PercentPerLevel == 0)
                errors.Add(nameof(FlatPerLevel), "a modifier must change something (flat or percent)");
            if (!Enum.IsDefined(typeof(SkillPassiveTrigger), Trigger))
                errors.Add(nameof(Trigger), "unknown trigger");
            if (Trigger == SkillPassiveTrigger.Always && Weapons.Length != 0)
                errors.Add(nameof(Weapons), "an 'always' modifier names no weapons");
            if (Trigger != SkillPassiveTrigger.Always && Weapons.Length == 0)
                errors.Add(nameof(Weapons), $"a '{Trigger}' modifier names the weapon types it looks at");
            foreach (var weapon in Weapons)
            {
                if (weapon == WeaponType.None || !Enum.IsDefined(typeof(WeaponType), weapon))
                    errors.Add(nameof(Weapons), $"{(int)weapon} is not a weapon type");
            }
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (!set.TryGet(typeof(SkillModifierDef), out var table))
                return;
            int count = 0;
            bool repeated = false;
            foreach (var row in table.Rows)
            {
                var other = (SkillModifierDef)row;
                if (other.SkillId != SkillId)
                    continue;
                count++;
                repeated |= !ReferenceEquals(other, this) && other.Stat == Stat && other.Trigger == Trigger;
            }
            if (repeated)
                errors.Add(nameof(Stat), $"'{SkillId}' changes '{Stat}' twice with the same trigger; use one row");
            if (count > MaxPerSkill)
                errors.Add(nameof(SkillId), $"'{SkillId}' has {count} modifiers, at most {MaxPerSkill}");
        }
    }

    /// <summary>Weapon type sets as bit masks (bit n is <see cref="WeaponType"/> n), for the hot paths (R-3.5-07).</summary>
    public static class WeaponMasks
    {
        public static uint Bit(WeaponType type) => type == WeaponType.None ? 0u : 1u << (int)type;

        public static uint Of(WeaponType[] types)
        {
            uint mask = 0;
            foreach (var type in types)
                mask |= Bit(type);
            return mask;
        }

        public static uint Of(System.Collections.Generic.IReadOnlyList<WeaponType> types)
        {
            uint mask = 0;
            for (int i = 0; i < types.Count; i++)
                mask |= Bit(types[i]);
            return mask;
        }
    }
}
