using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Skills;
using Shared.Tests.Simulation.Progression;
using Shared.Tests.Simulation.Skills;

namespace Shared.Tests.Data;

/// <summary>
/// The skill-tree tables of spec 3.5 (WP-M2-5): SkillTree, SkillEvolution, SkillDef (inheritance), SkillModifierDef,
/// SkillTreeSettings and the class-line fields of ClassDef, with the committed placeholder numbers of the user decision
/// of 2026-10-27 and the validator rules.
/// </summary>
public class SkillTablesTests
{
    private static List<ValidationError> Validate(ConfigSet set) => TableValidator.Validate(set);

    private static List<T> RowsOf<T>(ConfigSet set) => set.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>().ToList() : new List<T>();

    [Fact]
    public void The_committed_trees_follow_the_wave_1_numbers()
    {
        // Max level 5 (weapon unlocks 1), 1 point per level, minimum level 1 + 4 x row (roots) and 30 + 5 x row (tier 1).
        var set = CommittedTables.Load();
        var settings = SkillTreeSettings.Find(set)!;
        Assert.Equal((5, 1, 1, 4, 5, 11), (settings.DefaultMaxLevel, settings.WeaponUnlockMaxLevel, settings.PointCostPerLevel,
            settings.RootMinLevelPerRow, settings.TierMinLevelPerRow, settings.HotbarSlots));
        foreach (var node in SkillTree.Rows(set))
        {
            var cls = ClassDef.Find(set, node.ClassId)!;
            Assert.True(cls.Tier <= 1, $"{node.Id}: only the roots' and tier-1 trees ship in M2");
            Assert.Equal(cls.Tier == 0 ? 1 + 4 * node.Row : 30 + 5 * node.Row, node.MinCharLevel);
            Assert.Equal(node.Kind == SkillNodeKind.WeaponUnlock ? 1 : 5, node.MaxLevel);
            Assert.Equal(1, node.PointCost);
        }
        Assert.Equal(new[] { WeaponType.BigAxe, WeaponType.BigSword }, SkillTree.Find(set, "swordman.swordsmanArt")!.Weapons);
        Assert.Equal(new[] { WeaponType.Spear }, SkillTree.Find(set, "knight.knightTraining")!.Weapons);
        Assert.Equal(SkillNodeKind.Passive, SkillTree.Find(set, "knight.combatTraning")!.Kind); // R-3.5-08: an armor passive
    }

    [Fact]
    public void Only_the_root_trees_and_swordman_and_knight_have_nodes()
    {
        // User decision 2026-10-27: the other six tier-1 classes have rows and empty trees until their nodes are designed.
        var set = CommittedTables.Load();
        var withNodes = SkillTree.Rows(set).Select(n => n.ClassId).Distinct().OrderBy(c => c, StringComparer.Ordinal);
        Assert.Equal(new[] { "archer", "assist", "knight", "mage", "swordman", "warrior" }, withNodes);
    }

    [Fact]
    public void Every_kit_skill_is_a_learnable_root_node_with_its_kit_action()
    {
        var set = CommittedTables.Load();
        var rules = SkillRules.From(set)!;
        foreach (var root in new[] { "warrior", "archer", "mage", "assist" })
        {
            var kit = rules.KitNodes(root);
            Assert.Equal(5, kit.Count);
            var bindings = rules.Bindings(root)!;
            Assert.Equal(bindings.SkillSlots.Where(s => s.Length != 0).OrderBy(s => s), kit.Select(n => rules.Skill(n)!.ActionId).OrderBy(s => s));
        }
    }

    [Fact]
    public void An_evolution_inherits_its_parent_and_overrides_only_what_it_sets()
    {
        // R-3.5-05: the resolved skill takes the parent's action when it sets none (DataValidator --skill prints it).
        var set = CommittedTables.Load();
        var rows = SkillDefResolver.RowsOf(set);
        Assert.True(SkillDefResolver.TryResolve(rows.Skills, rows.Modifiers, "knight.piercingSequence", out var skill, out _));
        Assert.Equal(("skill.warrior.piercingAttack", 10), (skill.ActionId, skill.DamagePercentPerLevel));
        Assert.Equal(new[] { "knight.piercingSequence", "warrior.piercingAttack" }, skill.Chain);
        Assert.Equal(140, skill.LevelPercent(5));

        var kit = SkillTestKit.Set();
        rows = SkillDefResolver.RowsOf(kit);
        Assert.True(SkillDefResolver.TryResolve(rows.Skills, rows.Modifiers, "c.armored", out var armored, out _));
        Assert.Equal(("skill.armored", 20), (armored.ActionId, armored.DamagePercentPerLevel)); // its own values win
    }

    [Fact]
    public void A_missing_parent_or_an_inheritance_loop_does_not_resolve()
    {
        var skills = new Dictionary<string, SkillDef>
        {
            ["a"] = new() { Id = "a", Parent = "b" },
            ["b"] = new() { Id = "b", Parent = "a" },
            ["c"] = new() { Id = "c", Parent = "missing" },
        };
        Assert.False(SkillDefResolver.TryResolve(skills, [], "a", out _, out string loop));
        Assert.Contains("inherits itself", loop);
        Assert.False(SkillDefResolver.TryResolve(skills, [], "c", out _, out string missing));
        Assert.Contains("missing", missing);
    }

    [Fact]
    public void The_kit_tree_is_valid()
    {
        var set = SkillTestKit.Set();
        // The kit leaves out tables the rules never read (ActionDef, StatDef): only the skill tables' own rules are checked.
        var errors = Validate(set).Where(e => e.Table.StartsWith("Skill", StringComparison.Ordinal)
                                                  && !e.Message.Contains("which is not loaded", StringComparison.Ordinal)).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Theory]
    [InlineData("loop")]
    [InlineData("otherTree")]
    [InlineData("levelAboveMax")]
    [InlineData("evolveSameTier")]
    [InlineData("unlockWithoutWeapons")]
    [InlineData("wrongFamily")]
    [InlineData("belowUnlockLevel")]
    public void A_broken_node_fails_validation(string broken)
    {
        var set = SkillTestKit.Set();
        var nodes = SkillTree.Rows(set);
        var cut = nodes.Single(n => n.Id == "r.cut");
        var circle = nodes.Single(n => n.Id == "r.circle");
        switch (broken)
        {
            case "loop":
                cut.Requires = ["r.circle"];
                cut.RequiresLevels = [1];
                break;
            case "otherTree":
                circle.Requires = ["c.spear"];
                circle.RequiresLevels = [1];
                break;
            case "levelAboveMax":
                circle.RequiresLevels = [6];
                break;
            case "evolveSameTier":
                circle.EvolvesFrom = "r.cut";
                circle.Family = "r.cut";
                break;
            case "unlockWithoutWeapons":
                nodes.Single(n => n.Id == "c.spear").Weapons = [];
                break;
            case "wrongFamily":
                nodes.Single(n => n.Id == "c.charge").Family = "c.charge";
                break;
            case "belowUnlockLevel":
                nodes.Single(n => n.Id == "c.charge").MinCharLevel = 29;
                break;
        }
        Assert.Contains(Validate(set), e => e.Table == "SkillTree");
    }

    [Fact]
    public void A_skill_def_must_fit_its_node_kind_and_its_evolution_chain()
    {
        var set = SkillTestKit.Set();
        var defs = RowsOf<SkillDef>(set);
        defs.Single(d => d.Id == "r.circle").ActionId = ""; // an active without an action
        defs.Single(d => d.Id == "c.spear").ArmorClass = ArmorClass.Light; // a weapon unlock with an armor tier
        defs.Single(d => d.Id == "c.lance").Parent = "r.circle"; // not on its evolution chain
        var rows = Validate(set).Where(e => e.Table == "SkillDef").Select(e => defs[e.Row!.Value].Id).ToList();
        Assert.Contains("r.circle", rows);
        Assert.Contains("c.spear", rows);
        Assert.Contains("c.lance", rows);
    }

    [Fact]
    public void A_tier_1_class_needs_its_parent_s_tier_plus_one_and_a_higher_unlock_level()
    {
        var set = SkillTestKit.Set();
        var classes = RowsOf<ClassDef>(set);
        classes.Single(c => c.Id == "c").Tier = 2;
        classes.Single(c => c.Id == "d").UnlockLevel = 1;
        var rows = Validate(set).Where(e => e.Table == "ClassDef").Select(e => classes[e.Row!.Value].Id).ToList();
        Assert.Contains("c", rows);
        Assert.Contains("d", rows);
    }
}
