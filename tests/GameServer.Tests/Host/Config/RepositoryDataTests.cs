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
    public void Every_initial_class_is_a_ClassDef_row_with_a_visual()
    {
        var set = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set;
        Assert.True(set.TryGet(typeof(EntityVisualDef), out var visuals));
        var ids = visuals.Rows.Cast<EntityVisualDef>().Select(v => v.Id).ToHashSet();

        foreach (var classId in set.Single<CharacterCreationConfig>().AllowedInitialClasses)
        {
            var classDef = ClassDef.Find(set, classId);
            Assert.NotNull(classDef);
            Assert.Contains(classDef.VisualId, ids);
        }
    }

    [Fact]
    public void The_committed_world_has_every_marker_the_tables_name()
    {
        // Spec 11.6: marker ids are checked against the config tables (player spawn, SpawnPoints).
        var set = ConfigLoader.LoadAndValidate(TablesDirectory(), ConfigTables.All).Set;
        var world = GameServer.World.Map.WorldLoader.Load(Path.Combine(TablesDirectory(), "..", "world", "m1"), set);

        Assert.True(world.Ok, string.Join("\n", world.Errors));
        Assert.Empty(GameServer.World.Map.WorldLoader.CheckConfigMarkers(world.Map!, set));
        Assert.True(set.TryGet(typeof(SpawnPoints), out var points));
        Assert.NotEmpty(points.Rows);
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
