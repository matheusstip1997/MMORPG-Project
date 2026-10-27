using System;
using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>
    /// The combat numbers an entity starts from, shared by <see cref="ClassDef"/> and <see cref="MonsterDef"/>: base values
    /// of the <c>StatDef</c> stats code reads by name (spec 3.4), poise (R-3.3-01) and flinch (R-3.3-07). The server builds
    /// a combatant from it (backlog D5 wiring).
    /// </summary>
    public interface ICombatantTemplate
    {
        string Id { get; }
        int MaxHp { get; }
        int MaxMp { get; }
        int HpRegen { get; }
        int MpRegen { get; }
        int Atk { get; }
        int Matk { get; }
        int Def { get; }
        int Mdef { get; }
        int PoiseMax { get; }
        int FlinchResist { get; }
        int FlinchDurationPercent { get; }
        int FlinchImmunityMs { get; }
        int BodyRadiusCm { get; }
    }

    /// <summary>
    /// A class (spec 3.5, catalog 8.5) v0: the 4 root classes of M1 with placeholder numbers (backlog E tunes them, K
    /// sets real ones). A character's <c>class_id</c> names a row; the game server builds the player's combatant from it at
    /// spawn. Stamina is the same for every player (<c>StartingValuesM1.staminaPool</c>, <c>staminaRegenPerSec</c>).
    /// Primary attributes (R-3.4-01, spec 3.4 "M2 primary attributes", user decision 2026-10-27): the class holds STR, DEX,
    /// STA and INT at level 1 and their growth per level; there are no free points. The derived stat fields below are the
    /// class base, to which <see cref="AttributeFormula"/> adds the attributes' share. The starting kit is the class's <see cref="ActionBindings"/> row (backlog E). TODO(M2):
    /// the advanced classes of the tree; the class gauge is a locked frame in M1 (spec 9.3).
    /// </summary>
    [Table("ClassDef", 1)]
    public sealed class ClassDef : IValidatableRow, ISetValidatableRow, ICombatantTemplate
    {
        /// <summary>Highest <see cref="NetId"/>: spawn events send it as their 16-bit definition id (<c>Snapshot.DefIdBits</c>).</summary>
        public const int MaxNetId = 65_535;

        /// <summary>The largest target radius <c>HitShape.Overlaps</c> takes (<c>HitShape.MaxTargetRadiusRaw</c>, 5000 cm).</summary>
        public const int MaxBodyRadiusCm = 5_000;

        /// <summary>Stable id stored in <c>characters.class_id</c>, for example <c>warrior</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Definition id of a player's spawn event (spec 1.7), so clients pick its visual; unique, never reused.</summary>
        [Range(1, MaxNetId)] public int NetId { get; set; }

        [Ref(typeof(EntityVisualDef))] public string VisualId { get; set; } = string.Empty;

        /// <summary>
        /// R-3.3-05, R-3.5-08: armor class comes from the class, not from equipment. It decides which parry colors the
        /// character can parry (R-3.3-04: yellow needs heavy, blue medium or heavy).
        /// </summary>
        public ArmorClass ArmorClass { get; set; }

        [Range(1, 1_000_000)] public int MaxHp { get; set; }
        [Range(0, 1_000_000)] public int MaxMp { get; set; }
        /// <summary>Points per second (<c>StatDef</c> <c>hpRegen</c>).</summary>
        [Range(0, 100_000)] public int HpRegen { get; set; }
        [Range(0, 100_000)] public int MpRegen { get; set; }
        [Range(0, 1_000_000)] public int Atk { get; set; }
        [Range(0, 1_000_000)] public int Matk { get; set; }
        [Range(0, 1_000_000)] public int Def { get; set; }
        [Range(0, 1_000_000)] public int Mdef { get; set; }

        /// <summary>R-3.3-01: size of the poise bar (spec 12.7: 100 for players, <c>StartingValuesM1.playerPoise</c>).</summary>
        [Range(1, 10_000)] public int PoiseMax { get; set; }

        /// <summary>R-3.3-07: a hit flinches when its <c>flinchPower</c> is at least this.</summary>
        [Range(0, 10_000)] public int FlinchResist { get; set; }

        /// <summary>R-3.3-07: a flinch lasts the hit's <c>flinchMs</c> times this / 100.</summary>
        [Range(0, 1_000)] public int FlinchDurationPercent { get; set; }

        /// <summary>R-3.3-07: after a flinch, further flinches are ignored this long (no stunlock).</summary>
        [Range(0, 60_000)] public int FlinchImmunityMs { get; set; }

        /// <summary>The body circle hit shapes test against (R-3.1-03, spec 3.1: a target is hit when its circle touches a shape).</summary>
        [Range(1, MaxBodyRadiusCm)] public int BodyRadiusCm { get; set; }

        /// <summary>Sanity bounds of the attributes at level 1 and of their growth per level (hundredths).</summary>
        public const int MaxAttribute = 10_000;
        public const int MaxAttributeGrowth = 100_000;

        /// <summary>R-3.4-01: STR at level 1, whole points (WP-M2-2).</summary>
        [Range(0, MaxAttribute)] public int Str { get; set; }
        [Range(0, MaxAttribute)] public int Dex { get; set; }
        [Range(0, MaxAttribute)] public int Sta { get; set; }
        [Range(0, MaxAttribute)] public int Int { get; set; }

        /// <summary>STR gained per level after the first, in hundredths of a point (250 = 2.5 per level; the total is rounded down).</summary>
        [Range(0, MaxAttributeGrowth)] public int StrGrowth { get; set; }
        [Range(0, MaxAttributeGrowth)] public int DexGrowth { get; set; }
        [Range(0, MaxAttributeGrowth)] public int StaGrowth { get; set; }
        [Range(0, MaxAttributeGrowth)] public int IntGrowth { get; set; }

        /// <summary>The highest class tier (R-3.5-01: roots 0, then the class changes at levels 30, 50 and 80).</summary>
        public const int MaxTier = 3;

        /// <summary>
        /// R-3.5-01, R-3.5-02 (WP-M2-5): the class this one advances from; empty for a root class. A class line is the
        /// chain from the root to a class. Generated from <c>docs/design/skill-trees.json</c> (<c>SkillTreeGen</c>).
        /// </summary>
        [Optional, Ref(typeof(ClassDef), Optional = true)] public string Parent { get; set; } = string.Empty;

        /// <summary>0 for a root, the parent's tier plus one otherwise.</summary>
        [Optional, Range(0, MaxTier)] public int Tier { get; set; }

        /// <summary>R-3.5-01: the character level a character of the parent class may advance into this one at (roots 1).</summary>
        [Optional, Range(1, 10_000)] public int UnlockLevel { get; set; } = 1;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!Enum.IsDefined(typeof(ArmorClass), ArmorClass))
                errors.Add(nameof(ArmorClass), "unknown armor class");
            if (Parent.Length == 0 && Tier != 0)
                errors.Add(nameof(Tier), "a root class (no parent) has tier 0");
            if (Parent.Length != 0 && Tier == 0)
                errors.Add(nameof(Tier), "a class with a parent has tier 1 or more");
            if (Parent == Id)
                errors.Add(nameof(Parent), "a class cannot advance from itself");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            NetIds.CheckUnique<ClassDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);
            // Backlog E, spec 9.6: each class line has its normal attack, dodge and parry and its default hotbar layout:
            // a root's own ActionBindings row, which the later classes of its line use unless they have one (WP-M2-5).
            if (set.TryGet(typeof(ActionBindings), out _) && ClassLines.BindingsFor(set, Id) == null)
                errors.Add(nameof(Id), $"class '{Id}' has no ActionBindings row on its class line (spec 9.6)");
            var parent = Parent.Length != 0 ? Find(set, Parent) : null;
            if (parent != null)
            {
                if (Tier != parent.Tier + 1)
                    errors.Add(nameof(Tier), $"is {Tier}, the parent '{Parent}' has tier {parent.Tier} (R-3.5-02)");
                if (UnlockLevel <= parent.UnlockLevel)
                    errors.Add(nameof(UnlockLevel), $"{UnlockLevel} is not above the parent's {parent.UnlockLevel} (R-3.5-01)");
            }
        }

        public static ClassDef? Find(ConfigSet set, string id) => NetIds.Find<ClassDef>(set, id, row => row.Id);
    }

    /// <summary>Helpers for the <c>NetId</c> columns of the definition tables.</summary>
    internal static class NetIds
    {
        public static void CheckUnique<T>(ConfigSet set, T self, int netId, Func<T, int> netIdOf, Func<T, string> idOf,
            RowErrors errors) where T : class
        {
            if (!set.TryGet(typeof(T), out var table))
                return;
            foreach (var row in table.Rows)
            {
                var other = (T)row;
                if (!ReferenceEquals(other, self) && netIdOf(other) == netId)
                {
                    errors.Add("NetId", $"{netId} is also used by '{idOf(other)}'");
                    return;
                }
            }
        }

        public static T? Find<T>(ConfigSet set, string id, Func<T, string> idOf) where T : class
        {
            if (!set.TryGet(typeof(T), out var table))
                return null;
            foreach (var row in table.Rows)
            {
                if (string.Equals(idOf((T)row), id, StringComparison.Ordinal))
                    return (T)row;
            }
            return null;
        }

        public static List<T> Rows<T>(ConfigSet set) where T : class
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
