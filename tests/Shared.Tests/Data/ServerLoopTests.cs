using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>Spec 1.6 input catch-up (fix/parry-feel): a tick cannot apply more frames than the queue holds.</summary>
public class ServerLoopTests
{
    private static RowErrors Validate(ServerLoop row)
    {
        var errors = new RowErrors();
        row.Validate(errors);
        return errors;
    }

    private static ServerLoop Valid() => new()
    {
        PlayerSpawnMarkerId = "m1.spawn.city", MaxQueuedInputFrames = 4, InputBacklogTicks = 10, MaxInputFramesPerTick = 2,
        NetPollIntervalMs = 1, SpeedTolerancePercent = 10,
    };

    [Fact]
    public void The_m1_values_are_valid() => Assert.Empty(Validate(Valid()).Items);

    [Fact]
    public void More_frames_per_tick_than_the_queue_holds_is_a_broken_rule()
    {
        var row = Valid();
        row.MaxInputFramesPerTick = 5;

        Assert.Equal(nameof(ServerLoop.MaxInputFramesPerTick), Assert.Single(Validate(row).Items).Field);
    }
}
