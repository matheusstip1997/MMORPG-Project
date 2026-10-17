using Shared.Data;

namespace Shared.Tests.Data;

public enum ReaderTestMode
{
    SafeZone,
    OpenPvp,
}

[Table("ReaderTest", 1)]
public sealed class ReaderTestRow
{
    [TableId] public string Id { get; set; } = "";
    [Range(0, 100)] public int Value { get; set; }
    public long Big { get; set; }
    public bool Enabled { get; set; }
    public ReaderTestMode Mode { get; set; }
}

[Table("ReaderUnsupported", 1)]
public sealed class ReaderUnsupportedRow
{
    public double Ratio { get; set; }
}

/// <summary>
/// The portable config reader the Unity client uses (spec 1.8). Parity with the server's ConfigLoader on the committed
/// tables is checked in GameServer.Tests (ConfigReaderParityTests).
/// </summary>
public class ConfigTableReaderTests
{
    private const string ValidRow = """{ "id": "a", "value": 5, "big": 5000000000, "enabled": true, "mode": "openPvp" }""";

    private static string Table(string rows, string name = "ReaderTest", int version = 1) =>
        $$"""{ "table": "{{name}}", "version": {{version}}, "rows": [ {{rows}} ] }""";

    private static (ConfigTable? Table, List<ValidationError> Errors) Read(string json, Type? rowType = null)
    {
        var errors = new List<ValidationError>();
        var table = ConfigTableReader.ReadTable(json, rowType ?? typeof(ReaderTestRow), errors);
        return (table, errors);
    }

    private static ValidationError SingleError(string json) => Assert.Single(Read(json).Errors);

    [Fact]
    public void Valid_row_loads_every_supported_field_type()
    {
        var (table, errors) = Read(Table(ValidRow));

        Assert.Empty(errors);
        var row = (ReaderTestRow)Assert.Single(table!.Rows);
        Assert.Equal("a", row.Id);
        Assert.Equal(5, row.Value);
        Assert.Equal(5_000_000_000L, row.Big);
        Assert.True(row.Enabled);
        Assert.Equal(ReaderTestMode.OpenPvp, row.Mode);
        Assert.Equal(1, table.Version);
    }

    [Fact]
    public void Comments_trailing_commas_and_escapes_are_accepted()
    {
        var json = """
            // header comment
            { "table": "ReaderTest", "version": 1, /* inline */ "rows": [
                { "id": "a\"b\\cA", "value": 0, "big": -1, "enabled": false, "mode": "safeZone", },
            ], }
            """;

        var (table, errors) = Read(json);

        Assert.Empty(errors);
        Assert.Equal("a\"b\\cA", ((ReaderTestRow)table!.Rows[0]).Id);
    }

    [Theory]
    [InlineData("{ \"table\": ")]
    [InlineData("{ \"table\": \"ReaderTest\" } extra")]
    [InlineData("{ \"a\": 1, \"a\": 2 }")]
    [InlineData("{ \"a\": 01 }")]
    [InlineData("{ \"a\": 'x' }")]
    [InlineData("{ \"a\": 1 /* open")]
    [InlineData("[,]")]
    public void Malformed_json_is_InvalidJson(string json)
    {
        Assert.Equal(ValidationCodes.InvalidJson, SingleError(json).Code);
    }

    [Fact]
    public void Wrong_table_name_and_unknown_root_field_are_envelope_errors()
    {
        var (table, errors) = Read("""{ "table": "Other", "version": 1, "rows": [], "extra": 0 }""");

        Assert.Null(table);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Equal(ValidationCodes.BadEnvelope, e.Code));
    }

    [Fact]
    public void Fractional_version_is_an_envelope_error()
    {
        Assert.Equal("version", SingleError("""{ "table": "ReaderTest", "version": 1.5, "rows": [] }""").Field);
    }

    [Fact]
    public void Missing_unknown_and_null_fields_are_reported_by_name()
    {
        var (table, errors) = Read(Table("""{ "id": null, "value": 1, "big": 1, "enabled": true, "extra": 1 }"""));

        Assert.Empty(table!.Rows);
        Assert.Contains(errors, e => e.Code == ValidationCodes.NullValue && e.Field == "id");
        Assert.Contains(errors, e => e.Code == ValidationCodes.UnknownField && e.Field == "extra");
        Assert.Contains(errors, e => e.Code == ValidationCodes.MissingField && e.Field == "mode");
    }

    [Theory]
    [InlineData("\"value\": 1.0")]
    [InlineData("\"value\": 1e2")]
    [InlineData("\"value\": \"5\"")]
    [InlineData("\"value\": 3000000000")]
    [InlineData("\"enabled\": 1")]
    [InlineData("\"id\": 7")]
    [InlineData("\"mode\": \"SafeZone\"")]
    [InlineData("\"mode\": \"nowhere\"")]
    [InlineData("\"mode\": 0")]
    public void Wrong_value_types_are_WrongType(string replacement)
    {
        string field = replacement.Substring(1, replacement.IndexOf('"', 1) - 1);
        var members = new Dictionary<string, string>
        {
            ["id"] = "\"id\": \"a\"", ["value"] = "\"value\": 5", ["big"] = "\"big\": 1",
            ["enabled"] = "\"enabled\": true", ["mode"] = "\"mode\": \"safeZone\"",
        };
        members[field] = replacement;

        var error = SingleError(Table("{ " + string.Join(", ", members.Values) + " }"));

        Assert.Equal(ValidationCodes.WrongType, error.Code);
        Assert.Equal(field, error.Field);
        Assert.Equal(0, error.Row);
    }

    [Fact]
    public void Unsupported_field_type_is_a_schema_error()
    {
        var error = Assert.Single(Read(Table("""{ "ratio": 1 }""", "ReaderUnsupported"), typeof(ReaderUnsupportedRow)).Errors);

        Assert.Equal(ValidationCodes.SchemaError, error.Code);
    }

    [Fact]
    public void LoadAndValidate_reports_missing_files_and_runs_the_validator()
    {
        var errors = new List<ValidationError>();

        var set = ConfigTableReader.LoadAndValidate(new[] { typeof(ReaderTestRow), typeof(ReaderUnsupportedRow) },
            name => name == "ReaderTest" ? Table(ValidRow.Replace("\"value\": 5", "\"value\": 500")) : null, errors);

        Assert.True(set.TryGet(typeof(ReaderTestRow), out _));
        Assert.Contains(errors, e => e.Code == ValidationCodes.MissingTable && e.Table == "ReaderUnsupported");
        Assert.Contains(errors, e => e.Code == ValidationCodes.OutOfRange && e.Field == "Value");
    }

    [Theory]
    [InlineData("TickRateHz", "tickRateHz")]
    [InlineData("URLValue", "urlValue")]
    [InlineData("ID", "id")]
    [InlineData("A", "a")]
    [InlineData("already", "already")]
    [InlineData("SafeZone", "safeZone")]
    public void CamelCase_follows_the_System_Text_Json_rule(string name, string expected)
    {
        Assert.Equal(expected, ConfigTableReader.CamelCase(name));
    }
}
