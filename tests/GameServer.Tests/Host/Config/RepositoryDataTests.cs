using GameServer.Host.Config;
using Shared.Data;
using Shared.Data.Tables;
using Shared.World;

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

    [Fact]
    public void Every_initial_class_has_a_visual()
    {
        // Interim binding (EntityVisualDef): a player's visual id is its class id until ClassDef has a visualId (TODO(E)).
        var set = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set;
        Assert.True(set.TryGet(typeof(EntityVisualDef), out var visuals));
        var ids = visuals.Rows.Cast<EntityVisualDef>().Select(v => v.Id).ToHashSet();

        foreach (var classId in set.Single<CharacterCreationConfig>().AllowedInitialClasses)
            Assert.Contains(classId, ids);
    }

    [Theory]
    // Spec 2.3 "Default flags by area type": PvP, honor effects, drops.
    [InlineData("normalCity", PvpMode.Off, true, DeathDrop.Off)]
    [InlineData("lawlessCity", PvpMode.AlwaysOn, false, DeathDrop.On)]
    [InlineData("openWorld", PvpMode.ArmedOnly, true, DeathDrop.On)]
    [InlineData("regularDungeon", PvpMode.Off, false, DeathDrop.Off)]
    [InlineData("raid", PvpMode.Off, false, DeathDrop.Off)]
    [InlineData("arena", PvpMode.AlwaysOn, false, DeathDrop.Off)]
    [InlineData("siege", PvpMode.AlwaysOn, false, DeathDrop.Off)]
    public void RegionDefaults_match_the_spec_2_3_table(string areaType, PvpMode pvp, bool honorEffects, DeathDrop drops)
    {
        var set = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set;
        Assert.True(set.TryGet(typeof(RegionDefaults), out var table));

        var row = Assert.Single(table.Rows.Cast<RegionDefaults>(), r => r.Id == areaType);

        Assert.Equal(new RegionFlags(pvp, honorEffects, drops), row.ToFlags());
    }
}
