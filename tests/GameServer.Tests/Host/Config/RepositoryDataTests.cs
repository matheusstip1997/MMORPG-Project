using GameServer.Host.Config;
using Shared.Data;
using Shared.Data.Tables;

namespace GameServer.Tests.Host.Config;

/// <summary>The tables committed in data/tables must always be valid.</summary>
public class RepositoryDataTests
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
    public void Committed_tables_are_valid()
    {
        var result = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All);

        Assert.True(result.Ok, string.Join("\n", result.Errors));
    }

    [Fact]
    public void StartingValuesM1_matches_spec_12_7_tick_rate()
    {
        var values = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set.Single<StartingValuesM1>();

        // Architecture rule 12: 20 Hz tick, lag compensation capped at 200 ms.
        Assert.Equal(20, values.TickRateHz);
        Assert.Equal(200, values.LagCompensationCapMs);
    }
}
