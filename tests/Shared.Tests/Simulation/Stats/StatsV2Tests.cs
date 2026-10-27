using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Stats;
using static Shared.Tests.Simulation.Stats.StatsTestConfig;

namespace Shared.Tests.Simulation.Stats;

/// <summary>
/// Stats v2 (spec 3.4 "M2 primary attributes", R-3.4-01/02, WP-M2-2): attributes from the class and the level, derived
/// stats from the linear <c>AttributeFormula</c>, gear and status modifiers on top, caps from <c>StatDef</c>. Test-local
/// tables with the placeholder shape (they mirror the shipped numbers so the expectations below are hand-computed once;
/// retuning data/tables does not change them).
/// </summary>
public class StatsV2Tests
{
    private const string CooldownReduction = "cooldownReductionPercent";
    private const string MoveSpeed = "moveSpeedPercent";

    private readonly CombatConfig _config = Build(Set());
    private CombatStatIds Ids => _config.Ids;

    private static ConfigSet Set(List<AttributeFormula>? formula = null)
    {
        var stats = StatRows();
        stats.Add(new StatDef { Id = CooldownReduction, Min = 0, Max = 40 });
        stats.Add(new StatDef { Id = MoveSpeed, Min = -90, Max = 100 });
        var set = StatsTestConfig.Set(stats);
        Add(set, formula ?? Formula());
        return set;
    }

    /// <summary>The placeholder shape: Atk from STR and a little DEX, Matk from INT, Def from STA and STR, MDef from STA and INT, HP from STA, MP from INT, crit from DEX.</summary>
    private static List<AttributeFormula> Formula() => new()
    {
        new() { Id = "atk", Str = 100, Dex = 25 },
        new() { Id = "matk", Int = 100 },
        new() { Id = "def", Str = 25, Sta = 50 },
        new() { Id = "mdef", Sta = 50, Int = 25 },
        new() { Id = "maxHp", Sta = 500 },
        new() { Id = "maxMp", Int = 300 },
        new() { Id = "critRate", Dex = 5 },
    };

    private static readonly Dictionary<string, ClassDef> Classes = new[]
    {
        Class("warrior", 100, 24, 9, 3, 8, 3, 10, 5, 10, 2, 300, 100, 250, 50),
        Class("archer", 80, 31, 10, 3, 4, 5, 6, 10, 6, 3, 150, 300, 150, 75),
        Class("mage", 65, 64, 2, 10, 3, 9, 2, 4, 5, 12, 50, 100, 125, 325),
        Class("assist", 80, 53, 4, 7, 5, 6, 5, 4, 8, 9, 100, 100, 200, 225),
    }.ToDictionary(c => c.Id);

    private static ClassDef Class(string id, int hp, int mp, int atk, int matk, int def, int mdef,
        int str, int dex, int sta, int intelligence, int strGrowth, int dexGrowth, int staGrowth, int intGrowth) => new()
    {
        Id = id, MaxHp = hp, MaxMp = mp, Atk = atk, Matk = matk, Def = def, Mdef = mdef,
        Str = str, Dex = dex, Sta = sta, Int = intelligence,
        StrGrowth = strGrowth, DexGrowth = dexGrowth, StaGrowth = staGrowth, IntGrowth = intGrowth,
    };

    /// <summary>A character's stat block the way <c>CombatRules.ForPlayer</c> builds it: class bases, then the attributes at the level.</summary>
    private StatBlock Character(ClassDef c, int level)
    {
        var block = new StatBlock(_config.Stats, _config.Attributes);
        block.SetBase(Ids.MaxHp, c.MaxHp);
        block.SetBase(Ids.MaxMp, c.MaxMp);
        block.SetBase(Ids.Atk, c.Atk);
        block.SetBase(Ids.Matk, c.Matk);
        block.SetBase(Ids.Def, c.Def);
        block.SetBase(Ids.Mdef, c.Mdef);
        Attributes.SetBases(block, _config.Attributes, c, level);
        return block;
    }

    private int Stat(string id) => _config.Stats.IdFor(id);

    // ---- Attributes at a level ----

    [Theory]
    [InlineData(10, 300, 1, 10)]
    [InlineData(10, 300, 2, 13)]
    [InlineData(10, 250, 2, 12)]   // 2.5 per level: floor of the total, not per level
    [InlineData(10, 250, 3, 15)]
    [InlineData(10, 250, 30, 82)]
    [InlineData(10, 250, 0, 10)]   // below level 1 counts as 1
    [InlineData(10, 0, 50, 10)]
    public void An_attribute_grows_by_its_hundredths_per_level_rounded_down_in_total(int atLevel1, int growth, int level, int expected) =>
        Assert.Equal(expected, Attributes.AtLevel(atLevel1, growth, level));

    // ---- The grid: each class at levels 1, 15, 30; bare, with gear, with gear and a status ----

    [Theory]
    // class, level, STR DEX STA INT, Atk Matk Def MDef maxHp maxMp, Atk and Def with the gear, Def with gear and Weakened
    [InlineData("warrior", 1, 10, 5, 10, 2, 20, 5, 15, 8, 150, 30, 42, 16, 12)]
    [InlineData("warrior", 15, 52, 19, 45, 9, 65, 12, 43, 27, 325, 51, 96, 44, 35)]
    [InlineData("warrior", 30, 97, 34, 82, 16, 114, 19, 73, 48, 510, 72, 154, 74, 59)]
    [InlineData("archer", 1, 6, 10, 6, 3, 18, 6, 8, 8, 110, 40, 39, 9, 7)]
    [InlineData("archer", 15, 27, 52, 27, 13, 50, 16, 24, 21, 215, 70, 78, 25, 20)]
    [InlineData("archer", 30, 49, 97, 49, 24, 83, 27, 40, 35, 325, 103, 117, 42, 33)]
    [InlineData("mage", 1, 2, 4, 5, 12, 5, 22, 6, 14, 90, 100, 24, 7, 5)]
    [InlineData("mage", 15, 9, 18, 22, 57, 15, 67, 16, 34, 175, 235, 36, 17, 13)]
    [InlineData("mage", 30, 16, 33, 41, 106, 26, 116, 27, 56, 270, 382, 49, 28, 22)]
    [InlineData("assist", 1, 5, 4, 8, 9, 10, 16, 10, 12, 120, 80, 30, 11, 8)]
    [InlineData("assist", 15, 19, 18, 36, 40, 27, 47, 27, 34, 260, 173, 50, 29, 23)]
    [InlineData("assist", 30, 34, 33, 66, 74, 46, 81, 46, 57, 410, 275, 73, 47, 37)]
    public void Each_class_gets_its_stats_from_class_level_gear_and_status(string classId, int level,
        int str, int dex, int sta, int intelligence, int atk, int matk, int def, int mdef, int maxHp, int maxMp,
        int gearAtk, int gearDef, int weakenedDef)
    {
        var block = Character(Classes[classId], level);
        Assert.Equal(new[] { str, dex, sta, intelligence },
            new[] { block.Get(Stat("str")), block.Get(Stat("dex")), block.Get(Stat("sta")), block.Get(Stat("int")) });
        Assert.Equal(new[] { atk, matk, def, mdef, maxHp, maxMp },
            new[] { block.Get(Ids.Atk), block.Get(Ids.Matk), block.Get(Ids.Def), block.Get(Ids.Mdef), block.Get(Ids.MaxHp), block.Get(Ids.MaxMp) });

        // Gear (source type gear): +5 STR feeds Atk and Def through the formula, then +10 flat and +20% Atk.
        block.Add(new StatModifier(Stat("str"), 5, 0, StatSourceType.Gear, 1));
        block.Add(new StatModifier(Ids.Atk, 10, 0, StatSourceType.Gear, 2));
        block.Add(new StatModifier(Ids.Atk, 0, 20, StatSourceType.Gear, 2));
        Assert.Equal(str + 5, block.Get(Stat("str")));
        Assert.Equal(gearAtk, block.Get(Ids.Atk));
        Assert.Equal(gearDef, block.Get(Ids.Def));

        // A status on top (Weakened, Def -20%, spec 3.7), then gone again.
        block.Add(new StatModifier(Ids.Def, 0, -20, StatSourceType.Debuff, 99));
        Assert.Equal(weakenedDef, block.Get(Ids.Def));
        Assert.Equal(gearAtk, block.Get(Ids.Atk));
        block.RemoveSource(StatSourceType.Debuff, 99);
        Assert.Equal(gearDef, block.Get(Ids.Def));

        // Unequipping removes every gear modifier at once and the class-and-level values come back.
        Assert.Equal(3, block.RemoveSourceType(StatSourceType.Gear));
        Assert.Equal(atk, block.Get(Ids.Atk));
        Assert.Equal(def, block.Get(Ids.Def));
    }

    [Fact]
    public void Crit_rate_comes_from_DEX_and_stays_0_below_20_DEX()
    {
        var block = Character(Classes["archer"], 1); // DEX 10
        Assert.Equal(0, block.Get(Ids.CritRate));
        block.SetBase(Stat("dex"), 97);
        Assert.Equal(4, block.Get(Ids.CritRate)); // floor(97 x 5 / 100)
    }

    // ---- Caps (architecture rule 2) ----

    [Fact]
    public void Percent_stats_are_capped_by_their_StatDef_bounds()
    {
        var block = Character(Classes["warrior"], 1);
        block.Add(new StatModifier(Stat(CooldownReduction), 60, 0, StatSourceType.Gear, 1));
        Assert.Equal(40, block.Get(Stat(CooldownReduction))); // R-3.4-02: cooldown reduction capped
        block.Add(new StatModifier(Stat(MoveSpeed), -500, 0, StatSourceType.Debuff, 2));
        Assert.Equal(-90, block.Get(Stat(MoveSpeed)));
        block.Add(new StatModifier(Stat(MoveSpeed), 900, 0, StatSourceType.Buff, 3));
        Assert.Equal(100, block.Get(Stat(MoveSpeed)));
    }

    [Fact]
    public void An_attribute_is_clamped_before_it_feeds_the_derived_stats()
    {
        var block = Character(Classes["warrior"], 1);
        block.Add(new StatModifier(Stat("str"), 5_000, 0, StatSourceType.Buff, 1)); // test StatDef: str at most 1000
        Assert.Equal(1_000, block.Get(Stat("str")));
        Assert.Equal(9 + (1_000 * 100 + 5 * 25) / 100, block.Get(Ids.Atk));
    }

    [Fact]
    public void A_negative_share_is_floored_toward_negative_infinity_and_the_final_value_clamped()
    {
        var config = Build(Set(new List<AttributeFormula> { new() { Id = "atk", Str = -150 } }));
        var block = new StatBlock(config.Stats, config.Attributes);
        block.SetBase(config.Ids.Atk, 10);
        block.SetBase(config.Stats.IdFor("str"), 3);
        Assert.Equal(5, block.Get(config.Ids.Atk)); // 10 + floor(-4.5) = 5
        block.SetBase(config.Stats.IdFor("str"), 100);
        Assert.Equal(0, block.Get(config.Ids.Atk)); // StatDef min 0
    }

    // ---- Recompute only on change ----

    [Fact]
    public void The_block_recomputes_only_after_a_source_changed()
    {
        var block = Character(Classes["mage"], 15);
        block.Get(Ids.Matk);
        int recomputes = block.RecomputeCount;
        int version = block.Version;
        block.Get(Ids.Matk);
        block.Get(Ids.Atk);
        Assert.False(block.Refresh());
        Assert.Equal(recomputes, block.RecomputeCount);

        Attributes.SetBases(block, _config.Attributes, Classes["mage"], 15); // the same level again: nothing changes
        Assert.False(block.Refresh());
        Assert.Equal(recomputes, block.RecomputeCount);

        Attributes.SetBases(block, _config.Attributes, Classes["mage"], 16); // a level up
        Assert.True(block.Refresh());
        Assert.Equal(recomputes + 1, block.RecomputeCount);
        Assert.Equal(version + 1, block.Version);
        Assert.Equal(10 + 60, block.Get(Ids.Matk)); // INT 12 + floor(325 x 15 / 100) = 60
    }

    [Fact]
    public void A_block_without_the_formula_keeps_flat_stats_like_a_monster()
    {
        var monster = new StatBlock(_config.Stats);
        monster.SetBase(Ids.Atk, 30);
        monster.SetBase(Stat("str"), 500); // a status on a monster's STR changes nothing else
        Assert.Equal(30, monster.Get(Ids.Atk));
        Assert.Null(monster.Formula);
    }

    // ---- AttributeFormula cross-table rules ----

    [Fact]
    public void A_formula_row_on_an_attribute_is_refused()
    {
        var errors = new List<ValidationError>();
        Assert.Null(CombatConfig.TryBuild(Set(new List<AttributeFormula> { new() { Id = "str", Dex = 10 } }), errors));
        var error = Assert.Single(errors);
        Assert.Equal(ValidationCodes.RuleBroken, error.Code);
        Assert.Contains("primary attribute", error.Message);
    }

    [Fact]
    public void A_formula_row_on_an_unknown_stat_is_refused()
    {
        var errors = new List<ValidationError>();
        Assert.Null(CombatConfig.TryBuild(Set(new List<AttributeFormula> { new() { Id = "luck", Dex = 10 } }), errors));
        Assert.Equal(ValidationCodes.BrokenRef, Assert.Single(errors).Code);
    }

    [Fact]
    public void A_missing_attribute_stat_is_refused()
    {
        var stats = StatRows().Where(s => s.Id != "dex").ToList();
        var errors = new List<ValidationError>();
        Assert.Null(CombatConfig.TryBuild(StatsTestConfig.Set(stats), errors));
        Assert.Contains(errors, e => e.Message.Contains("'dex'"));
    }

    [Fact]
    public void Without_the_table_the_formula_has_no_rows()
    {
        var config = Build(StatsTestConfig.Set());
        Assert.False(config.Attributes.HasRow(config.Ids.Atk));
        Assert.Equal(0, config.Attributes.Contribution(config.Ids.Atk, 100, 100, 100, 100));
    }
}
