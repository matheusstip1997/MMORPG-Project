using BotClient;
using BotClient.World;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Simulation;

namespace GameServer.Tests.Bots;

/// <summary>QA wave 6 O3: bots ask only for the drops locked to them (their own kills), from the tables.</summary>
public class OwnLootTests
{
    private static readonly Lazy<BotData> Data = new(() => BotData.Load(RepositoryPaths.Root()));

    [Fact]
    public void A_drop_is_the_bots_own_near_its_own_kill_until_the_loot_lock_ends()
    {
        var tables = Data.Value.Tables;
        int lockMs = tables.Single<StartingValuesM1>().PartyLootLockMs;
        int scatterCm = tables.Single<LootSettings>().DropScatterCm;
        var own = OwnLoot.From(tables);
        var kill = FixVec2.FromCm(10_000, 5_000);

        Assert.False(own.IsOwn(kill, 0)); // no kill yet: another bot's drop
        own.NoteKill(kill, 1_000);
        Assert.True(own.IsOwn(FixVec2.FromCm(10_000 + scatterCm, 5_000), 1_050)); // scattered around the death spot
        Assert.False(own.IsOwn(FixVec2.FromCm(10_000 + scatterCm + 200, 5_000), 1_050)); // another monster's drop
        Assert.True(own.IsOwn(kill, 1_000 + lockMs));
        Assert.False(own.IsOwn(kill, 1_000 + lockMs + 1)); // the lock is over: anyone's, so not asked for

        // Old kills are forgotten as new ones come.
        own.NoteKill(FixVec2.FromCm(0, 0), 2_000 + lockMs);
        Assert.False(own.IsOwn(kill, 2_000 + lockMs));
        Assert.True(own.IsOwn(FixVec2.FromCm(0, 0), 2_000 + lockMs));
    }
}
