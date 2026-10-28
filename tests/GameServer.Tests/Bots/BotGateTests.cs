using BotClient;
using GameServer.Tests.Support;
using Shared.Data.Tables;

namespace GameServer.Tests.Bots;

/// <summary>
/// The bots and the tutorial gate (R-12.3-01, WP-M2-10) with the committed tables and world: below level 10 no bot is sent to
/// a camp behind the gate (rings 2 to 4), from level 10 the m2-loop homes move on as before.
/// </summary>
public class BotGateTests
{
    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    private static string RegionAt(Shared.Simulation.FixVec2 position)
    {
        var regionIndex = Data.Value.Grid.RegionIndexAt(position);
        Assert.True(Shared.World.WorldPackage.TryLoadIndex(
            name => File.Exists(Path.Combine(RepositoryPaths.M1World(), name)) ? File.ReadAllBytes(Path.Combine(RepositoryPaths.M1World(), name)) : null,
            out var index, out _));
        return index!.Regions.Single(r => r.Index == regionIndex).Id;
    }

    [Theory]
    [InlineData("m2-loop", 1)]
    [InlineData("m2-loop", 9)]
    [InlineData("field-zone", 1)]
    [InlineData("field-zone", 9)]
    public void Below_level_10_every_bot_home_stays_on_the_tutorial_side(string behaviourId, int level)
    {
        var behaviour = Data.Value.Behaviour(behaviourId);
        for (int index = 0; index < 50; index++)
        {
            var home = Data.Value.HomeFor(behaviour, index, level);
            Assert.True(Data.Value.CanEnter(home, level), $"bot {index} at level {level} is sent behind the gate");
            Assert.DoesNotContain(RegionAt(home), new[] { "field1.ring2", "field1.ring3", "field1.ring4" });
        }
    }

    [Fact]
    public void At_level_10_the_loop_bots_go_to_the_spitters_behind_the_gate()
    {
        var behaviour = Data.Value.Behaviour("m2-loop");
        Assert.True(behaviour.HomesByLevel);

        var home = Data.Value.HomeFor(behaviour, 0, 12);

        Assert.Equal("field1.ring2", RegionAt(home));
        Assert.True(Data.Value.CanEnter(home, 12));
        Assert.False(Data.Value.CanEnter(home, 9));
        Assert.True(Data.Value.Gates.Any);
    }
}
