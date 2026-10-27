using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Actions;
using Shared.Simulation.Skills;

namespace Shared.Tests.Simulation.Skills;

/// <summary>
/// A small skill tree written out in code (WP-M2-5), so table tuning never changes these tests. Root class <c>r</c> (heavy,
/// sword) with tree: <c>r.cut</c> (active, row 0, slot 1 of the kit), <c>r.circle</c> (active, row 1, min level 5, needs
/// <c>r.cut</c> at 2), <c>r.guard</c> (passive, DEF +3 per level). Tier-1 class <c>c</c> (parent r, level 30, sword and
/// spear and shield) with <c>c.charge</c> and <c>c.armored</c> (two evolutions of <c>r.cut</c>: a choice), <c>c.spear</c>
/// (weapon unlock: spear), <c>c.training</c> (passive: medium armor), <c>c.lance</c> (active, needs a spear),
/// <c>c.blank</c> (no SkillDef). A second tier-1 class <c>d</c> of the same root. Actions from <see cref="ActionTestKit.Catalog"/>.
/// </summary>
internal static class SkillTestKit
{
    public static readonly ActionCatalog Catalog = ActionTestKit.Catalog();

    public static SkillRules Rules() => SkillRules.From(Set())!;

    public static ConfigSet Set()
    {
        var set = new ConfigSet();
        set.Add(Table(new SkillTreeSettings
        {
            DefaultMaxLevel = 5, WeaponUnlockMaxLevel = 1, PointCostPerLevel = 1, RootMinLevelPerRow = 4, TierMinLevelPerRow = 5, HotbarSlots = 11,
        }));
        set.Add(Table(
            Class("r", 1, "", 0, 1, ArmorClass.Heavy),
            Class("c", 2, "r", 1, 30, ArmorClass.Heavy),
            Class("d", 3, "r", 1, 30, ArmorClass.Light)));
        set.Add(Table(
            new ClassGearDef { Id = "r", WeaponTypes = [WeaponType.Sword] },
            new ClassGearDef { Id = "c", WeaponTypes = [WeaponType.Sword, WeaponType.Spear, WeaponType.Shield] },
            new ClassGearDef { Id = "d", WeaponTypes = [WeaponType.Bow] }));
        set.Add(Table(new ActionBindings
        {
            ClassId = "r", NormalAttack = "attack", Dodge = "dodge", Parry = "parry", SkillSlots = ["skill.cut", "", "", "", "", "", "skill.circle"],
        }));
        set.Add(Table(
            Node("r.cut", "r", 1, 0, 0, SkillNodeKind.Active),
            Node("r.circle", "r", 5, 1, 1, SkillNodeKind.Active, requires: [("r.cut", 2)]),
            Node("r.guard", "r", 1, 0, 2, SkillNodeKind.Passive),
            Node("c.charge", "c", 30, 0, 0, SkillNodeKind.Active, evolvesFrom: "r.cut"),
            Node("c.armored", "c", 30, 0, 1, SkillNodeKind.Active, evolvesFrom: "r.cut"),
            Node("c.spear", "c", 30, 0, 2, SkillNodeKind.WeaponUnlock, weapons: [WeaponType.Spear], maxLevel: 1),
            Node("c.training", "c", 30, 0, 3, SkillNodeKind.Passive),
            Node("c.lance", "c", 35, 1, 0, SkillNodeKind.Active, requires: [("c.spear", 1)], weapons: [WeaponType.Spear]),
            Node("c.blank", "c", 30, 0, 4, SkillNodeKind.Active),
            Node("d.shot", "d", 30, 0, 0, SkillNodeKind.Active)));
        set.Add(Table(
            new SkillDef { Id = "r.cut", ActionId = "skill.cut", DamagePercentPerLevel = 10 },
            new SkillDef { Id = "r.circle", ActionId = "skill.circle" },
            new SkillDef { Id = "r.guard" },
            new SkillDef { Id = "c.charge", Parent = "r.cut", ActionId = "skill.charge" },
            new SkillDef { Id = "c.armored", Parent = "r.cut", ActionId = "skill.armored", DamagePercentPerLevel = 20 },
            new SkillDef { Id = "c.spear" },
            new SkillDef { Id = "c.training", ArmorClass = ArmorClass.Medium },
            new SkillDef { Id = "c.lance", ActionId = "skill.charge" },
            new SkillDef { Id = "d.shot", ActionId = "skill.cut" }));
        set.Add(Table(new SkillModifierDef { Id = "r.guard.def", SkillId = "r.guard", Stat = "def", FlatPerLevel = 3 }));
        return set;
    }

    public static SkillState Root() => new("r");

    public static SkillState Learned(string classId, params (string Node, int Level)[] nodes)
    {
        var state = new SkillState(classId);
        foreach (var (node, level) in nodes)
            state.SetLevel(node, level);
        return state;
    }

    private static ClassDef Class(string id, int netId, string parent, int tier, int unlock, ArmorClass armor) => new()
    {
        Id = id, NetId = netId, VisualId = "v", ArmorClass = armor, MaxHp = 100, MaxMp = 20, Atk = 10, Def = 5, PoiseMax = 100,
        BodyRadiusCm = 40, Parent = parent, Tier = tier, UnlockLevel = unlock,
    };

    private static SkillTree Node(string id, string classId, int minLevel, int row, int col, SkillNodeKind kind,
        (string Node, int Level)[]? requires = null, string evolvesFrom = "", WeaponType[]? weapons = null, int maxLevel = 5) => new()
    {
        Id = id,
        Family = evolvesFrom.Length == 0 ? id : evolvesFrom,
        ClassId = classId,
        MinCharLevel = minLevel,
        Requires = (requires ?? []).Select(r => r.Node).ToArray(),
        RequiresLevels = (requires ?? []).Select(r => r.Level).ToArray(),
        MaxLevel = maxLevel,
        PointCost = 1,
        Kind = kind,
        EvolvesFrom = evolvesFrom,
        Weapons = weapons ?? [],
        Row = row,
        Col = col,
    };

    private static ConfigTable Table<T>(params T[] rows) where T : class =>
        new(typeof(T), TableInfo.For(typeof(T)).Version, rows.Cast<object>().ToList());
}
