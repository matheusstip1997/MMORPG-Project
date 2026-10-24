using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Stats;
using static Shared.Tests.Simulation.Stats.StatsTestConfig;

namespace Shared.Tests.Simulation.Stats;

/// <summary>Row rules of the D5 tables and the cross-table rules of <see cref="CombatConfig.TryBuild"/>.</summary>
public class CombatConfigTests
{
    private static List<ValidationError> BuildErrors(ConfigSet set)
    {
        var errors = new List<ValidationError>();
        Assert.Null(CombatConfig.TryBuild(set, errors));
        Assert.NotEmpty(errors);
        return errors;
    }

    [Fact]
    public void A_valid_set_builds_with_the_starting_values_and_constants()
    {
        var config = Build();

        Assert.Equal(5, config.BaseCritChancePercent);
        Assert.Equal(150, config.BaseCritDamagePercent);
        Assert.Equal(1000, config.StaminaRegenDelayMs);
        Assert.Equal(5000, config.HpRegenDelayMs);
        Assert.Equal(3000, config.MpRegenDelayMs);
        Assert.Equal(100, config.MitigationConstant(Rank.F));
        Assert.Equal(800, config.MitigationConstant(Rank.SS));
        Assert.Equal(100, config.RankFactorPercent(Rank.A, Rank.E));
        Assert.True(config.TryGetElement("fire", out int fire));
        Assert.Equal(config.Stats.IdFor("fireRes"), config.ElementResistStat(fire));
        Assert.Equal(Elements.Count, config.ElementCount);
        Assert.True(config.TryGetElement("ice", out int ice));
        Assert.Equal(ice, config.ElementIndex(Element.Ice));
        Assert.Equal(fire, config.ElementIndex(Element.Fire));
        Assert.Equal(DamageCalc.NoElement, config.ElementIndex(Element.None));
        Assert.False(config.TryGetElement("physical", out _));
        Assert.Equal(config.Stats.IdFor(StatIds.MaxStamina), config.Ids.MaxStamina);
    }

    [Fact]
    public void A_missing_rank_pair_is_reported()
    {
        var ranks = RankRows();
        ranks.RemoveAll(r => r.Attacker == Rank.B && r.Defender == Rank.S);

        var error = Assert.Single(BuildErrors(Set(ranks: ranks)));
        Assert.Equal(ValidationCodes.RuleBroken, error.Code);
        Assert.Contains("B vs S", error.Message);
    }

    [Fact]
    public void A_duplicate_rank_pair_is_reported()
    {
        var ranks = RankRows();
        ranks.Add(new RankFactor { Attacker = Rank.C, Defender = Rank.C, FactorPercent = 100 });

        Assert.Equal(ValidationCodes.DuplicateId, Assert.Single(BuildErrors(Set(ranks: ranks))).Code);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(201)]
    public void A_rank_factor_outside_the_damage_constant_bounds_is_reported(int factor)
    {
        var ranks = RankRows((a, d) => a == Rank.E && d == Rank.D ? factor : 100);

        var error = Assert.Single(BuildErrors(Set(ranks: ranks)));
        Assert.Equal(nameof(RankFactor.FactorPercent), error.Field);
    }

    [Fact]
    public void A_missing_required_stat_is_reported()
    {
        var stats = StatRows();
        stats.RemoveAll(s => s.Id == StatIds.CritDamage);

        var error = Assert.Single(BuildErrors(Set(stats: stats)));
        Assert.Equal(ValidationCodes.BrokenRef, error.Code);
        Assert.Contains(StatIds.CritDamage, error.Message);
    }

    [Fact]
    public void An_element_with_an_unknown_resist_stat_is_reported()
    {
        var elements = ElementRows();
        elements[1].ResistStat = "missingRes";

        Assert.Equal(nameof(ElementCaps.ResistStat), Assert.Single(BuildErrors(Set(elements: elements))).Field);
    }

    [Fact]
    public void Bad_mitigation_constants_are_reported_even_when_rows_were_not_validated()
    {
        var damage = Damage();
        damage.MitigationConstantByRank = new[] { 100, 0, 300, 400, 500, 600, 700, 800 };
        Assert.Equal(nameof(DamageConstants.MitigationConstantByRank), Assert.Single(BuildErrors(Set(damage: damage))).Field);

        damage.MitigationConstantByRank = new[] { 100 };
        Assert.Equal(nameof(DamageConstants.MitigationConstantByRank), Assert.Single(BuildErrors(Set(damage: damage))).Field);
    }

    [Fact]
    public void A_missing_table_is_reported()
    {
        var set = new ConfigSet();
        Add(set, StatRows());

        var errors = BuildErrors(set);
        Assert.All(errors, e => Assert.Equal(ValidationCodes.MissingTable, e.Code));
        Assert.Equal(5, errors.Count);
    }

    [Fact]
    public void Duplicate_stat_ids_are_reported_instead_of_thrown()
    {
        var stats = StatRows();
        stats.Add(new StatDef { Id = StatIds.Atk, Min = 0, Max = 1 });

        Assert.Equal(ValidationCodes.RuleBroken, Assert.Single(BuildErrors(Set(stats: stats))).Code);
    }

    // ---- Row rules ----

    private static IReadOnlyList<(string? Field, string Message)> RowErrors(IValidatableRow row)
    {
        var errors = new RowErrors();
        row.Validate(errors);
        return errors.Items;
    }

    [Fact]
    public void Damage_constants_need_one_constant_per_rank_and_bounds_around_100()
    {
        Assert.Empty(RowErrors(Damage()));

        var wrongLength = Damage();
        wrongLength.MitigationConstantByRank = new[] { 100, 200 };
        Assert.Single(RowErrors(wrongLength));

        var outOfRange = Damage();
        outOfRange.MitigationConstantByRank = new[] { 100, 200, 300, 400, 500, 600, 700, 100_001 };
        Assert.Single(RowErrors(outOfRange));

        var excludesNeutral = Damage();
        excludesNeutral.RankFactorMinPercent = 110;
        Assert.Single(RowErrors(excludesNeutral));

        var inverted = Damage();
        inverted.RankFactorMinPercent = 150;
        inverted.RankFactorMaxPercent = 120;
        Assert.Equal(nameof(DamageConstants.RankFactorMinPercent), Assert.Single(RowErrors(inverted)).Field);
    }

    [Fact]
    public void Stat_def_element_caps_and_rank_factor_rows_check_their_fields()
    {
        Assert.Single(RowErrors(new StatDef { Id = "x", Min = 5, Max = 4 }));
        Assert.Single(RowErrors(new StatDef { Id = " ", Min = 0, Max = 0 }));
        Assert.Single(RowErrors(new ElementCaps { Id = "fire", ResistStat = "fireRes", DamageResistMinPercent = 10, DamageResistMaxPercent = 0 }));
        Assert.Single(RowErrors(new RankFactor { Attacker = (Rank)8, Defender = Rank.F, FactorPercent = 100 }));
        Assert.Empty(RowErrors(new RankFactor { Attacker = Rank.SS, Defender = Rank.F, FactorPercent = 100 }));
    }

    [Fact]
    public void Committed_tables_build_a_combat_config()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var tables = Path.Combine(directory!.FullName, "data", "tables");

        var errors = new List<ValidationError>();
        var set = ConfigTableReader.LoadAndValidate(ConfigTables.All, name =>
        {
            var path = Path.Combine(tables, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, errors);
        Assert.Empty(errors);

        var config = CombatConfig.TryBuild(set, errors);
        Assert.True(config != null, string.Join("\n", errors));
        // Spec 12.7 values reach the formula.
        Assert.Equal(5, config!.BaseCritChancePercent);
        Assert.Equal(150, config.BaseCritDamagePercent);
        Assert.Equal(1000, config.StaminaRegenDelayMs);
    }
}
