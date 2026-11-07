using GameServer.Combat;
using Shared.Simulation;

namespace GameServer.Tests.Combat;

/// <summary>
/// The lock aim's view clock (M3 decision 16, R-3.1-03). #154's review noted "the newest tick is not interpolated at a round
/// trip under one tick": it is. Phase 2 records every entity before phase 3 reads it, so a view inside the last tick lies
/// between the tick before and the newest one, and only a view of the newest tick itself (lag 0) reads it exactly.
/// </summary>
public class PositionHistoryViewTests
{
    private const int SubTicks = 64;

    private static PositionHistory History()
    {
        var history = new PositionHistory(4);
        history.Record(10, FixVec2.FromCm(0, 0));
        history.Record(11, FixVec2.FromCm(100, 0));
        return history;
    }

    [Theory]
    [InlineData(32, 50)] // half a tick of lag: halfway from tick 10 to the newest tick 11
    [InlineData(16, 75)]
    [InlineData(48, 25)]
    [InlineData(1, 98)]
    public void A_view_inside_the_last_tick_interpolates_toward_the_newest_tick(int lag64, int expectedCm)
    {
        Assert.True(History().TryGetAtView64(11 * SubTicks - lag64, out var seen));
        Assert.InRange(FixedPoint.ToCmRound(seen.X), expectedCm - 1, expectedCm + 1);
    }

    [Fact]
    public void A_view_of_the_newest_tick_itself_reads_it_exactly()
    {
        Assert.True(History().TryGetAtView64(11 * SubTicks, out var seen));
        Assert.Equal(FixVec2.FromCm(100, 0), seen);
    }
}
