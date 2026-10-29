using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Stats;

namespace Shared.Tests.Data;

/// <summary>
/// The progression tables of spec 3.6 (WP-M2-3): ProgressionSettings, ExpCurve, ExpBandCurves, RankModifier,
/// RankLevelRanges, AchievementDef, RankRequirements, and the new MonsterDef and CharacterCreationConfig fields.
/// </summary>
public class ProgressionTablesTests
{
    private static readonly AchievementCategory[] Categories =
    {
        AchievementCategory.MonsterKills, AchievementCategory.MonsterTypesKilled, AchievementCategory.BossKills,
        AchievementCategory.RunsClaimed, AchievementCategory.OutpostsVisited,
    };

    private static ConfigSet Set(int maxLevel = 4, IEnumerable<ExpCurve>? curve = null, IEnumerable<RankModifier>? modifiers = null,
        IEnumerable<RankLevelRanges>? ranges = null, IEnumerable<AchievementDef>? achievements = null,
        IEnumerable<RankRequirements>? requirements = null)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ProgressionSettings), 2,
            new object[] { new ProgressionSettings { MaxLevel = maxLevel, SkillPointsPerLevel = 1, AttributePointsPerLevel = 1 } }));
        set.Add(new ConfigTable(typeof(ExpCurve), 1, (curve ?? Curve(maxLevel - 1)).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(ExpBandCurves), 1, new object[]
        {
            new ExpBandCurves { BelowBonusPercentPerLevel = 2, BelowBonusCapPercent = 20, AboveMultiplierPercentPerLevel = 60 },
        }));
        set.Add(new ConfigTable(typeof(RankModifier), 1, (modifiers ?? Modifiers(-2, 50, 75, 100, 125, 150)).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(RankLevelRanges), 1, (ranges ?? AllRanges()).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(AchievementDef), 1, (achievements ?? Achievements()).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(RankRequirements), 1, (requirements ?? Requirements()).Cast<object>().ToList()));
        return set;
    }

    private static IEnumerable<ExpCurve> Curve(int rows) =>
        Enumerable.Range(1, rows).Select(l => new ExpCurve { Level = l, ExpToNext = 100 * l, MonsterBaseExp = 10 * l });

    private static IEnumerable<RankModifier> Modifiers(int first, params int[] percents) =>
        percents.Select((p, i) => new RankModifier { RankDiff = first + i, Percent = p });

    private static IEnumerable<RankLevelRanges> AllRanges() =>
        Enumerable.Range(0, Ranks.Count).Select(r => new RankLevelRanges { Rank = (Rank)r, MinLevel = r * 10, MaxLevel = r * 10 + 30 });

    private static IEnumerable<AchievementDef> Achievements() => Categories.Select(c => new AchievementDef
    {
        Id = c.ToString(),
        Category = c,
        Kind = c is AchievementCategory.MonsterTypesKilled or AchievementCategory.OutpostsVisited ? AchievementKind.Distinct : AchievementKind.Count,
    });

    /// <summary>Two categories for every rank below SS.</summary>
    private static List<RankRequirements> Requirements() => Enumerable.Range(0, Ranks.Count - 1).SelectMany(r => new[]
    {
        new RankRequirements { Id = $"{r}.kills", FromRank = (Rank)r, AchievementId = nameof(AchievementCategory.MonsterKills), Minimum = 10 },
        new RankRequirements { Id = $"{r}.boss", FromRank = (Rank)r, AchievementId = nameof(AchievementCategory.BossKills), Minimum = 1 },
    }).ToList();

    private static List<ValidationError> Validate(ConfigSet set) => TableValidator.Validate(set);

    [Fact]
    public void Valid_tables_pass() => Assert.Empty(Validate(Set()));

    [Fact]
    public void The_committed_tables_pass() => Assert.Empty(Validate(Shared.Tests.Simulation.Progression.CommittedTables.Load()));

    [Fact]
    public void The_curve_must_cover_every_level_below_the_cap_in_order()
    {
        Assert.Contains(Validate(Set(curve: Curve(2))), e => e.Table == "ExpCurve" && e.Field == "Level");
        var outOfOrder = new[] { new ExpCurve { Level = 2, ExpToNext = 1, MonsterBaseExp = 1 }, new ExpCurve { Level = 1, ExpToNext = 2, MonsterBaseExp = 1 },
            new ExpCurve { Level = 3, ExpToNext = 3, MonsterBaseExp = 1 } };
        Assert.Contains(Validate(Set(curve: outOfOrder)), e => e.Table == "ExpCurve" && e.Field == "Level");
    }

    [Fact]
    public void The_curve_never_decreases()
    {
        var curve = new[] { new ExpCurve { Level = 1, ExpToNext = 200, MonsterBaseExp = 1 }, new ExpCurve { Level = 2, ExpToNext = 100, MonsterBaseExp = 1 },
            new ExpCurve { Level = 3, ExpToNext = 300, MonsterBaseExp = 1 } };
        Assert.Contains(Validate(Set(curve: curve)), e => e.Table == "ExpCurve" && e.Field == "ExpToNext");
    }

    [Fact]
    public void Rank_modifiers_are_contiguous_and_the_same_rank_is_x1()
    {
        Assert.Contains(Validate(Set(modifiers: new[] { new RankModifier { RankDiff = -1, Percent = 75 }, new RankModifier { RankDiff = 1, Percent = 125 } })),
            e => e.Table == "RankModifier" && e.Field == "RankDiff");
        Assert.Contains(Validate(Set(modifiers: Modifiers(-1, 75, 90, 125))), e => e.Table == "RankModifier" && e.Field == "Percent");
        Assert.Contains(Validate(Set(modifiers: Modifiers(1, 125, 150))), e => e.Table == "RankModifier" && e.Field == "RankDiff");
    }

    [Fact]
    public void Every_rank_has_exactly_one_level_band()
    {
        Assert.Contains(Validate(Set(ranges: AllRanges().Where(r => r.Rank != Rank.C))), e => e.Table == "RankLevelRanges");
        Assert.Contains(Validate(Set(ranges: AllRanges().Append(new RankLevelRanges { Rank = Rank.F, MinLevel = 0, MaxLevel = 1 }))),
            e => e.Table == "RankLevelRanges");
        Assert.Contains(Validate(Set(ranges: AllRanges().Select(r => r.Rank == Rank.E ? new RankLevelRanges { Rank = Rank.E, MinLevel = 9, MaxLevel = 2 } : r))),
            e => e.Table == "RankLevelRanges" && e.Field == "MinLevel");
    }

    [Fact]
    public void Achievement_kinds_fit_their_category_and_each_category_has_one_counter()
    {
        var wrongKind = Achievements().Select(a => a.Category == AchievementCategory.MonsterKills
            ? new AchievementDef { Id = a.Id, Category = a.Category, Kind = AchievementKind.Distinct } : a);
        Assert.Contains(Validate(Set(achievements: wrongKind)), e => e.Table == "AchievementDef" && e.Field == "Kind");
        var twice = Achievements().Append(new AchievementDef { Id = "more", Category = AchievementCategory.BossKills });
        Assert.Contains(Validate(Set(achievements: twice)), e => e.Table == "AchievementDef" && e.Field == "Category");
    }

    [Fact]
    public void Each_rank_up_needs_two_categories_and_SS_has_none()
    {
        var oneCategory = Requirements().Where(r => !(r.FromRank == Rank.E && r.Id.EndsWith(".boss")));
        Assert.Contains(Validate(Set(requirements: oneCategory)), e => e.Table == "RankRequirements" && e.Message.Contains("from E"));

        var fromSS = Requirements().Append(new RankRequirements { Id = "ss", FromRank = Rank.SS, AchievementId = "MonsterKills", Minimum = 1 });
        Assert.Contains(Validate(Set(requirements: fromSS)), e => e.Table == "RankRequirements" && e.Field == "FromRank");

        var twice = Requirements().Append(new RankRequirements { Id = "f.again", FromRank = Rank.F, AchievementId = "MonsterKills", Minimum = 5 });
        Assert.Contains(Validate(Set(requirements: twice)), e => e.Table == "RankRequirements" && e.Field == "AchievementId");
    }

    [Fact]
    public void A_monster_needs_a_level_within_the_cap_once_progression_is_loaded()
    {
        var set = Set();
        var monster = new MonsterDef
        {
            Id = "m", NetId = 1, VisualId = "", MaxHp = 1, PoiseMax = 1, BodyRadiusCm = 1, MoveSpeedCmPerSec = 1, AggroRangeCm = 1,
            LeashRangeCm = 2,
        };
        set.Add(new ConfigTable(typeof(MonsterDef), 2, new object[] { monster }));
        Assert.Contains(Validate(set), e => e.Table == "MonsterDef" && e.Field == "Level");

        monster.Level = 2;
        monster.RecommendedLevelMin = 3;
        monster.RecommendedLevelMax = 1;
        Assert.Contains(Validate(set), e => e.Table == "MonsterDef" && e.Field == "RecommendedLevelMin");

        monster.RecommendedLevelMax = 9;
        Assert.Contains(Validate(set), e => e.Table == "MonsterDef" && e.Field == "RecommendedLevelMax");

        monster.RecommendedLevelMax = 4;
        Assert.DoesNotContain(Validate(set), e => e.Table == "MonsterDef" && (e.Field ?? "").Contains("Level"));
        Assert.Equal((3, 4), (monster.RecommendedMin(), monster.RecommendedMax()));
    }

    [Fact]
    public void A_new_character_has_one_point_per_starting_level()
    {
        var set = Set();
        var creation = new CharacterCreationConfig { StartLevel = 1, StartSkillPoints = 2 };
        set.Add(new ConfigTable(typeof(CharacterCreationConfig), 2, new object[] { creation }));
        Assert.Contains(Validate(set), e => e.Table == "CharacterCreationConfig" && e.Field == "StartSkillPoints");

        creation.StartSkillPoints = 1;
        Assert.DoesNotContain(Validate(set), e => e.Table == "CharacterCreationConfig" && e.Field == "StartSkillPoints");
    }

    [Fact]
    public void A_new_character_has_one_attribute_point_per_starting_level_above_1()
    {
        // R-3.4-08 (WP-M2-13): free attribute points from level 2, so a level-1 character has none.
        var set = Set();
        var creation = new CharacterCreationConfig { StartLevel = 1, StartSkillPoints = 1, StartAttributePoints = 1 };
        set.Add(new ConfigTable(typeof(CharacterCreationConfig), 2, new object[] { creation }));
        Assert.Contains(Validate(set), e => e.Table == "CharacterCreationConfig" && e.Field == "StartAttributePoints");

        creation.StartAttributePoints = 0;
        Assert.DoesNotContain(Validate(set), e => e.Table == "CharacterCreationConfig" && e.Field == "StartAttributePoints");

        creation.StartLevel = 3;
        creation.StartSkillPoints = 3;
        Assert.Contains(Validate(set), e => e.Table == "CharacterCreationConfig" && e.Field == "StartAttributePoints");
        creation.StartAttributePoints = 2;
        Assert.DoesNotContain(Validate(set), e => e.Table == "CharacterCreationConfig" && e.Field is "StartAttributePoints" or "StartSkillPoints");
    }

    [Fact]
    public void The_committed_settings_give_one_attribute_point_per_level()
    {
        var settings = ProgressionSettings.Find(Shared.Tests.Simulation.Progression.CommittedTables.Load());
        Assert.NotNull(settings);
        Assert.Equal(1, settings!.AttributePointsPerLevel); // user decision 2026-10-29
    }
}
