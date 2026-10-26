using GameServer.Host;
using GameServer.Host.Config;
using GameServer.Tests.Support;
using GameServer.World.Map;
using Shared.Data;
using Shared.Simulation;

namespace GameServer.Tests.Host;

/// <summary>QA wave 5 G3 (rule 12): the per-player paths run once at server start, in a world of their own.</summary>
public class WorldWarmupTests
{
    [Fact]
    public void Every_class_enters_a_throwaway_world_on_the_real_tables_and_map()
    {
        var tables = ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All);
        Assert.True(tables.Ok, string.Join("\n", tables.Errors));
        var world = WorldLoader.Load(RepositoryPaths.M1World(), tables.Set);
        Assert.True(world.Ok, string.Join("\n", world.Errors));
        var config = ServerConfig.From(tables.Set);

        int entered = WorldWarmup.Run(config, world.Map!, new SystemClock());

        Assert.Equal(config.Combat!.Classes.Count, entered);
        Assert.True(entered >= 4); // the four M1 root classes
    }
}
