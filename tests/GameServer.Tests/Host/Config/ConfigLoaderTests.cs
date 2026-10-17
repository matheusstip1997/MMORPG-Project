using GameServer.Host.Config;
using Shared.Data;

namespace GameServer.Tests.Host.Config;

[Table("LoaderTest", 1)]
public sealed class LoaderTestRow
{
    [TableId] public string Id { get; set; } = "";
    [Range(0, 100)] public int Value { get; set; }
}

public sealed class ConfigLoaderTests : IDisposable
{
    private static readonly Type[] RowTypes = [typeof(LoaderTestRow)];
    private readonly string _directory = Directory.CreateTempSubdirectory("mmorpg-config-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void WriteTable(string json, string name = "LoaderTest") =>
        File.WriteAllText(Path.Combine(_directory, name + ".json"), json);

    private ConfigLoadResult Load() => ConfigLoader.LoadAndValidate(_directory, RowTypes);

    private ValidationError SingleError() => Assert.Single(Load().Errors);

    [Fact]
    public void Valid_file_loads_with_comments_and_trailing_commas()
    {
        WriteTable("""
            { "table": "LoaderTest", "version": 1, "rows": [
                // a comment
                { "id": "a", "value": 5, },
            ] }
            """);

        var result = Load();

        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.True(result.Set.TryGet(typeof(LoaderTestRow), out var table));
        Assert.Equal(5, ((LoaderTestRow)table.Rows[0]).Value);
    }

    [Fact]
    public void Missing_file_is_reported()
    {
        Assert.Equal(ValidationCodes.MissingTable, SingleError().Code);
    }

    [Fact]
    public void File_without_a_registered_row_type_is_reported()
    {
        WriteTable("""{ "table": "LoaderTest", "version": 1, "rows": [] }""");
        WriteTable("{}", "Stray");

        var error = SingleError();

        Assert.Equal(ValidationCodes.UnknownTable, error.Code);
        Assert.Equal("Stray", error.Table);
    }

    [Fact]
    public void Invalid_json_is_reported()
    {
        WriteTable("{ not json");

        Assert.Equal(ValidationCodes.InvalidJson, SingleError().Code);
    }

    [Fact]
    public void Wrong_table_name_in_envelope_is_reported()
    {
        WriteTable("""{ "table": "Other", "version": 1, "rows": [] }""");

        var error = SingleError();

        Assert.Equal(ValidationCodes.BadEnvelope, error.Code);
        Assert.Equal("table", error.Field);
    }

    [Fact]
    public void Missing_field_is_reported()
    {
        WriteTable("""{ "table": "LoaderTest", "version": 1, "rows": [ { "id": "a" } ] }""");

        var error = SingleError();

        Assert.Equal(ValidationCodes.MissingField, error.Code);
        Assert.Equal("value", error.Field);
    }

    [Fact]
    public void Unknown_field_is_reported()
    {
        WriteTable("""{ "table": "LoaderTest", "version": 1, "rows": [ { "id": "a", "value": 1, "valeu": 2 } ] }""");

        var error = SingleError();

        Assert.Equal(ValidationCodes.UnknownField, error.Code);
        Assert.Equal("valeu", error.Field);
    }

    [Fact]
    public void Null_value_is_reported()
    {
        WriteTable("""{ "table": "LoaderTest", "version": 1, "rows": [ { "id": null, "value": 1 } ] }""");

        Assert.Equal(ValidationCodes.NullValue, SingleError().Code);
    }

    [Fact]
    public void Value_of_the_wrong_type_is_reported()
    {
        WriteTable("""{ "table": "LoaderTest", "version": 1, "rows": [ { "id": "a", "value": 1.5 } ] }""");

        Assert.Equal(ValidationCodes.WrongType, SingleError().Code);
    }

    [Fact]
    public void Validator_runs_on_loaded_rows()
    {
        WriteTable("""{ "table": "LoaderTest", "version": 1, "rows": [ { "id": "a", "value": 101 } ] }""");

        Assert.Equal(ValidationCodes.OutOfRange, SingleError().Code);
    }
}
