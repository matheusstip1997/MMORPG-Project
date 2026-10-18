using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

[Table("TestClass", 1)]
public sealed class TestClassRow
{
    [TableId] public string Id { get; set; } = "";
    [Range(1, 10)] public int Power { get; set; }
}

[Table("TestSkill", 1)]
public sealed class TestSkillRow
{
    [TableId] public int Id { get; set; }
    [Ref(typeof(TestClassRow))] public string ClassId { get; set; } = "";
    [Ref(typeof(TestClassRow), Optional = true)] public string UpgradeClassId { get; set; } = "";
}

[Table("TestSettings", 2)]
[Singleton]
public sealed class TestSettingsRow : IValidatableRow
{
    public int Min { get; set; }
    public int Max { get; set; }

    public void Validate(RowErrors errors)
    {
        if (Min > Max)
            errors.Add(nameof(Min), "must not be greater than Max");
    }
}

/// <summary>A row with a list of references and a cross-table rule (<see cref="ISetValidatableRow"/>).</summary>
[Table("TestCombo", 1)]
public sealed class TestComboRow : ISetValidatableRow
{
    [TableId] public string Id { get; set; } = "";
    [Ref(typeof(TestClassRow), Optional = true)] public string[] Classes { get; set; } = [];

    public void Validate(ConfigSet set, RowErrors errors)
    {
        if (!set.TryGet(typeof(TestSettingsRow), out var settings))
            return;
        if (Classes.Length > ((TestSettingsRow)settings.Rows[0]).Max)
            errors.Add(nameof(Classes), "more classes than TestSettings.Max");
    }
}

public class TableValidatorTests
{
    [Fact]
    public void List_references_and_cross_table_rules_are_checked()
    {
        var set = ValidSet();
        set.Add(new ConfigTable(typeof(TestComboRow), 1, [new TestComboRow { Id = "a", Classes = ["warrior", "", "mage", "warrior"] }]));

        var errors = TableValidator.Validate(set);

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Code == ValidationCodes.BrokenRef && e.Message.Contains("'mage'"));
        Assert.Contains(errors, e => e.Code == ValidationCodes.RuleBroken && e.Field == nameof(TestComboRow.Classes));
    }

    private static ConfigSet ValidSet()
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(TestClassRow), 1, [new TestClassRow { Id = "warrior", Power = 5 }]));
        set.Add(new ConfigTable(typeof(TestSkillRow), 1, [new TestSkillRow { Id = 1, ClassId = "warrior" }]));
        set.Add(new ConfigTable(typeof(TestSettingsRow), 2, [new TestSettingsRow { Min = 1, Max = 2 }]));
        return set;
    }

    private static ValidationError Single(ConfigSet set) => Assert.Single(TableValidator.Validate(set));

    [Fact]
    public void Valid_set_has_no_errors()
    {
        Assert.Empty(TableValidator.Validate(ValidSet()));
    }

    [Fact]
    public void Value_outside_range_is_reported()
    {
        var set = ValidSet();
        set.Add(new ConfigTable(typeof(TestClassRow), 1, [new TestClassRow { Id = "warrior", Power = 11 }]));

        var error = Single(set);

        Assert.Equal(ValidationCodes.OutOfRange, error.Code);
        Assert.Equal("Power", error.Field);
        Assert.Equal(0, error.Row);
    }

    [Fact]
    public void Duplicate_id_is_reported_on_the_second_row()
    {
        var set = ValidSet();
        set.Add(new ConfigTable(typeof(TestClassRow), 1,
            [new TestClassRow { Id = "warrior", Power = 1 }, new TestClassRow { Id = "warrior", Power = 2 }]));

        var error = Single(set);

        Assert.Equal(ValidationCodes.DuplicateId, error.Code);
        Assert.Equal(1, error.Row);
    }

    [Fact]
    public void Reference_to_missing_row_is_reported()
    {
        var set = ValidSet();
        set.Add(new ConfigTable(typeof(TestSkillRow), 1, [new TestSkillRow { Id = 1, ClassId = "mage" }]));

        var error = Single(set);

        Assert.Equal(ValidationCodes.BrokenRef, error.Code);
        Assert.Equal("ClassId", error.Field);
    }

    [Fact]
    public void Reference_to_table_that_is_not_loaded_is_reported()
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(TestSkillRow), 1, [new TestSkillRow { Id = 1, ClassId = "warrior" }]));

        Assert.Equal(ValidationCodes.BrokenRef, Single(set).Code);
    }

    [Fact]
    public void Empty_optional_reference_is_allowed_but_a_wrong_one_is_not()
    {
        var set = ValidSet();
        set.Add(new ConfigTable(typeof(TestSkillRow), 1,
            [new TestSkillRow { Id = 1, ClassId = "warrior", UpgradeClassId = "" },
             new TestSkillRow { Id = 2, ClassId = "warrior", UpgradeClassId = "paladin" }]));

        var error = Single(set);

        Assert.Equal("UpgradeClassId", error.Field);
        Assert.Equal(1, error.Row);
    }

    [Fact]
    public void File_version_different_from_code_is_reported()
    {
        var set = ValidSet();
        set.Add(new ConfigTable(typeof(TestSettingsRow), 1, [new TestSettingsRow { Min = 1, Max = 2 }]));

        Assert.Equal(ValidationCodes.VersionMismatch, Single(set).Code);
    }

    [Fact]
    public void Singleton_with_two_rows_is_reported()
    {
        var set = ValidSet();
        set.Add(new ConfigTable(typeof(TestSettingsRow), 2, [new TestSettingsRow(), new TestSettingsRow()]));

        Assert.Equal(ValidationCodes.SingletonCount, Single(set).Code);
    }

    [Fact]
    public void Row_rule_is_reported_with_its_field()
    {
        var set = ValidSet();
        set.Add(new ConfigTable(typeof(TestSettingsRow), 2, [new TestSettingsRow { Min = 3, Max = 2 }]));

        var error = Single(set);

        Assert.Equal(ValidationCodes.RuleBroken, error.Code);
        Assert.Equal("Min", error.Field);
    }

    [Fact]
    public void StartingValuesM1_rejects_iframes_longer_than_the_dodge()
    {
        var values = new StartingValuesM1 { DodgeDurationMs = 500, DodgeIFramesMs = 600 };
        var errors = new RowErrors();

        values.Validate(errors);

        Assert.Contains(errors.Items, e => e.Field == nameof(StartingValuesM1.DodgeIFramesMs));
    }

    [Theory]
    [InlineData(20, 20, true)]
    [InlineData(20, 10, true)]
    [InlineData(20, 15, false)]
    [InlineData(20, 40, false)]
    public void StartingValuesM1_snapshot_rate_must_divide_the_tick_rate(int tickRateHz, int snapshotRateHz, bool valid)
    {
        var values = new StartingValuesM1 { TickRateHz = tickRateHz, SnapshotRateHz = snapshotRateHz };
        var errors = new RowErrors();

        values.Validate(errors);

        Assert.Equal(valid, !errors.Items.Any(e => e.Field == nameof(StartingValuesM1.SnapshotRateHz)));
    }
}
