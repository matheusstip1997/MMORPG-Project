using System.Text.Json;
using GameServer.Host.Config;
using Shared.Data;

namespace GameServer.Tests.Host.Config;

/// <summary>
/// The Unity client reads data/tables with the portable <see cref="ConfigTableReader"/>; the server with
/// <see cref="ConfigLoader"/> (System.Text.Json). Both must give the same rows for every committed table, so client
/// prediction runs on the server's values (spec 1.8, 2.2).
/// </summary>
public class ConfigReaderParityTests
{
    private static string TablesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "data", "tables");
    }

    [Fact]
    public void Both_readers_load_the_same_rows_from_every_committed_table()
    {
        var directory = TablesDirectory();
        var server = ConfigLoader.LoadAndValidate(directory, ConfigTables.All);
        Assert.True(server.Ok, string.Join("\n", server.Errors));

        var clientErrors = new List<ValidationError>();
        var client = ConfigTableReader.LoadAndValidate(ConfigTables.All, name =>
        {
            var path = Path.Combine(directory, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, clientErrors);
        Assert.Empty(clientErrors);

        foreach (var rowType in ConfigTables.All)
        {
            Assert.True(server.Set.TryGet(rowType, out var expected));
            Assert.True(client.TryGet(rowType, out var actual));
            Assert.Equal(expected.Version, actual.Version);
            Assert.Equal(expected.Rows.Count, actual.Rows.Count);
            for (int row = 0; row < expected.Rows.Count; row++)
            {
                foreach (var field in TableInfo.For(rowType).Fields)
                    Assert.True(Equals(field.GetValue(expected.Rows[row]), field.GetValue(actual.Rows[row])),
                        $"{rowType.Name} row {row} field {field.Name} differs");
            }
        }
    }

    [Fact]
    public void Field_and_enum_names_are_camelCased_like_System_Text_Json()
    {
        foreach (var rowType in ConfigTables.All)
        {
            foreach (var field in TableInfo.For(rowType).Fields)
            {
                Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(field.Name), ConfigTableReader.CamelCase(field.Name));
                if (!field.PropertyType.IsEnum)
                    continue;
                foreach (var name in Enum.GetNames(field.PropertyType))
                    Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(name), ConfigTableReader.CamelCase(name));
            }
        }
    }
}
